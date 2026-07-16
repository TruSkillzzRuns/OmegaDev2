using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OmegaDev2.Services;
using Windows.UI;

namespace OmegaDev2.Pages;

public sealed class DpsRow
{
    private static readonly Brush s_playerBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x2E, 0xC8, 0xC8));
    private static readonly Brush s_phantomBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x9B, 0x59, 0xD0));

    public string Name { get; }
    public string IdleText { get; }
    public string TotalText { get; }
    public string PeakHitText { get; }
    public string Dps10Text { get; }
    public string DpsAllText { get; }
    public double BarWidth { get; }
    public Brush BarBrush { get; }

    public DpsRow(DpsCombatant c, long maxTotal)
    {
        Name = c.Name;
        IdleText = c.SecondsSinceLastHit >= 10 ? $"  (idle {FormatDuration(c.SecondsSinceLastHit)})" : "";
        TotalText = FormatNumber(c.Total);
        PeakHitText = FormatNumber(c.PeakHit);
        Dps10Text = FormatNumber((long)c.Dps10);
        DpsAllText = FormatNumber((long)c.DpsOverall);
        // Bars scale relative to the top damage dealer; keep a sliver visible
        // for anyone on the board at all.
        BarWidth = maxTotal > 0 ? Math.Max(4.0, 420.0 * c.Total / maxTotal) : 4.0;
        BarBrush = c.IsPhantom ? s_phantomBrush : s_playerBrush;
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

            Rows.Clear();
            foreach (var c in resp.Combatants)
                Rows.Add(new DpsRow(c, maxTotal));

            long teamTotal = resp.Combatants.Sum(c => c.Total);
            double teamDps10 = resp.Combatants.Sum(c => c.Dps10);
            StatusText.Text = resp.Combatants.Count == 0
                ? $"no damage recorded yet · meter running {resp.SecondsSinceReset}s"
                : $"team: {DpsRow.FormatNumber(teamTotal)} total · {DpsRow.FormatNumber((long)teamDps10)} DPS (10s) · meter {resp.SecondsSinceReset}s";
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
