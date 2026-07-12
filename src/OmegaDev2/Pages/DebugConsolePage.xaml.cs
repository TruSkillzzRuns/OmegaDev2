using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace OmegaDev2.Pages;

// Debug Console — defaults to QUIET mode (warnings + errors only).
// Top stat tiles give an at-a-glance health view. The log list stays empty
// when the server is healthy ("All quiet" state). Toggle "Show all" to drop
// to Info level and see verbose activity.
public sealed partial class DebugConsolePage : Page
{
    private const string ServerBaseUrl = "http://localhost:8080";
    private const string BearerToken = "6A8D162E6DC12BEBEA63E4135EA9D7E8";
    // Poll less aggressively in quiet mode — there's nothing changing most of
    // the time, so 1s is plenty. Show-all mode bumps to 250ms for responsiveness.
    private int _pollIntervalMs = 1000;
    private const int MaxVisibleEntries = 2000;
    // Coalesce repeated (logger,message) pairs that arrive in a burst.
    private const int CoalesceWindowMs = 1500;

    private readonly ObservableCollection<LogEntryViewModel> _visible = new();
    private readonly List<LogEntryViewModel> _allBuffered = new();
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource _pollCts;
    private long _lastSeq = -1;
    private string _searchTerm = "";
    private string _minLevel = "Warn";   // default: quiet
    private bool _quietMode = true;
    private DateTime _firstSeen = DateTime.MinValue;
    private int _errorsLast5Min;
    private int _warningsLast5Min;
    private readonly List<(DateTime, string)> _recentLevels = new();   // for time-windowed counts

    public DebugConsolePage()
    {
        InitializeComponent();
        // Set toggle defaults AFTER InitializeComponent so the Toggled event
        // handler (which reads other named elements) doesn't fire on a half-
        // initialized page. Setting IsOn="True" in XAML caused a XAML parse
        // crash because Toggled fires during init when no refs are bound yet.
        QuietModeToggle.IsOn = true;
        AutoScrollToggle.IsOn = true;
        LogList.ItemsSource = _visible;
        Loaded += OnLoaded;
        Unloaded += (_, _) => StopStreaming();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { await StartStreamingAsync(); }
        catch (Exception ex) { SetStatus($"Init failed: {ex.Message}"); }
    }

    private Task StartStreamingAsync()
    {
        StopStreaming();
        _pollCts = new CancellationTokenSource();
        var ct = _pollCts.Token;
        SetStatus("Connecting…");
        _ = Task.Run(async () => await PollLoopAsync(ct), ct);
        _ = Task.Run(async () => await StatsTickAsync(ct), ct);
        return Task.CompletedTask;
    }

    private void StopStreaming()
    {
        _pollCts?.Cancel();
        _pollCts = null;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", BearerToken);
        int consecutiveErrors = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string url = $"{ServerBaseUrl}/webapi/debug/logs?since={_lastSeq}&max=500&min={_minLevel}";
                var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    consecutiveErrors++;
                    _ui.TryEnqueue(() => SetHealth(false, $"Server: HTTP {(int)resp.StatusCode}"));
                    await Task.Delay(Math.Min(5000, 1000 * consecutiveErrors), ct).ConfigureAwait(false);
                    continue;
                }
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                long latestSeq = doc.RootElement.TryGetProperty("latestSeq", out var ls) ? ls.GetInt64() : -1;
                var batch = new List<LogEntryViewModel>();
                if (doc.RootElement.TryGetProperty("entries", out var arr))
                {
                    foreach (var e in arr.EnumerateArray())
                    {
                        batch.Add(new LogEntryViewModel
                        {
                            Seq     = e.GetProperty("seq").GetInt64(),
                            Ts      = e.GetProperty("ts").GetString() ?? "",
                            Level   = e.GetProperty("level").GetString() ?? "",
                            Logger  = e.GetProperty("logger").GetString() ?? "",
                            Message = e.GetProperty("message").GetString() ?? "",
                        });
                    }
                }
                if (batch.Count > 0) _lastSeq = batch[^1].Seq;
                else if (latestSeq > _lastSeq) _lastSeq = latestSeq;
                consecutiveErrors = 0;
                if (_firstSeen == DateTime.MinValue) _firstSeen = DateTime.UtcNow;
                _ui.TryEnqueue(() => AppendBatch(batch));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                consecutiveErrors++;
                _ui.TryEnqueue(() => SetHealth(false, $"Server unreachable ({ex.GetType().Name})"));
                await Task.Delay(Math.Min(5000, 500 * consecutiveErrors), ct).ConfigureAwait(false);
                continue;
            }
            try { await Task.Delay(_pollIntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Refresh the stat tiles + sliding-window counters once per second.
    private async Task StatsTickAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            _ui.TryEnqueue(RefreshStats);
        }
    }

    private void RefreshStats()
    {
        try
        {
            RefreshStatsCore();
        }
        catch { /* swallow — UI nice-to-have */ }
    }
    private void RefreshStatsCore()
    {
        if (HealthDot == null || ErrorsValue == null) return;
        // Trim outside 5-minute window
        var cutoff = DateTime.UtcNow.AddMinutes(-5);
        _recentLevels.RemoveAll(p => p.Item1 < cutoff);
        _errorsLast5Min   = _recentLevels.Count(p => p.Item2 == "Error");
        _warningsLast5Min = _recentLevels.Count(p => p.Item2 == "Warn");
        ErrorsValue.Text   = _errorsLast5Min.ToString();
        WarningsValue.Text = _warningsLast5Min.ToString();
        TotalValue.Text    = _allBuffered.Count.ToString();
        if (_firstSeen != DateTime.MinValue)
        {
            var up = DateTime.UtcNow - _firstSeen;
            UptimeValue.Text = up.TotalHours >= 1
                ? $"{(int)up.TotalHours}h {up.Minutes}m"
                : $"{up.Minutes}m {up.Seconds}s";
        }
        // Health roll-up: red if any errors in last minute, yellow if warnings, green otherwise.
        var lastMinute = DateTime.UtcNow.AddMinutes(-1);
        bool err1m = _recentLevels.Any(p => p.Item1 >= lastMinute && p.Item2 == "Error");
        bool warn1m = _recentLevels.Any(p => p.Item1 >= lastMinute && p.Item2 == "Warn");
        if (err1m)        SetHealth(false, "Errors in last minute");
        else if (warn1m)  SetHealth(true,  "Healthy (warnings present)", warning: true);
        else              SetHealth(true,  "Healthy");
    }

    private void AppendBatch(List<LogEntryViewModel> batch)
    {
        foreach (var entry in batch)
        {
            entry.LevelBrush = LevelToBrush(entry.Level);
            _allBuffered.Add(entry);
            _recentLevels.Add((DateTime.UtcNow, entry.Level));

            // Coalesce: if the most recent visible entry is identical
            // (same logger+message+level) and arrived within the window,
            // bump its repeat count instead of adding a new row.
            if (_visible.Count > 0)
            {
                var last = _visible[^1];
                if (last.Level == entry.Level
                 && last.Logger == entry.Logger
                 && last.Message == entry.Message
                 && (DateTime.UtcNow - last.LastUpdated).TotalMilliseconds < CoalesceWindowMs)
                {
                    last.RepeatCount++;
                    last.LastUpdated = DateTime.UtcNow;
                    last.NotifyRepeat();
                    continue;
                }
            }

            if (MatchesFilter(entry))
            {
                entry.LastUpdated = DateTime.UtcNow;
                _visible.Add(entry);
            }
        }
        while (_allBuffered.Count > MaxVisibleEntries) _allBuffered.RemoveAt(0);
        while (_visible.Count > MaxVisibleEntries) _visible.RemoveAt(0);

        EmptyState.Visibility = _visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (AutoScrollToggle.IsOn && _visible.Count > 0)
            LogList.ScrollIntoView(_visible[^1]);
    }

    private bool MatchesFilter(LogEntryViewModel e)
    {
        if (string.IsNullOrEmpty(_searchTerm)) return true;
        return e.Message.Contains(_searchTerm, StringComparison.OrdinalIgnoreCase)
            || e.Logger.Contains(_searchTerm, StringComparison.OrdinalIgnoreCase);
    }

    private void Refilter()
    {
        _visible.Clear();
        foreach (var e in _allBuffered) if (MatchesFilter(e)) _visible.Add(e);
        EmptyState.Visibility = _visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static SolidColorBrush LevelToBrush(string level) => level switch
    {
        "Trace" => new SolidColorBrush(Color.FromArgb(255, 100, 100, 110)),
        "Debug" => new SolidColorBrush(Color.FromArgb(255, 76, 109, 175)),
        "Info"  => new SolidColorBrush(Color.FromArgb(255, 60, 130, 90)),
        "Warn"  => new SolidColorBrush(Color.FromArgb(255, 200, 140, 30)),
        "Error" => new SolidColorBrush(Color.FromArgb(255, 200, 60, 60)),
        _       => new SolidColorBrush(Color.FromArgb(255, 90, 90, 100)),
    };

    private void SetHealth(bool ok, string statusText, bool warning = false)
    {
        HealthDot.Fill = new SolidColorBrush(
            !ok       ? Color.FromArgb(255, 200, 80, 80)
            : warning ? Color.FromArgb(255, 220, 170, 60)
            :           Color.FromArgb(255, 92, 184, 92));
        HealthValue.Text = !ok ? "Down" : (warning ? "Watch" : "OK");
        SetStatus(statusText);
    }

    private void SetStatus(string s) => StatusText.Text = s;

    // ---- event handlers ----
    private void QuietMode_Toggled(object sender, RoutedEventArgs e)
    {
        _quietMode = QuietModeToggle.IsOn;
        if (_quietMode)
        {
            _minLevel = "Warn";
            _pollIntervalMs = 1000;
            FiltersBar.Visibility = Visibility.Collapsed;
            HeaderSubtitle.Text = "Quiet by default — warnings + errors only. Toggle 'Show all' to see everything.";
            EmptyTitle.Text = "All quiet.";
            EmptySubtitle.Text = "No warnings or errors. Toggle 'Show all' to see verbose activity.";
        }
        else
        {
            _minLevel = "Info";
            _pollIntervalMs = 500;
            FiltersBar.Visibility = Visibility.Visible;
            HeaderSubtitle.Text = "Showing all activity — filters available below.";
            EmptyTitle.Text = "Waiting for activity…";
            EmptySubtitle.Text = "No log lines match the current filter yet.";
        }
        // Force a re-poll quickly so the new min level takes effect.
        _ = StartStreamingAsync();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _allBuffered.Clear();
        _visible.Clear();
        EmptyState.Visibility = Visibility.Visible;
        SetStatus("Visible log cleared.");
    }
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        foreach (var entry in _visible)
        {
            sb.Append('[').Append(entry.Ts).Append("] [").Append(entry.Level).Append("] [")
              .Append(entry.Logger).Append("] ").Append(entry.Message);
            if (entry.RepeatCount > 1) sb.Append("  ×").Append(entry.RepeatCount);
            sb.AppendLine();
        }
        var dp = new DataPackage(); dp.SetText(sb.ToString()); Clipboard.SetContent(dp);
        SetStatus($"Copied {_visible.Count} lines.");
    }
    private void LevelFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LevelFilter.SelectedItem is ComboBoxItem ci && ci.Content is string s) _minLevel = s;
    }
    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _searchTerm = (sender.Text ?? "").Trim();
        Refilter();
    }
    private void QuickFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag)
        {
            SearchBox.Text = tag;
            _searchTerm = tag; Refilter();
        }
    }
    private void QuickFilterClear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = ""; _searchTerm = ""; Refilter();
    }
}

public sealed class LogEntryViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public long Seq { get; init; }
    public string Ts { get; init; } = "";
    public string Level { get; init; } = "";
    public string Logger { get; init; } = "";
    public string Message { get; init; } = "";
    public SolidColorBrush LevelBrush { get; set; }
    public DateTime LastUpdated { get; set; }
    public int RepeatCount { get; set; } = 1;
    public string RepeatBadge => $"×{RepeatCount}";
    public Visibility RepeatBadgeVisible => RepeatCount > 1 ? Visibility.Visible : Visibility.Collapsed;
    public void NotifyRepeat()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RepeatBadge)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RepeatBadgeVisible)));
    }
}
