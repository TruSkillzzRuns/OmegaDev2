using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using OmegaDev2.Services;
using Windows.Graphics;
using Windows.UI;
using WinRT;
using WinRT.Interop;

namespace OmegaDev2;

/// <summary>
/// Always-on-top, translucent, draggable/resizable overlay showing the live
/// Endless Wave status (same data EndlessChallengePage's "LIVE" tile
/// already polls via ServerApiClient.GetEndlessStatusAsync), so it can sit
/// over the game window instead of only living in the OmegaDev2 app.
/// Windowing mechanics (layered-window alpha, always-on-top, drag handle,
/// position/size persistence) are the same pattern already proven in the
/// DestinyCompanionOverlay-WinUI3 project's OverlayWindow.
/// </summary>
public sealed partial class EndlessOverlayWindow : Window
{
    // Whole-window alpha blend via Win32 layered windows — guarantees
    // see-through regardless of the Windows "Transparency effects" setting,
    // which only gates Fluent Acrylic/Mica blur (the DesktopAcrylicController
    // below); when that setting is off, acrylic flattens to opaque no matter
    // what this app requests. WS_EX_LAYERED + SetLayeredWindowAttributes is
    // a much older per-window alpha mechanism unaffected by that setting.
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const uint LWA_ALPHA = 0x2;
    private const byte OverlayAlpha = 160; // 0 = fully invisible, 255 = fully opaque

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointInt32 point);

    private readonly ServerApiClient _api = new();
    private readonly EndlessOverlayWindowSettingsStore _settingsStore = new();
    private readonly DispatcherQueueTimer _timer;
    private readonly string _targetPlayer;

    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private bool _dragging;
    private PointInt32 _dragStartWindowPosition;
    private PointInt32 _dragStartCursorPosition;
    private bool _pollInFlight;
    private string? _lastPolledState;

    public EndlessOverlayWindow(string targetPlayer)
    {
        _targetPlayer = string.IsNullOrWhiteSpace(targetPlayer) ? "*" : targetPlayer;
        InitializeComponent();

        Closed += EndlessOverlayWindow_Closed;
        Activated += EndlessOverlayWindow_Activated;

        TrySetTranslucentBackdrop();
        ApplyLayeredWindowAlpha();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = true;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        }

        EndlessOverlayWindowSettings? saved = _settingsStore.LoadSync();
        if (saved is not null)
        {
            AppWindow.Resize(new SizeInt32(Math.Max(200, saved.Width), Math.Max(160, saved.Height)));
            AppWindow.Move(new PointInt32(saved.X, saved.Y));
        }
        else
        {
            AppWindow.Resize(new SizeInt32(260, 220));
            PositionTopRight();
        }

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += async (_, _) => await PollStatusAsync();
        _timer.Start();
        _ = PollStatusAsync();
    }

    private void ApplyLayeredWindowAlpha()
    {
        IntPtr hwnd = WindowNative.GetWindowHandle(this);
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
        SetLayeredWindowAttributes(hwnd, 0, OverlayAlpha, LWA_ALPHA);
    }

    /// <summary>
    /// Toggles whole-window click-through via WS_EX_TRANSPARENT. When
    /// enabled, every mouse click (including the drag handle/close button)
    /// passes straight through to the game — can only be turned back off
    /// from the main OmegaDev2 window, not from the overlay itself.
    /// </summary>
    public void SetClickThrough(bool enabled)
    {
        IntPtr hwnd = WindowNative.GetWindowHandle(this);
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle = enabled ? exStyle | WS_EX_TRANSPARENT : exStyle & ~WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
    }

    private void TrySetTranslucentBackdrop()
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
            return;
        }

        _backdropConfiguration = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = SystemBackdropTheme.Dark
        };

        _acrylicController = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Thin,
            TintColor = Color.FromArgb(255, 15, 19, 26),
            TintOpacity = 0.08f,
            LuminosityOpacity = 0.15f,
            FallbackColor = Color.FromArgb(255, 15, 19, 26)
        };
        _acrylicController.AddSystemBackdropTarget(this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
        _acrylicController.SetSystemBackdropConfiguration(_backdropConfiguration);
    }

    private void EndlessOverlayWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        // Always report "active" to the backdrop, even unfocused (the
        // normal state while playing) — otherwise WinUI3's acrylic material
        // flattens to near-opaque for inactive windows, defeating the
        // see-through effect for what is, by design, rarely focused.
        if (_backdropConfiguration is not null)
            _backdropConfiguration.IsInputActive = true;
    }

    private void PositionTopRight()
    {
        DisplayArea? displayArea = DisplayArea.Primary;
        if (displayArea is null) return;

        RectInt32 workArea = displayArea.WorkArea;
        int x = workArea.X + workArea.Width - AppWindow.Size.Width - 24;
        int y = workArea.Y + 24;
        AppWindow.Move(new PointInt32(x, y));
    }

    private void EndlessOverlayWindow_Closed(object sender, WindowEventArgs args)
    {
        _timer.Stop();
        _acrylicController?.Dispose();
        _acrylicController = null;

        EndlessOverlayWindowSettings settingsToSave = new()
        {
            X = AppWindow.Position.X,
            Y = AppWindow.Position.Y,
            Width = AppWindow.Size.Width,
            Height = AppWindow.Size.Height
        };
        _ = _settingsStore.SaveAsync(settingsToSave, CancellationToken.None);
    }

    private async System.Threading.Tasks.Task PollStatusAsync()
    {
        if (_pollInFlight) return;
        _pollInFlight = true;
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.GetEndlessStatusAsync(_targetPlayer);
            var s = resp?.Status;
            if (resp == null || resp.Ok == false || s == null || s.Active == false)
            {
                LiveStateText.Text = "○ not running";
                _lastPolledState = null;
                return;
            }

            string stateText = s.State switch
            {
                "Fighting" => s.Paused ? "PAUSED (fighting)" : "● FIGHT!",
                "Intermission" => s.Paused ? "PAUSED (intermission)" : $"next wave in {Math.Ceiling(s.IntermissionRemainingMs / 1000.0):0}s",
                "WarpingToArena" => "warping to arena…",
                "SettlingArena" => "sterilizing arena…",
                _ => s.State,
            };
            LiveStateText.Text = stateText;
            LiveWavesText.Text = s.WavesSurvived.ToString();
            LiveRankText.Text = s.PeakRank.ToString();
            LiveAliveText.Text = s.Alive.ToString();
            LiveKillsText.Text = s.Kills.ToString();

            _lastPolledState = s.State;
        }
        catch
        {
            if (_lastPolledState == null) LiveStateText.Text = "server unreachable";
        }
        finally { _pollInFlight = false; }
    }

    private void DragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        _dragStartWindowPosition = AppWindow.Position;
        GetCursorPos(out _dragStartCursorPosition);
        ((UIElement)sender).CapturePointer(e.Pointer);
    }

    private void DragHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        GetCursorPos(out PointInt32 current);
        int deltaX = current.X - _dragStartCursorPosition.X;
        int deltaY = current.Y - _dragStartCursorPosition.Y;
        AppWindow.Move(new PointInt32(_dragStartWindowPosition.X + deltaX, _dragStartWindowPosition.Y + deltaY));
    }

    private void DragHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
