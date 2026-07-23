using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OmegaDev2.Pages;
using OmegaDev2.Services;

namespace OmegaDev2;

public sealed partial class MainWindow : Window
{
    // Nav is generated as private by the XAML compiler. Expose it so HomePage
    // tool-card taps can update the selected menu item.
    public NavigationView NavView => Nav;

    private readonly ServerApiClient _pingClient = new();
    private readonly DispatcherQueueTimer _pingTimer;
    private readonly DispatcherQueueTimer _godModeTimer;

    public MainWindow()
    {
        InitializeComponent();
        Title = "OmegaDev2";

        // Apply the user's theme preference (System/Light/Dark) to the window's
        // root FrameworkElement. Default = follow the OS; Light/Dark = pin the
        // app regardless of the Windows setting. Subscribe so switching in
        // Settings reflows the whole app live, no restart.
        ApplyTheme(PreferencesService.Theme);
        PreferencesService.ThemeChanged += ApplyTheme;
        Closed += (_, _) => PreferencesService.ThemeChanged -= ApplyTheme;

        // Title-bar + taskbar icon (unpackaged app: resolve from the exe folder)
        string icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "OmegaDev2.ico");
        if (System.IO.File.Exists(icoPath))
            AppWindow.SetIcon(icoPath);
        Nav.SelectedItem = Nav.MenuItems[0];

        _pingTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _pingTimer.Interval = TimeSpan.FromSeconds(3);
        _pingTimer.Tick += async (_, _) => await PingAsync();
        _pingTimer.Start();
        _ = PingAsync(); // kick off immediately, don't wait 3s

        _ = WarnIfLastUpdateFailedAsync();

        // God Mode has no auto-expiration (a real Property write, not a
        // timed Condition) — a toggle left on from an earlier session
        // silently persists across zone changes/logout. Poll and show a
        // persistent pill instead of leaving that invisible.
        _godModeTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _godModeTimer.Interval = TimeSpan.FromSeconds(15);
        _godModeTimer.Tick += async (_, _) => await CheckGodModeStatusAsync();
        _godModeTimer.Start();
        _ = CheckGodModeStatusAsync();
    }

    // Surfaces a warning once if the self-updater's robocopy step left a
    // failure marker (see UpdateCheckService.DownloadAndInstallAsync) —
    // otherwise a half-updated install silently launches with no
    // indication anything went wrong until something crashes.
    private async System.Threading.Tasks.Task WarnIfLastUpdateFailedAsync()
    {
        var info = UpdateCheckService.CheckForFailedUpdate();
        if (info == null) return;
        UpdateCheckService.ClearFailedUpdateMarker();

        var dlg = new ContentDialog
        {
            Title = "Last update may be incomplete",
            Content = $"The update to v{info.Version} reported {info.ExitCode} file copy failure(s) — some files may not have been replaced. " +
                      $"If anything looks broken, re-run the update from Settings, or reinstall fresh.\n\nDetails: {info.LogPath}",
            CloseButtonText = "OK",
            XamlRoot = Content.XamlRoot,
        };
        try { await dlg.ShowAsync(); } catch { }
    }

    private void ApplyTheme(PreferencesService.ThemeMode mode)
    {
        if (Content is FrameworkElement root)
            root.RequestedTheme = PreferencesService.ToElementTheme(mode);
    }

    private async System.Threading.Tasks.Task PingAsync()
    {
        _pingClient.BaseUrl = AppState.ServerUrl;
        bool ok;
        try
        {
            var resp = await _pingClient.GetJsonAsync<PingResp>("/ServerStatus");
            ok = resp != null;
        }
        catch { ok = false; }

        AppState.SetServerReachable(ok);
        var accent = ok ? "OmegaDev2.SuccessBrush" : "OmegaDev2.DangerBrush";
        if (Application.Current.Resources.TryGetValue(accent, out var brushObj) && brushObj is Brush b)
            ServerStatusDot.Fill = b;
        ServerStatusText.Text = ok ? "server: online" : "server: offline";
    }

    private async System.Threading.Tasks.Task CheckGodModeStatusAsync()
    {
        try
        {
            _pingClient.BaseUrl = AppState.ServerUrl;
            var resp = await _pingClient.GetGodModeStatusAsync("*");
            GodModeBanner.Visibility = resp?.Ok == true && resp.Active
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch { GodModeBanner.Visibility = Visibility.Collapsed; }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        string tag = item.Tag as string ?? "";
        Type? target = tag switch
        {
            "home"          => typeof(HomePage),
            "teleportpad"   => typeof(TeleportPadPage),
            "phantoms"      => typeof(PhantomsPage),
            "dpsmeter"      => typeof(DpsMeterPage),
            "leaderboard"   => typeof(LeaderboardPage),
            "enemyphantoms" => typeof(EnemyPhantomsPage),
            "wavedirector"  => typeof(WaveDirectorPage),
            "endlesschallenge" => typeof(EndlessChallengePage),
            "godmode"       => typeof(GodModePage),
            "liveevents"    => typeof(EventsLiveTuningPage),
            "regionevents"  => typeof(MetaGameEditorPage),
            "stashmanager"  => typeof(StashManagerPage),
            "currencyeditor" => typeof(CurrencyEditorPage),
            "accounts"      => typeof(AccountManagerPage),
            "console"       => typeof(ConsolePage),
            "logviewer"     => typeof(LogViewerPage),
            "setup"         => typeof(SetupPage),
            "configeditor"  => typeof(ConfigEditorPage),
            "gearpicker"    => typeof(GearPickerPage),
            "diagnostics"   => typeof(DebugConsolePage),
            "settings"      => typeof(SettingsPage),
            _               => null,
        };
        if (target != null) ContentFrame.Navigate(target);
    }

    private class PingResp { public string? Version { get; set; } }
}
