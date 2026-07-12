using System;
using System.Collections.ObjectModel;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OmegaDev2.Services;
using Windows.System;

namespace OmegaDev2.Pages;

// Command Console — run any server chat/console command over
// /webapi/console/exec. With a player set, commands execute as that player's
// connection on their game thread (so client-invoker commands work); blank
// player = server-console mode.
public sealed partial class ConsolePage : Page
{
    private readonly ServerApiClient _api = new();
    private readonly StringBuilder _output = new();
    public ObservableCollection<string> History { get; } = new();

    // History persists across page visits within the app run.
    private static readonly ObservableCollection<string> s_sharedHistory = new();

    public ConsolePage()
    {
        InitializeComponent();
        HistoryList.ItemsSource = s_sharedHistory;
        OutputText.Text = "Ready. Type a command below — '!' prefix optional.\n";
    }

    private void CommandBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            Run_Click(sender, null!);
        }
    }

    private void HistoryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is string cmd)
        {
            CommandBox.Text = cmd;
            CommandBox.Focus(FocusState.Programmatic);
            CommandBox.SelectionStart = cmd.Length;
        }
    }

    private void ClearOutput_Click(object sender, RoutedEventArgs e)
    {
        _output.Clear();
        OutputText.Text = "";
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        string command = CommandBox.Text?.Trim() ?? "";
        if (command.Length == 0) return;

        string player = PlayerBox.Text?.Trim() ?? "";
        RunBtn.IsEnabled = false;
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            Append($"> {command}{(player.Length > 0 ? $"   (as {player})" : "   (server console)")}");

            var resp = await _api.PostConsoleExecAsync(command, player.Length > 0 ? player : null);
            if (resp == null)
                Append("  [no response from server]");
            else if (resp.Ok == false)
                Append($"  [error] {resp.Error}");
            else
                Append(string.IsNullOrWhiteSpace(resp.Output) ? "  (no output)" : Indent(resp.Output!));

            // De-dupe: most recent run floats to the top.
            s_sharedHistory.Remove(command);
            s_sharedHistory.Insert(0, command);
            while (s_sharedHistory.Count > 50) s_sharedHistory.RemoveAt(s_sharedHistory.Count - 1);

            CommandBox.Text = "";
            CommandBox.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            Append($"  [error] {ex.Message}");
        }
        finally
        {
            RunBtn.IsEnabled = true;
        }
    }

    private static string Indent(string text)
    {
        var sb = new StringBuilder();
        foreach (string line in text.Replace("\r", "").Split('\n'))
            sb.Append("  ").AppendLine(line);
        return sb.ToString().TrimEnd();
    }

    private void Append(string line)
    {
        _output.AppendLine(line);
        // Keep the buffer bounded so hours of use can't balloon memory.
        if (_output.Length > 200_000) _output.Remove(0, _output.Length - 150_000);
        OutputText.Text = _output.ToString();
        OutputScroll.UpdateLayout();
        OutputScroll.ChangeView(null, OutputScroll.ScrollableHeight, null, true);
    }
}
