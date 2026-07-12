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

    public MainWindow()
    {
        InitializeComponent();
        Title = "OmegaDev2";

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

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        string tag = item.Tag as string ?? "";
        Type? target = tag switch
        {
            "home"          => typeof(HomePage),
            "teleportpad"   => typeof(TeleportPadPage),
            "phantoms"      => typeof(PhantomsPage),
            "squadbuilder"  => typeof(SquadBuilderPage),
            "dpsmeter"      => typeof(DpsMeterPage),
            "enemyphantoms" => typeof(EnemyPhantomsPage),
            "wavedirector"  => typeof(WaveDirectorPage),
            "stashmanager"  => typeof(StashManagerPage),
            "accounts"      => typeof(AccountManagerPage),
            "console"       => typeof(ConsolePage),
            "logviewer"     => typeof(LogViewerPage),
            "setup"         => typeof(SetupPage),
            "gearpicker"    => typeof(GearPickerPage),
            "diagnostics"   => typeof(DebugConsolePage),
            _               => null,
        };
        if (target != null) ContentFrame.Navigate(target);
    }

    private class PingResp { public string? Version { get; set; } }
}
