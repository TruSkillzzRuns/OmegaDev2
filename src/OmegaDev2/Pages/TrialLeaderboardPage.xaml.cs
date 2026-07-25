using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using OmegaDev2.Services;
using Windows.UI;

namespace OmegaDev2.Pages;

// Trial of the Impossible — its own sub-tool under Leaderboards. Unlike
// LeaderboardPage (scoped to one logged-in account's DPS parses / terminal
// runs), this is a real cross-account board: every registered account's
// Trial attempts, ranked together, via GetTrialGlobalLeaderboardAsync.
public sealed class TrialLeaderboardRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private static readonly SolidColorBrush s_goldBrush = new(Color.FromArgb(0xFF, 0xFF, 0xC1, 0x07));
    private static readonly SolidColorBrush s_silverBrush = new(Color.FromArgb(0xFF, 0xC9, 0xD3, 0xDC));
    private static readonly SolidColorBrush s_bronzeBrush = new(Color.FromArgb(0xFF, 0xCD, 0x7F, 0x32));
    private static readonly SolidColorBrush s_wonBrush = new(Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50));
    private static readonly SolidColorBrush s_abortedBrush = new(Color.FromArgb(0xFF, 0xE0, 0x57, 0x57));

    public TrialGlobalLeaderboardEntry Entry { get; }

    public int Rank => Entry.Rank;
    public string RankText => Entry.Rank.ToString();
    public string RankMedal => Entry.Rank switch { 1 => "\U0001F451", 2 => "\U0001F948", 3 => "\U0001F949", _ => "" };
    public Microsoft.UI.Xaml.Visibility MedalVisibility => Entry.Rank <= 3
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Brush RankBrush => Entry.Rank switch
    {
        1 => s_goldBrush,
        2 => s_silverBrush,
        3 => s_bronzeBrush,
        _ => (Brush)Microsoft.UI.Xaml.Application.Current.Resources["OmegaDev2.TextSecondaryBrush"],
    };

    public string PlayerName => Entry.PlayerName;
    public string HeroName => Entry.HeroName;
    public string NemesisKillsText => Entry.NemesisKills.ToString("N0");
    public string DeathsText => Entry.Deaths.ToString();
    public string ResultText => Entry.Completed ? "Victory" : (Entry.FailReason ?? "Aborted");
    public SolidColorBrush ResultBrush => Entry.Completed ? s_wonBrush : s_abortedBrush;
    public string TimeText => FormatDuration(Entry.ElapsedMs);
    public string DateText => DateTimeOffset.FromUnixTimeMilliseconds(Entry.TimestampMs).LocalDateTime.ToString("MMM d, yyyy h:mm tt");

    private BitmapImage? _portrait;
    public BitmapImage? Portrait { get => _portrait; set { _portrait = value; Raise(); } }
    public bool PortraitRequested;

    public TrialLeaderboardRow(TrialGlobalLeaderboardEntry entry) => Entry = entry;

    private static string FormatDuration(long ms)
    {
        long totalSeconds = ms / 1000;
        long h = totalSeconds / 3600;
        long m = (totalSeconds % 3600) / 60;
        long s = totalSeconds % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }
}

public sealed partial class TrialLeaderboardPage : Page
{
    private readonly ServerApiClient _api = new();
    private readonly DispatcherQueueTimer _timer;
    private CancellationTokenSource? _pageCts;
    private bool _pollInFlight;
    private bool _portraitSweepRunning;

    public ObservableCollection<TrialLeaderboardRow> AllRows { get; } = new();
    public ObservableCollection<TrialLeaderboardRow> ShownRows { get; } = new();

    public TrialLeaderboardPage()
    {
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(5);
        _timer.Tick += async (_, _) => await RefreshAsync();

        InitializeComponent();
        EntryList.ItemsSource = ShownRows;
        _ = RefreshAsync();
        _timer.Start();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _pageCts = new CancellationTokenSource();
        _timer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _timer.Stop();
        _pageCts?.Cancel();
        base.OnNavigatedFrom(e);
    }

    private async void Refresh_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => await RefreshAsync();

    private void SearchBox_TextChanged(object sender, Microsoft.UI.Xaml.Controls.TextChangedEventArgs e) => ApplyFilter();

    private async void DeleteEntry_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if ((sender as Microsoft.UI.Xaml.FrameworkElement)?.Tag is not TrialLeaderboardRow row) return;

        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostTrialGlobalDeleteAsync(row.PlayerName, row.Entry.Id);
            StatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"error: {ex.Message}";
        }
    }

    private async void ResetAll_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var dlg = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "Reset Trial of the Impossible leaderboard?",
            Content = "This deletes every account's Trial runs from every saved leaderboard file. This can't be undone.",
            PrimaryButtonText = "Reset Leaderboard",
            CloseButtonText = "Cancel",
            DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        var result = await dlg.ShowAsync();
        if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary) return;

        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostTrialGlobalClearAsync();
            StatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"error: {ex.Message}";
        }
    }

    private async Task RefreshAsync()
    {
        if (_pollInFlight) return;
        _pollInFlight = true;
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.GetTrialGlobalLeaderboardAsync();
            AllRows.Clear();
            if (resp == null || resp.Ok == false)
            {
                StatusText.Text = resp?.Error ?? "leaderboard load failed";
                ApplyFilter();
                return;
            }

            foreach (var entry in resp.Entries)
                AllRows.Add(new TrialLeaderboardRow(entry));

            StatusText.Text = $"{resp.Entries.Count} run{(resp.Entries.Count == 1 ? "" : "s")}";
            ApplyFilter();
            _ = RunPortraitSweepAsync();
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

    private void ApplyFilter()
    {
        string q = (SearchBox.Text ?? "").Trim();
        ShownRows.Clear();
        foreach (var row in AllRows)
        {
            if (q.Length > 0 &&
                row.PlayerName.Contains(q, StringComparison.OrdinalIgnoreCase) == false &&
                row.HeroName.Contains(q, StringComparison.OrdinalIgnoreCase) == false &&
                row.RankText.Contains(q, StringComparison.OrdinalIgnoreCase) == false)
                continue;
            ShownRows.Add(row);
        }
    }

    // Same "warm the server cache via a byte fetch, then decode straight
    // from the /webapi/texbyname URI" pattern PhantomsPage uses for its
    // hero roster portraits, working through each row's candidate list in
    // order since not every candidate lives in the TFC stream.
    private async Task RunPortraitSweepAsync()
    {
        if (_portraitSweepRunning) return;
        _portraitSweepRunning = true;
        var ct = _pageCts?.Token ?? CancellationToken.None;
        try
        {
            string portraitBase = AppState.ServerUrl.TrimEnd('/');
            using var throttle = new SemaphoreSlim(8);
            var tasks = new System.Collections.Generic.List<Task>();
            foreach (var row in AllRows)
            {
                var candidates = row.Entry.HeroPortraitCandidates is { Count: > 0 }
                    ? row.Entry.HeroPortraitCandidates
                    : (string.IsNullOrEmpty(row.Entry.HeroPortraitPath) ? null : new System.Collections.Generic.List<string> { row.Entry.HeroPortraitPath! });
                if (row.PortraitRequested || candidates == null) continue;
                row.PortraitRequested = true;
                await throttle.WaitAsync(ct);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        foreach (string candidate in candidates)
                        {
                            byte[]? png = await _api.GetTexturePngAsync(candidate, ct);
                            if (png == null || png.Length == 0) continue;
                            string url = $"{portraitBase}/webapi/texbyname?name={Uri.EscapeDataString(candidate)}";
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                try { row.Portrait = new BitmapImage(new Uri(url)) { DecodePixelWidth = 64 }; }
                                catch { }
                            });
                            break;
                        }
                    }
                    catch { }
                    finally { throttle.Release(); }
                }, ct));
            }
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { }
        catch { }
        finally { _portraitSweepRunning = false; }
    }
}
