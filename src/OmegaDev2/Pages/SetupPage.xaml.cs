using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmegaDev2.Services;
using Windows.Storage.Pickers;

namespace OmegaDev2.Pages;

// Asset Setup — two Browse buttons instead of a manual config walkthrough.
// Inspect() re-derives the checklist from disk every time, so the page is
// idempotent: running Apply twice is safe, and a fresh install shows exactly
// which steps remain.
public sealed partial class SetupPage : Page
{
    private AssetSetupService.SetupStatus _status = new();
    private readonly StringBuilder _output = new();
    private bool _autoCorrecting;

    public SetupPage()
    {
        InitializeComponent();
        var saved = AssetSetupService.LoadPaths();
        ServerDirBox.Text = saved.ServerDir ?? "";
        ClientDirBox.Text = saved.ClientDir ?? "";
        RepoDirBox.Text = saved.RepoDir ?? "";
        Recheck();
    }

    // ---------------- pickers ----------------

    private async void BrowseServer_Click(object sender, RoutedEventArgs e)
    {
        string? dir = await PickFolderAsync();
        if (dir != null) { ServerDirBox.Text = dir; Recheck(); }
    }

    private async void BrowseClient_Click(object sender, RoutedEventArgs e)
    {
        string? dir = await PickFolderAsync();
        if (dir != null) { ClientDirBox.Text = dir; Recheck(); }
    }

    private async void BrowseRepo_Click(object sender, RoutedEventArgs e)
    {
        string? dir = await PickFolderAsync();
        if (dir != null) { RepoDirBox.Text = dir; Recheck(); }
    }

    private static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        // Unpackaged WinUI 3: the picker needs the window handle explicitly.
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private void Paths_Changed(object sender, TextChangedEventArgs e) => Recheck();

    private void Recheck_Click(object sender, RoutedEventArgs e) => Recheck();

    // ---------------- checklist ----------------

    private void Recheck()
    {
        if (CheckServer == null) return; // parse-time event before load

        _status = AssetSetupService.Inspect(ServerDirBox.Text?.Trim(), ClientDirBox.Text?.Trim(), RepoDirBox.Text?.Trim());

        // If the user picked a parent folder (e.g. the repo root), Inspect
        // resolved the real server folder below it — reflect that in the box
        // so every later step uses the corrected path. Guarded so the
        // TextChanged this triggers doesn't recurse.
        if (_autoCorrecting == false && _status.ServerOk && _status.ServerDirResolved != null &&
            string.Equals(_status.ServerDirResolved, ServerDirBox.Text?.Trim(), StringComparison.OrdinalIgnoreCase) == false)
        {
            _autoCorrecting = true;
            ServerDirBox.Text = _status.ServerDirResolved;
            _autoCorrecting = false;
        }

        SetCheck(CheckServer, _status.ServerOk,
            _status.ServerOk ? "server folder found" : "server folder — select the folder holding MHServerEmu.exe + Config.ini");
        SetCheck(CheckCooked, _status.CookedOk,
            _status.CookedOk ? $"client textures: {_status.CookedPath}" : "client CookedPCConsole folder — select your game client's install folder");
        SetCheck(CheckManifest, _status.ManifestOk,
            _status.ManifestOk ? "texture cache manifest found" : "texture cache manifest (TextureFileCacheManifest.bin) not found");
        SetCheck(CheckTools, _status.ToolsOk,
            _status.ToolsOk ? "extraction tools present"
            : _status.RepoOk ? "extraction tools missing — click Build Tools to build and install them"
            : "extraction tools missing — select your server repo folder above, then click Build Tools");
        SetCheck(CheckLocale, _status.LocaleSourceOk || _status.LocaleInstalled,
            _status.LocaleInstalled ? "locale files already installed on server"
            : _status.LocaleSourceOk ? $"locale files found: {_status.LocaleSourcePath}"
            : "locale files not found in client (item names will use data leaf names)");
        SetCheck(CheckConfig, _status.ConfigApplied,
            _status.ConfigApplied ? "Config.ini already configured" : "Config.ini not configured yet");
        SetCheck(CheckIndex, _status.TexIndexOk,
            _status.TexIndexOk ? "texture index already built" : "texture index not built yet (Apply runs it — takes a few minutes)");

        ApplyBtn.IsEnabled = _status.ServerOk && _status.CookedOk;
        BuildToolsBtn.IsEnabled = _status.ServerOk && _status.RepoOk;
    }

    private async void BuildTools_Click(object sender, RoutedEventArgs e)
    {
        if (_status.ServerOk == false || _status.RepoOk == false || _status.RepoToolsDir == null) return;

        string serverDir = ServerDirBox.Text.Trim();
        AssetSetupService.SavePaths(new AssetSetupService.SetupPaths
        {
            ServerDir = serverDir,
            ClientDir = ClientDirBox.Text.Trim(),
            RepoDir = RepoDirBox.Text.Trim(),
        });

        BuildToolsBtn.IsEnabled = false;
        ApplyBtn.IsEnabled = false;
        OutputPanel.Visibility = Visibility.Visible;
        _output.Clear();
        ApplyStatusText.Text = "building tools…";

        try
        {
            bool ok = await AssetSetupService.BuildToolsAsync(_status.RepoToolsDir, serverDir,
                line => DispatcherQueue.TryEnqueue(() => AppendOutput(line)));
            AppendOutput(ok ? "Tools built and installed." : "Tool build FAILED — see output above.");
            ApplyStatusText.Text = ok ? "Tools installed — now hit Apply Setup." : "Tool build failed.";
        }
        catch (Exception ex)
        {
            AppendOutput($"ERROR: {ex.Message}");
            ApplyStatusText.Text = "Tool build failed — see output.";
        }
        finally
        {
            Recheck();
        }
    }

    private static void SetCheck(TextBlock block, bool ok, string text)
    {
        block.Text = (ok ? "✔  " : "✖  ") + text;
        block.Opacity = ok ? 1.0 : 0.75;
    }

    // ---------------- apply ----------------

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_status.ServerOk == false || _status.CookedOk == false) return;

        string serverDir = ServerDirBox.Text.Trim();
        AssetSetupService.SavePaths(new AssetSetupService.SetupPaths
        {
            ServerDir = serverDir,
            ClientDir = ClientDirBox.Text.Trim(),
            RepoDir = RepoDirBox.Text.Trim(),
        });

        ApplyBtn.IsEnabled = false;
        OutputPanel.Visibility = Visibility.Visible;
        _output.Clear();

        try
        {
            // 1. Config.ini
            AppendOutput(AssetSetupService.ApplyConfig(_status.ConfigIniPath!, _status.CookedPath!));

            // 2. Locale files
            if (_status.LocaleSourceOk && _status.LocaleSourcePath != null)
                AppendOutput(await Task.Run(() => AssetSetupService.CopyLocaleFiles(_status.LocaleSourcePath, serverDir)));
            else
                AppendOutput("Locale files: skipped (source not found).");

            // 3. Texture index
            if (_status.TexIndexOk)
            {
                AppendOutput("Texture index: already built — skipped.");
            }
            else if (_status.ToolsOk && _status.UpkExtractPath != null)
            {
                AppendOutput("Texture index: building (this scans every package once — a few minutes)…");
                bool ok = await AssetSetupService.BuildTextureIndexAsync(_status.UpkExtractPath, _status.CookedPath!, serverDir,
                    line => DispatcherQueue.TryEnqueue(() => AppendOutput("  " + line)));
                AppendOutput(ok ? "Texture index: done." : "Texture index: FAILED — check the output above.");
            }
            else
            {
                AppendOutput("Texture index: skipped — extraction tools missing (see checklist).");
            }

            AppendOutput("");
            AppendOutput("Setup complete. RESTART THE SERVER to pick up the new Config.ini, then reload the catalog/roster in the app.");
            ApplyStatusText.Text = "Done — restart the server.";
        }
        catch (Exception ex)
        {
            AppendOutput($"ERROR: {ex.Message}");
            ApplyStatusText.Text = "Setup failed — see output.";
        }
        finally
        {
            Recheck();
            ApplyBtn.IsEnabled = true;
        }
    }

    private void AppendOutput(string line)
    {
        _output.AppendLine(line);
        OutputText.Text = _output.ToString();
        OutputScroll.UpdateLayout();
        OutputScroll.ChangeView(null, OutputScroll.ScrollableHeight, null, true);
    }
}
