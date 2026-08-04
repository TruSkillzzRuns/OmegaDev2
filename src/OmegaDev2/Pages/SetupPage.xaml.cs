using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmegaDev2.Services;
using Windows.Storage.Pickers;

namespace OmegaDev2.Pages;

// Asset Setup — one server folder + one game client folder PER SERVER VERSION.
//
// Each version must point at its own client install: locale strings and texture
// packages differ between 1.48 / 1.52 / 1.53, so a 1.48 server reading 1.53
// client files resolves item names against the wrong string table. The pairs are
// saved to setup.json, so this is one-time setup rather than something to redo
// every session.
//
// Inspect() re-derives each version's checklist from disk on every Recheck, so
// the page is idempotent: running Apply twice is safe, and a fresh install shows
// exactly which versions still need attention.
public sealed partial class SetupPage : Page
{
    private readonly Dictionary<string, AssetSetupService.SetupStatus> _status = new();
    private readonly StringBuilder _output = new();
    private bool _autoCorrecting;

    public SetupPage()
    {
        InitializeComponent();

        var saved = AssetSetupService.LoadPaths();
        RepoDirBox.Text = saved.RepoDir ?? "";
        foreach (string v in AssetSetupService.Versions)
        {
            var vp = saved.For(v);
            ServerBox(v).Text = vp.ServerDir ?? "";
            ClientBox(v).Text = vp.ClientDir ?? "";
        }

        Recheck();
    }

    // ---------------- per-version control lookup ----------------

    private TextBox ServerBox(string version) => version switch
    {
        "1.48" => ServerDirBox148,
        "1.53" => ServerDirBox153,
        _ => ServerDirBox152,
    };

    private TextBox ClientBox(string version) => version switch
    {
        "1.48" => ClientDirBox148,
        "1.53" => ClientDirBox153,
        _ => ClientDirBox152,
    };

    private TextBlock CheckBlock(string version) => version switch
    {
        "1.48" => Check148,
        "1.53" => Check153,
        _ => Check152,
    };

    // ---------------- pickers ----------------

    private async void BrowseServer_Click(object sender, RoutedEventArgs e)
    {
        string version = (sender as FrameworkElement)?.Tag as string ?? "1.52";
        string? dir = await PickFolderAsync();
        if (dir != null) { ServerBox(version).Text = dir; Recheck(); }
    }

    private async void BrowseClient_Click(object sender, RoutedEventArgs e)
    {
        string version = (sender as FrameworkElement)?.Tag as string ?? "1.52";
        string? dir = await PickFolderAsync();
        if (dir != null) { ClientBox(version).Text = dir; Recheck(); }
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
        if (Check152 == null) return; // parse-time event before load

        bool anyApplyable = false;
        AssetSetupService.SetupStatus toolsRef = null;

        foreach (string v in AssetSetupService.Versions)
        {
            var st = AssetSetupService.Inspect(
                ServerBox(v).Text?.Trim(), ClientBox(v).Text?.Trim(), RepoDirBox.Text?.Trim());
            _status[v] = st;

            // If the user picked a parent folder (e.g. the repo root), Inspect
            // resolved the real server folder below it — reflect that in the box
            // so every later step uses the corrected path. Guarded so the
            // TextChanged this triggers doesn't recurse.
            if (_autoCorrecting == false && st.ServerOk && st.ServerDirResolved != null &&
                string.Equals(st.ServerDirResolved, ServerBox(v).Text?.Trim(), StringComparison.OrdinalIgnoreCase) == false)
            {
                _autoCorrecting = true;
                ServerBox(v).Text = st.ServerDirResolved;
                _autoCorrecting = false;
            }

            bool ready = st.ServerOk && st.CookedOk;
            if (ready) anyApplyable = true;
            if (st.ServerOk && toolsRef == null) toolsRef = st;

            SetCheck(CheckBlock(v), ready, DescribeVersion(v, st));
        }

        // Tools are shared — report the first configured server's view of them.
        SetCheck(CheckTools, toolsRef != null && toolsRef.ToolsOk,
            toolsRef == null ? "extraction tools — fill in a server folder first"
            : toolsRef.ToolsOk ? "extraction tools present"
            : toolsRef.RepoOk ? "extraction tools missing — click Build Tools to build and install them"
            : "extraction tools missing — select your server repo folder above, then click Build Tools");

        ApplyBtn.IsEnabled = anyApplyable;
        BuildToolsBtn.IsEnabled = toolsRef != null && toolsRef.RepoOk;
    }

    /// <summary>One compact status line per version — the detail goes to the output pane on Apply.</summary>
    private static string DescribeVersion(string version, AssetSetupService.SetupStatus st)
    {
        if (st.ServerOk == false) return $"{version} — server folder not set (skipped)";
        if (st.CookedOk == false) return $"{version} — server found, but no game client folder set (skipped)";

        var todo = new List<string>();
        if (st.ConfigApplied == false) todo.Add("config");
        if (st.LocaleInstalled == false && st.LocaleSourceOk) todo.Add("item names");
        if (st.TexIndexOk == false) todo.Add("texture index");

        string tail = todo.Count == 0
            ? "ready — Apply is safe to re-run"
            : "Apply will set up: " + string.Join(", ", todo);
        return $"{version} — {tail}";
    }

    private async void BuildTools_Click(object sender, RoutedEventArgs e)
    {
        // Tools install into a server folder; use the first configured version.
        string version = null;
        foreach (string v in AssetSetupService.Versions)
        {
            if (_status.TryGetValue(v, out var s) && s.ServerOk && s.RepoOk && s.RepoToolsDir != null)
            {
                version = v;
                break;
            }
        }
        if (version == null) return;

        var st = _status[version];
        string serverDir = ServerBox(version).Text.Trim();
        SaveAllPaths();

        BuildToolsBtn.IsEnabled = false;
        ApplyBtn.IsEnabled = false;
        OutputPanel.Visibility = Visibility.Visible;
        _output.Clear();
        ApplyStatusText.Text = "building tools…";

        try
        {
            bool ok = await AssetSetupService.BuildToolsAsync(st.RepoToolsDir, serverDir,
                line => DispatcherQueue.TryEnqueue(() => AppendOutput(line)));
            AppendOutput(ok ? $"Tools built and installed into the {version} server folder." : "Tool build FAILED — see output above.");
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

    private void SaveAllPaths()
    {
        var paths = new AssetSetupService.SetupPaths { RepoDir = RepoDirBox.Text.Trim() };
        foreach (string v in AssetSetupService.Versions)
        {
            paths.For(v).ServerDir = ServerBox(v).Text.Trim();
            paths.For(v).ClientDir = ClientBox(v).Text.Trim();
        }
        AssetSetupService.SavePaths(paths);
    }

    private static void SetCheck(TextBlock block, bool ok, string text)
    {
        block.Text = (ok ? "✔  " : "✖  ") + text;
        block.Opacity = ok ? 1.0 : 0.75;
    }

    // ---------------- apply ----------------

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        SaveAllPaths();

        ApplyBtn.IsEnabled = false;
        OutputPanel.Visibility = Visibility.Visible;
        _output.Clear();

        int configured = 0;

        try
        {
            foreach (string version in AssetSetupService.Versions)
            {
                if (_status.TryGetValue(version, out var st) == false) continue;

                if (st.ServerOk == false || st.CookedOk == false)
                {
                    AppendOutput($"[{version}] skipped — {(st.ServerOk == false ? "no server folder" : "no game client folder")}.");
                    continue;
                }

                string serverDir = ServerBox(version).Text.Trim();
                AppendOutput($"[{version}] configuring {serverDir}");

                // 1. Config.ini — client textures + locale loading for THIS version.
                AppendOutput("  " + AssetSetupService.ApplyConfig(st.ConfigIniPath, st.CookedPath));

                // 2. Locale files from THIS version's own client.
                if (st.LocaleSourceOk && st.LocaleSourcePath != null)
                {
                    string localeSrc = st.LocaleSourcePath;
                    AppendOutput("  " + await Task.Run(() => AssetSetupService.CopyLocaleFiles(localeSrc, serverDir)));
                }
                else
                {
                    AppendOutput("  Locale files: skipped (not found in this client — item names will use data leaf names).");
                }

                // 3. Texture index.
                if (st.TexIndexOk)
                {
                    AppendOutput("  Texture index: already built — skipped.");
                }
                else if (st.ToolsOk && st.UpkExtractPath != null)
                {
                    AppendOutput("  Texture index: building (this scans every package once — a few minutes)…");
                    bool ok = await AssetSetupService.BuildTextureIndexAsync(st.UpkExtractPath, st.CookedPath, serverDir,
                        line => DispatcherQueue.TryEnqueue(() => AppendOutput("    " + line)));
                    AppendOutput(ok ? "  Texture index: done." : "  Texture index: FAILED — check the output above.");
                }
                else
                {
                    AppendOutput("  Texture index: skipped — extraction tools missing (see checklist).");
                }

                configured++;
                AppendOutput("");
            }

            if (configured == 0)
            {
                AppendOutput("Nothing to do — fill in a server folder and its matching game client folder for at least one version.");
                ApplyStatusText.Text = "Nothing configured.";
            }
            else
            {
                AppendOutput($"Setup complete for {configured} version(s). RESTART THOSE SERVERS to pick up the new Config.ini, then reload the catalog/roster in the app.");
                ApplyStatusText.Text = $"Done ({configured}) — restart the server(s).";
            }
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
