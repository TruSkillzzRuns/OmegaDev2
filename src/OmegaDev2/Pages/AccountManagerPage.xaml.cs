using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmegaDev2.Services;

namespace OmegaDev2.Pages;

// Account Manager — a form UI over the server's account commands, executed
// in server-console mode via /webapi/console/exec. Nothing account-related
// is parsed or stored app-side; the server's own validation and messages
// come straight back into the result log.
public sealed partial class AccountManagerPage : Page
{
    private readonly ServerApiClient _api = new();
    private readonly StringBuilder _output = new();

    public AccountManagerPage()
    {
        InitializeComponent();
        Append("Ready. Results from the server appear here.");
    }

    private string ManagedEmail => ManageEmailBox.Text?.Trim() ?? "";

    private async Task RunAsync(string command, string redactedEcho)
    {
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            Append($"> {redactedEcho}");
            var resp = await _api.PostConsoleExecAsync(command, null);
            if (resp == null) Append("  [no response from server]");
            else if (resp.Ok == false) Append($"  [error] {resp.Error}");
            else Append("  " + (string.IsNullOrWhiteSpace(resp.Output) ? "(no output)" : resp.Output!.Replace("\n", "\n  ")));
        }
        catch (Exception ex)
        {
            Append($"  [error] {ex.Message}");
        }
    }

    private bool RequireEmail()
    {
        if (ManagedEmail.Length > 0) return true;
        Append("! enter an account email in the Manage panel first");
        return false;
    }

    // ---------------- Create ----------------

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        string email = CreateEmailBox.Text?.Trim() ?? "";
        string name = CreateNameBox.Text?.Trim() ?? "";
        string password = CreatePasswordBox.Password ?? "";
        if (email.Length == 0 || name.Length == 0 || password.Length == 0)
        {
            Append("! create needs email, player name and password");
            return;
        }

        CreateBtn.IsEnabled = false;
        try
        {
            // Password never appears in the log — only in the request.
            await RunAsync($"account create {email} {name} {password}", $"account create {email} {name} ******");
            CreatePasswordBox.Password = "";
        }
        finally { CreateBtn.IsEnabled = true; }
    }

    // ---------------- Manage ----------------

    private async void Info_Click(object sender, RoutedEventArgs e)
    {
        // account info is a client-only command that dumps whoever is
        // currently logged in — the server doesn't offer a "look up by
        // email" over-console path. Route it through the player-context
        // exec so it reports on whoever is online right now.
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            Append("> account info (current logged-in player)");
            var resp = await _api.PostConsoleExecAsync("account info", "*");
            if (resp == null) Append("  [no response from server]");
            else if (resp.Ok == false) Append($"  [error] {resp.Error}");
            else Append("  " + (string.IsNullOrWhiteSpace(resp.Output) ? "(no output)" : resp.Output!.Replace("\n", "\n  ")));
        }
        catch (Exception ex) { Append($"  [error] {ex.Message}"); }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (RequireEmail() == false) return;
        string name = NewNameBox.Text?.Trim() ?? "";
        if (name.Length == 0) { Append("! enter the new player name"); return; }
        await RunAsync($"account playername {ManagedEmail} {name}", $"account playername {ManagedEmail} {name}");
    }

    private async void SetPassword_Click(object sender, RoutedEventArgs e)
    {
        if (RequireEmail() == false) return;
        string password = NewPasswordBox.Password ?? "";
        if (password.Length == 0) { Append("! enter the new password"); return; }
        await RunAsync($"account password {ManagedEmail} {password}", $"account password {ManagedEmail} ******");
        NewPasswordBox.Password = "";
    }

    private async void SetUserLevel_Click(object sender, RoutedEventArgs e)
    {
        if (RequireEmail() == false) return;
        int level = UserLevelCombo.SelectedIndex;
        if (level < 0) level = 0;
        await RunAsync($"account userlevel {ManagedEmail} {level}", $"account userlevel {ManagedEmail} {level}");
    }

    private async void Ban_Click(object sender, RoutedEventArgs e)
    {
        if (RequireEmail() == false) return;

        var dialog = new ContentDialog
        {
            Title = "Ban account?",
            Content = $"Ban {ManagedEmail}? They will no longer be able to log in.",
            PrimaryButtonText = "Ban",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await RunAsync($"account ban {ManagedEmail}", $"account ban {ManagedEmail}");
    }

    private async void Unban_Click(object sender, RoutedEventArgs e)
    {
        if (RequireEmail() == false) return;
        await RunAsync($"account unban {ManagedEmail}", $"account unban {ManagedEmail}");
    }

    private async void Whitelist_Click(object sender, RoutedEventArgs e)
    {
        if (RequireEmail() == false) return;
        await RunAsync($"account whitelist {ManagedEmail}", $"account whitelist {ManagedEmail}");
    }

    private async void Unwhitelist_Click(object sender, RoutedEventArgs e)
    {
        if (RequireEmail() == false) return;
        await RunAsync($"account unwhitelist {ManagedEmail}", $"account unwhitelist {ManagedEmail}");
    }

    // ---------------- Output ----------------

    private void ClearOutput_Click(object sender, RoutedEventArgs e)
    {
        _output.Clear();
        OutputText.Text = "";
    }

    private void Append(string line)
    {
        _output.AppendLine(line);
        if (_output.Length > 100_000) _output.Remove(0, _output.Length - 80_000);
        OutputText.Text = _output.ToString();
        OutputScroll.UpdateLayout();
        OutputScroll.ChangeView(null, OutputScroll.ScrollableHeight, null, true);
    }
}
