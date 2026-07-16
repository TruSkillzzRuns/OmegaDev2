using System;
using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OmegaDev2.Services;

namespace OmegaDev2.Pages;

public sealed class LeaderboardRow
{
    public string Id { get; }
    public int Rank { get; }
    public string KindBadge { get; }
    public string HeroName { get; }
    public string ValueText { get; }
    public string RegionText { get; }
    public string WhenText { get; }

    public LeaderboardRow(int rank, LeaderboardEntry e)
    {
        Id = e.Id;
        Rank = rank;
        bool isTerminal = string.Equals(e.Kind, "TerminalRun", StringComparison.OrdinalIgnoreCase);
        KindBadge = isTerminal ? "[RUN]" : "[DPS]";
        // Server sends a clean hero name for new entries — LeafOf is a
        // no-op for those and only matters for entries saved before that
        // fix, which still have the raw prototype path persisted.
        HeroName = LeafOf(e.HeroName);
        ValueText = isTerminal ? FormatDuration(e.Value) : $"{e.Value:N0} DPS";
        string regionLabel = isTerminal && e.RegionName != null ? $"{e.RegionName} ({e.DifficultyTier})" : "";
        RegionText = isTerminal && e.Completed == false ? $"{regionLabel} — aborted".Trim() : regionLabel;
        WhenText = DateTimeOffset.FromUnixTimeMilliseconds(e.TimestampMs).LocalDateTime.ToString("MM/dd HH:mm");
    }

    private static string FormatDuration(double ms)
    {
        long totalSeconds = (long)(ms / 1000.0);
        return $"{totalSeconds / 60}m {totalSeconds % 60:00}s";
    }

    private static string LeafOf(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        int slash = path.LastIndexOf('/');
        string leaf = slash >= 0 ? path[(slash + 1)..] : path;
        const string suffix = ".prototype";
        if (leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            leaf = leaf[..^suffix.Length];
        return leaf;
    }
}

// Leaderboard — persistent best-of history: saved DPS parses (explicit,
// via the DPS Meter's "Save to Leaderboard" button) and terminal run times
// (automatic, logged server-side the instant the boss dies).
public sealed partial class LeaderboardPage : Page
{
    private readonly ServerApiClient _api = new();
    private readonly DispatcherQueueTimer _timer;
    private bool _initialized;
    private bool _pollInFlight;
    public ObservableCollection<LeaderboardRow> Rows { get; } = new();

    public LeaderboardPage()
    {
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += async (_, _) => await RefreshAsync();

        InitializeComponent();
        EntryList.ItemsSource = Rows;
        _initialized = true;
        _timer.Start();
        _ = RefreshAsync();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _timer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _timer.Stop();
        base.OnNavigatedFrom(e);
    }

    private string TargetPlayer => string.IsNullOrWhiteSpace(PlayerBox.Text) ? "*" : PlayerBox.Text.Trim();
    private string? SelectedKind => (KindCombo.SelectedItem as ComboBoxItem)?.Tag as string;

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void KindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized == false) return; // parse-time firing
        await RefreshAsync();
    }

    private async System.Threading.Tasks.Task RefreshAsync()
    {
        if (_pollInFlight) return;
        _pollInFlight = true;
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            string? hero = string.IsNullOrWhiteSpace(HeroBox.Text) ? null : HeroBox.Text.Trim();
            var resp = await _api.GetLeaderboardAsync(TargetPlayer, SelectedKind, hero);
            Rows.Clear();
            if (resp == null || resp.Ok == false)
            {
                StatusText.Text = resp?.Error ?? "leaderboard load failed";
                return;
            }

            int rank = 1;
            foreach (var entry in resp.Entries)
                Rows.Add(new LeaderboardRow(rank++, entry));

            StatusText.Text = $"{resp.Entries.Count} entr{(resp.Entries.Count == 1 ? "y" : "ies")}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"error: {ex.Message}";
        }
        finally
        {
            _pollInFlight = false;
        }
    }

    private async void DeleteEntry_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id) return;
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostLeaderboardDeleteAsync(TargetPlayer, id);
            StatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"error: {ex.Message}";
        }
    }

    private async void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            Title = "Clear leaderboard?",
            Content = "This deletes every saved entry for this player. This can't be undone.",
            PrimaryButtonText = "Clear All",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        var result = await dlg.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostLeaderboardClearAsync(TargetPlayer);
            StatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"error: {ex.Message}";
        }
    }
}
