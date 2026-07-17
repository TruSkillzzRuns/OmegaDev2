using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Text;
using Windows.UI.Text;
using OmegaDev2.Services;
using Windows.UI;

namespace OmegaDev2.Pages;

public sealed class DpsRow
{
    // Rank-tiered bar colors: gold / silver / bronze / everyone else (teal for
    // the player, purple for phantoms, same scheme as before but dimmer).
    private static readonly Brush s_goldBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xC1, 0x07));
    private static readonly Brush s_silverBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xC7, 0xD1, 0xDB));
    private static readonly Brush s_bronzeBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xCD, 0x7F, 0x32));
    private static readonly Brush s_playerBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x2E, 0xC8, 0xC8));
    private static readonly Brush s_phantomBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x9B, 0x59, 0xD0));
    private static readonly Brush s_leaderRowBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xC1, 0x07));
    private static readonly Brush s_transparentBrush = new SolidColorBrush(Color.FromArgb(0x00, 0x00, 0x00, 0x00));

    public string Name { get; }
    public string IdleText { get; }
    public string RankText { get; }
    public string CrownText { get; }
    public string PercentText { get; }
    public string TotalText { get; }
    public string PeakHitText { get; }
    public string Dps10Text { get; }
    public string DpsAllText { get; }
    public double BarWidth { get; }
    public Brush BarBrush { get; }
    public Brush RowBackground { get; }
    public FontWeight NameWeight { get; }

    public DpsRow(DpsCombatant c, long maxTotal, long teamTotal, int rank)
    {
        Name = c.Name;
        IdleText = c.SecondsSinceLastHit >= 10 ? $"  (idle {FormatDuration(c.SecondsSinceLastHit)})" : "";
        RankText = $"#{rank}";
        CrownText = rank == 1 && c.Total > 0 ? "👑 " : "";
        PercentText = teamTotal > 0 ? $"{100.0 * c.Total / teamTotal:0.#}%" : "—";
        TotalText = FormatNumber(c.Total);
        PeakHitText = FormatNumber(c.PeakHit);
        Dps10Text = FormatNumber((long)c.Dps10);
        DpsAllText = FormatNumber((long)c.DpsOverall);
        // Bars scale relative to the top damage dealer; keep a sliver visible
        // for anyone on the board at all.
        BarWidth = maxTotal > 0 ? Math.Max(4.0, 420.0 * c.Total / maxTotal) : 4.0;
        BarBrush = rank switch
        {
            1 => s_goldBrush,
            2 => s_silverBrush,
            3 => s_bronzeBrush,
            _ => c.IsPhantom ? s_phantomBrush : s_playerBrush,
        };
        RowBackground = rank == 1 && c.Total > 0 ? s_leaderRowBrush : s_transparentBrush;
        NameWeight = rank == 1 && c.Total > 0 ? FontWeights.SemiBold : FontWeights.Normal;
    }

    public static string FormatNumber(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1_000_000_000.0:0.##}B",
        >= 1_000_000 => $"{n / 1_000_000.0:0.##}M",
        >= 10_000 => $"{n / 1_000.0:0.#}K",
        _ => n.ToString("N0"),
    };

    private static string FormatDuration(long seconds)
        => seconds >= 60 ? $"{seconds / 60}m{seconds % 60:00}s" : $"{seconds}s";
}

// DPS Meter — polls /webapi/dps once a second while Live is on. Rows are
// rebuilt per poll (a handful of combatants, no virtualization pressure).
public sealed partial class DpsMeterPage : Page
{
    private readonly ServerApiClient _api = new();
    private readonly DispatcherQueueTimer _timer;
    private bool _fetchInFlight;
    private List<DpsCombatant> _lastCombatants = new();
    // Armed whenever the top combatant is actively taking hits; disarmed the
    // moment an auto-save actually fires, so a parse only gets auto-saved
    // once per idle period instead of every poll tick while it sits idle.
    // Re-arms automatically the next time combat resumes.
    private bool _autoSaveArmed = true;
    private const long AutoSaveIdleSeconds = 10;
    public ObservableCollection<DpsRow> Rows { get; } = new();

    public DpsMeterPage()
    {
        // Timer exists before InitializeComponent — the Live toggle is
        // checked in XAML, so its Checked handler fires during parse.
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += async (_, _) => await FetchAsync();

        InitializeComponent();
        DpsList.ItemsSource = Rows;

        _timer.Start();
        _ = FetchAsync();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (LiveToggle.IsChecked == true) _timer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _timer.Stop();
        base.OnNavigatedFrom(e);
    }

    private async System.Threading.Tasks.Task FetchAsync()
    {
        if (_fetchInFlight) return;
        _fetchInFlight = true;
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            string player = string.IsNullOrWhiteSpace(PlayerBox.Text) ? "*" : PlayerBox.Text.Trim();
            var resp = await _api.GetDpsAsync(player);
            if (resp == null || resp.Ok == false)
            {
                StatusText.Text = resp?.Error ?? "server unreachable";
                return;
            }

            long maxTotal = resp.Combatants.Count > 0 ? resp.Combatants.Max(c => c.Total) : 0;
            _lastCombatants = resp.Combatants;
            long teamTotal = resp.Combatants.Sum(c => c.Total);

            Rows.Clear();
            for (int i = 0; i < resp.Combatants.Count; i++)
                Rows.Add(new DpsRow(resp.Combatants[i], maxTotal, teamTotal, i + 1));

            double teamDps10 = resp.Combatants.Sum(c => c.Dps10);
            StatusText.Text = resp.Combatants.Count == 0
                ? $"no damage recorded yet · meter running {resp.SecondsSinceReset}s"
                : $"team: {DpsRow.FormatNumber(teamTotal)} total · {DpsRow.FormatNumber((long)teamDps10)} DPS (10s) · meter {resp.SecondsSinceReset}s";

            if (AutoSaveCheck.IsChecked == true)
                await MaybeAutoSaveAsync(player);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _fetchInFlight = false;
        }
    }

    private void LiveToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (_timer == null) return; // parse-time firing
        LiveToggle.Content = "Live";
        _timer.Start();
        _ = FetchAsync();
    }

    private void LiveToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_timer == null) return;
        LiveToggle.Content = "Paused";
        _timer.Stop();
    }

    // Auto-commits the top parse to the Leaderboard once combat has gone
    // idle, without needing a manual "Save to Leaderboard" click. Reuses
    // the exact same commit call the button does. Guarded by
    // _autoSaveArmed so it fires exactly once per idle period — re-arms
    // the moment fresh damage comes in (new fight), not on a timer.
    private async System.Threading.Tasks.Task MaybeAutoSaveAsync(string player)
    {
        var top = _lastCombatants.FirstOrDefault();
        if (top == null || top.DpsOverall <= 0)
        {
            _autoSaveArmed = true;
            return;
        }

        if (top.SecondsSinceLastHit < AutoSaveIdleSeconds)
        {
            _autoSaveArmed = true;
            return;
        }

        if (_autoSaveArmed == false) return;
        _autoSaveArmed = false;

        try
        {
            var resp = await _api.PostLeaderboardCommitDpsAsync(player, top.Name, top.DpsOverall);
            StatusText.Text = $"auto-saved: {resp?.Message ?? resp?.Error ?? "no response"}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"auto-save error: {ex.Message}";
        }
    }

    private async void SaveToLeaderboard_Click(object sender, RoutedEventArgs e)
    {
        // Combatants are already sorted by Total damage descending (server-side).
        var top = _lastCombatants.FirstOrDefault();
        if (top == null || top.DpsOverall <= 0)
        {
            StatusText.Text = "nothing to save — no damage recorded yet";
            return;
        }

        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            string player = string.IsNullOrWhiteSpace(PlayerBox.Text) ? "*" : PlayerBox.Text.Trim();
            var resp = await _api.PostLeaderboardCommitDpsAsync(player, top.Name, top.DpsOverall);
            StatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"error: {ex.Message}";
        }
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            await _api.PostDpsResetAsync();
            Rows.Clear();
            StatusText.Text = "meter reset";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }
}
