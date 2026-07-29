using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmegaDev2.Services;
using Windows.Storage.Pickers;

namespace OmegaDev2.Pages;

public sealed partial class AccountMigrationPage : Page
{
    private sealed class ExportResponse
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public JsonElement Snapshot { get; set; }
    }

    private sealed class ImportRequest
    {
        public string PlayerName { get; set; } = "";
        public JsonElement Snapshot { get; set; }
    }

    private sealed class ImportResponse
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public int AvatarsImported { get; set; }
        public int TeamUpsImported { get; set; }
        public int ItemsImported { get; set; }
        public int ItemsEquipped { get; set; }
        public int StashTabsUnlocked { get; set; }
        public string[]? Skipped { get; set; }
    }

    private sealed class AccountCredentials
    {
        public string Email { get; set; } = "";
        public string PlayerName { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public string Salt { get; set; } = "";
        public int UserLevel { get; set; }
        public int Flags { get; set; }
    }

    private sealed class CredentialsExportResponse
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public AccountCredentials? Credentials { get; set; }
    }

    private sealed class CredentialsImportResponse
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public bool Created { get; set; }
        public string? Message { get; set; }
    }

    // Fixed ports set up by StartServer_v48.bat/StartServer.bat/StartServer_v53.bat --
    // same convention used everywhere else in this app for the multi-version setup.
    private sealed record ServerVersionOption(string Label, string Url);

    private static readonly ServerVersionOption[] ServerVersions =
    {
        new("1.48", "http://localhost:8081"),
        new("1.52", "http://localhost:8080"),
        new("1.53", "http://localhost:8082"),
    };

    private JsonElement? _loadedSnapshot;

    // Guards the initial Text assignment below from immediately re-saving
    // the values we just loaded back to disk via PathBox_TextChanged.
    private bool _pathsLoaded;

    public AccountMigrationPage()
    {
        InitializeComponent();

        foreach (var v in ServerVersions)
        {
            ExportServerCombo.Items.Add(v.Label);
            ImportServerCombo.Items.Add(v.Label);
            CloneSourceServerCombo.Items.Add(v.Label);
        }

        ExportServerCombo.SelectedIndex = 1; // 1.52 -- your existing account already lives here
        ImportServerCombo.SelectedIndex = 0; // 1.48 -- most common migration target
        CloneSourceServerCombo.SelectedIndex = 1; // 1.52 -- same source as Export by default

        var paths = ServerLaunchService.LoadPaths();
        RepoPathBox.Text = string.IsNullOrWhiteSpace(paths.RepoDir) ? @"C:\dev\MHServerEmu" : paths.RepoDir;
        ClientPath48Box.Text = paths.ClientPath48 ?? "";
        ClientPath52Box.Text = paths.ClientPath52 ?? "";
        ClientPath53Box.Text = paths.ClientPath53 ?? "";
        _pathsLoaded = true;
    }

    private void PathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_pathsLoaded == false) return;

        ServerLaunchService.SavePaths(new ServerLaunchService.LaunchPaths
        {
            RepoDir = RepoPathBox.Text?.Trim(),
            ClientPath48 = ClientPath48Box.Text?.Trim(),
            ClientPath52 = ClientPath52Box.Text?.Trim(),
            ClientPath53 = ClientPath53Box.Text?.Trim(),
        });
    }

    private async void BrowseRepoDir_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        RepoPathBox.Text = folder.Path;
    }

    private async void BrowseClientExe_Click(object sender, RoutedEventArgs e)
    {
        string tag = (string)((Button)sender).Tag;

        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        TextBox box = tag switch
        {
            "v48" => ClientPath48Box,
            "v52" => ClientPath52Box,
            "v53" => ClientPath53Box,
            _ => throw new ArgumentOutOfRangeException(nameof(tag)),
        };
        box.Text = file.Path;
    }

    private static string UrlFor(ComboBox combo) =>
        ServerVersions[Math.Max(0, combo.SelectedIndex)].Url;

    // ---------------- Server Control ----------------
    // Native replacements for the StartServer*.bat/StartClient*.bat scripts
    // (port-patch + launch + PID tracking, or launch client with the right
    // siteconfig) -- see ServerLaunchService. Those scripts are personal and
    // gitignored, so they only ever worked for whoever wrote them; this
    // works for anyone once they've set the paths above.

    private void StartServer_Click(object sender, RoutedEventArgs e)
    {
        string tag = (string)((Button)sender).Tag;
        string repoPath = RepoPathBox.Text?.Trim() ?? "";

        if (Directory.Exists(repoPath) && ServerLaunchService.IsServerRunning(repoPath, tag))
        {
            ServerControlStatusText.Text = $"{tag} server is already running — not starting a second one.";
            return;
        }

        var result = ServerLaunchService.StartServer(repoPath, tag);
        ServerControlStatusText.Text = result.Message;
    }

    private void StopServer_Click(object sender, RoutedEventArgs e)
    {
        string tag = (string)((Button)sender).Tag;
        string repoPath = RepoPathBox.Text?.Trim() ?? "";

        var result = ServerLaunchService.StopServer(repoPath, tag);
        ServerControlStatusText.Text = result.Message;
    }

    private void LaunchClient_Click(object sender, RoutedEventArgs e)
    {
        string tag = (string)((Button)sender).Tag;
        string clientPath = tag switch
        {
            "v48" => ClientPath48Box.Text?.Trim() ?? "",
            "v52" => ClientPath52Box.Text?.Trim() ?? "",
            "v53" => ClientPath53Box.Text?.Trim() ?? "",
            _ => "",
        };

        var result = ServerLaunchService.LaunchClient(clientPath, tag);
        ServerControlStatusText.Text = result.Message;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        ExportBtn.IsEnabled = false;
        ExportStatusText.Text = "Exporting…";
        try
        {
            string url = UrlFor(ExportServerCombo);
            using var client = new ServerApiClient(url);
            string player = string.IsNullOrWhiteSpace(ExportPlayerBox.Text) ? "*" : ExportPlayerBox.Text.Trim();

            var resp = await client.GetJsonAsync<ExportResponse>($"/webapi/account/migration/export?player={Uri.EscapeDataString(player)}");
            if (resp == null || resp.Ok == false)
            {
                ExportStatusText.Text = $"Export failed: {resp?.Error ?? "no response"}";
                return;
            }

            _loadedSnapshot = resp.Snapshot;
            SaveSnapshotBtn.IsEnabled = true;
            ImportBtn.IsEnabled = true;
            LoadedSnapshotText.Text = "Snapshot ready (just exported) — save it to a file, or import it directly if the target server is also running.";
            ExportStatusText.Text = "Export complete.";
        }
        catch (Exception ex)
        {
            ExportStatusText.Text = $"Export failed: {ex.Message}";
        }
        finally
        {
            ExportBtn.IsEnabled = true;
        }
    }

    private async void SaveSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedSnapshot == null) return;

        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add("Account Snapshot", new System.Collections.Generic.List<string> { ".json" });
        picker.SuggestedFileName = $"AccountMigration_{DateTime.Now:yyyyMMdd_HHmmss}";
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        string json = JsonSerializer.Serialize(_loadedSnapshot.Value, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(file.Path, json);
        ExportStatusText.Text = $"Saved to {file.Path}";
    }

    private async void LoadSnapshot_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        try
        {
            string json = await File.ReadAllTextAsync(file.Path);
            _loadedSnapshot = JsonSerializer.Deserialize<JsonElement>(json);
            ImportBtn.IsEnabled = true;
            LoadedSnapshotText.Text = $"Loaded from {file.Path}";
        }
        catch (Exception ex)
        {
            LoadedSnapshotText.Text = $"Failed to load snapshot: {ex.Message}";
        }
    }

    private async void CloneLogin_Click(object sender, RoutedEventArgs e)
    {
        string sourceUrl = UrlFor(CloneSourceServerCombo);
        string targetUrl = UrlFor(ImportServerCombo);
        string email = CloneEmailBox.Text?.Trim() ?? "";

        if (email.Length == 0)
        {
            CloneLoginStatusText.Text = "Needs the account email (as it exists on the source server).";
            return;
        }

        if (sourceUrl == targetUrl)
        {
            CloneLoginStatusText.Text = "Source and target are the same version — nothing to clone.";
            return;
        }

        CloneLoginBtn.IsEnabled = false;
        CloneLoginStatusText.Text = "Reading account from source…";
        try
        {
            using var sourceClient = new ServerApiClient(sourceUrl);
            var exportResp = await sourceClient.GetJsonAsync<CredentialsExportResponse>(
                $"/webapi/account/migration/credentials/export?email={Uri.EscapeDataString(email)}");

            if (exportResp == null || exportResp.Ok == false || exportResp.Credentials == null)
            {
                CloneLoginStatusText.Text = $"Failed to read source account: {exportResp?.Error ?? "no response"}";
                return;
            }

            CloneLoginStatusText.Text = "Cloning onto target…";
            using var targetClient = new ServerApiClient(targetUrl);
            var importResp = await targetClient.PostJsonAsync<CredentialsImportResponse>(
                "/webapi/account/migration/credentials/import", exportResp.Credentials);

            if (importResp == null || importResp.Ok == false)
            {
                CloneLoginStatusText.Text = $"Failed: {importResp?.Error ?? "no response"}";
                return;
            }

            CloneLoginStatusText.Text = importResp.Message ?? (importResp.Created ? "Login cloned — log in with your existing password." : "Account already existed on the target.");
        }
        catch (Exception ex)
        {
            CloneLoginStatusText.Text = $"Failed: {ex.Message}";
        }
        finally
        {
            CloneLoginBtn.IsEnabled = true;
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedSnapshot == null) return;

        ImportBtn.IsEnabled = false;
        ImportStatusText.Text = "Importing…";
        SkippedPanel.Visibility = Visibility.Collapsed;
        try
        {
            string url = UrlFor(ImportServerCombo);
            using var client = new ServerApiClient(url);
            string player = string.IsNullOrWhiteSpace(ImportPlayerBox.Text) ? "*" : ImportPlayerBox.Text.Trim();

            var request = new ImportRequest { PlayerName = player, Snapshot = _loadedSnapshot.Value };
            var resp = await client.PostJsonAsync<ImportResponse>("/webapi/account/migration/import", request);
            if (resp == null || resp.Ok == false)
            {
                ImportStatusText.Text = $"Import failed: {resp?.Error ?? "no response"}";
                return;
            }

            ImportStatusText.Text = $"Imported {resp.AvatarsImported} avatar(s), {resp.TeamUpsImported} team-up(s), {resp.ItemsImported} item(s) ({resp.ItemsEquipped} re-equipped, rest in stash), currency, and {resp.StashTabsUnlocked} stash tab(s) auto-unlocked. " +
                                     $"{(resp.Skipped?.Length ?? 0)} skipped (not on this client version).";

            if (resp.Skipped is { Length: > 0 })
            {
                var items = new ObservableCollection<string>(resp.Skipped);
                SkippedList.ItemsSource = items;
                SkippedPanel.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            ImportStatusText.Text = $"Import failed: {ex.Message}";
        }
        finally
        {
            ImportBtn.IsEnabled = true;
        }
    }
}
