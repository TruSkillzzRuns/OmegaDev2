using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Text;
using OmegaDev2.Services;
using Windows.UI;
using Windows.UI.Text;

namespace OmegaDev2.Pages;

public sealed class WaveEnemyCard
{
    public EnemyCatalogEntry Entry { get; }
    public string Name => Entry.Name;
    public string SubLabel
    {
        get
        {
            string rank = string.IsNullOrEmpty(Entry.Rank) ? "" : Entry.Rank;
            string faction = string.IsNullOrEmpty(Entry.Faction) ? "" : Entry.Faction;
            if (rank.Length > 0 && faction.Length > 0) return $"{rank} · {faction}";
            return rank.Length > 0 ? rank : faction;
        }
    }
    public WaveEnemyCard(EnemyCatalogEntry entry) => Entry = entry;
}

// The plan list renders waves as header rows and entries as child rows.
public sealed class WavePlanRow
{
    private static readonly Brush s_header = new SolidColorBrush(Color.FromArgb(0xFF, 0x2E, 0xC8, 0xC8));
    private static readonly Brush s_entry = new SolidColorBrush(Color.FromArgb(0xFF, 0xC8, 0xC8, 0xD2));

    public string Title { get; set; } = "";
    public bool IsHeader { get; set; }
    public string RowKey { get; set; } = "";     // "waveIdx" or "waveIdx:entryIdx"

    public double TitleSize => IsHeader ? 14 : 13;
    public FontWeight TitleWeight => IsHeader ? FontWeights.SemiBold : FontWeights.Normal;
    public Brush TitleBrush => IsHeader ? s_header : s_entry;
    public Visibility RemoveVisible => Visibility.Visible;
}

// Wave Director — build a wave plan client-side, ship it to
// /webapi/arena/waves/start, watch the run live.
public sealed partial class WaveDirectorPage : Page
{
    private sealed class PlanEntry
    {
        public string? AgentRef;
        public string? AgentName;
        public bool EnemyPhantom;
        // Enemy-phantom specific: null / empty = pick a random hero server-side.
        public string? HeroRef;
        public string? HeroName;
        public int Count;
        public int Level;
    }

    // Hero choices for the enemy-phantom picker. "(random)" is index 0.
    private sealed class HeroOption
    {
        public string Label { get; init; } = "";
        public string? ProtoRef { get; init; }
    }
    private readonly List<HeroOption> _heroOptions = new();

    private readonly ServerApiClient _api = new();
    private readonly List<WaveEnemyCard> _allEnemies = new();
    public ObservableCollection<WaveEnemyCard> ShownEnemies { get; } = new();
    public ObservableCollection<WavePlanRow> PlanRows { get; } = new();

    private readonly List<List<PlanEntry>> _plan = new() { new List<PlanEntry>() };
    private readonly DispatcherQueueTimer _timer;
    private bool _pollInFlight;

    // Curated allowlist of arena-viable regions, addressed by chapter-code
    // path fragments (CH02xx, CH08xx, CH09xx) — data file identifiers, not
    // content names. Labels come from the SERVER'S region name, driven by
    // the user's loaded client data (same pattern the phantom hero picker
    // uses). Every match must also be flagged isSafe by the server: regions
    // without a waypoint entry / difficulty tier hang forever on the load
    // screen. The sterilize sweep on arrival removes native content
    // (bosses, spawn markers, transition consoles), leaving a clean stage.
    private static readonly string[] s_arenaPathMatches =
    {
        "CH0207", "CH0803", "CH0809", "CH0906", "MrSinisterBase",
    };

    private sealed class ArenaOption
    {
        public string Label { get; init; } = "";
        public string? ProtoRef { get; init; }
    }

    private readonly List<ArenaOption> _arenaOptions = new();
    private string? _arenaRef;

    public WaveDirectorPage()
    {
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += async (_, _) => await PollStatusAsync();

        InitializeComponent();
        EnemyCatalogList.ItemsSource = ShownEnemies;
        PlanList.ItemsSource = PlanRows;
        RefreshPlanRows();

        _ = PopulateArenaComboAsync();
        _ = PopulateHeroComboAsync();
        _timer.Start();
    }

    private async Task PopulateHeroComboAsync()
    {
        _heroOptions.Clear();
        _heroOptions.Add(new HeroOption { Label = "(random hero)", ProtoRef = null });
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.GetPhantomHeroesAsync();
            if (resp != null)
            {
                foreach (var hero in resp.Heroes.OrderBy(h =>
                    string.IsNullOrEmpty(h.DisplayName) ? h.Name : h.DisplayName,
                    StringComparer.OrdinalIgnoreCase))
                {
                    string label = string.IsNullOrEmpty(hero.DisplayName) ? hero.Name : hero.DisplayName!;
                    _heroOptions.Add(new HeroOption { Label = label, ProtoRef = hero.ProtoRef });
                }
            }
        }
        catch { /* keep just (random) */ }

        PhantomHeroCombo.ItemsSource = _heroOptions;
        PhantomHeroCombo.SelectedIndex = 0;
    }

    private async Task PopulateArenaComboAsync()
    {
        // Resolve the curated allowlist against the actual region list the
        // server exposes — any entry the server doesn't have (older client
        // data, custom fork) is silently dropped.
        _arenaOptions.Clear();
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.GetRegionListAsync();
            var byMatch = resp?.Regions ?? new List<RegionListEntry>();

            _arenaOptions.Add(new ArenaOption { Label = "(none — spawn where you stand)", ProtoRef = null });

            // Every match must be flagged isSafe (unsafe regions hang the
            // load screen forever). The label is the region name straight
            // from the server's response — source contains only data-file
            // identifiers, not content names.
            foreach (string match in s_arenaPathMatches)
            {
                var region = byMatch.FirstOrDefault(r =>
                    r.IsSafe && (r.Path.Contains(match, StringComparison.OrdinalIgnoreCase)
                              || r.Name.Replace(" ", "").Contains(match, StringComparison.OrdinalIgnoreCase)));
                if (region == null) continue;
                _arenaOptions.Add(new ArenaOption { Label = region.Name, ProtoRef = region.ProtoRef });
            }
        }
        catch
        {
            // Server unreachable — offer just the "none" option so Start
            // Run still works locally without an arena warp.
            if (_arenaOptions.Count == 0)
                _arenaOptions.Add(new ArenaOption { Label = "(none — spawn where you stand)", ProtoRef = null });
        }

        ArenaCombo.ItemsSource = _arenaOptions;
        ArenaCombo.SelectedIndex = 0;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _timer.Stop();
        base.OnNavigatedFrom(e);
    }

    private string TargetPlayer => string.IsNullOrWhiteSpace(PlayerBox.Text) ? "*" : PlayerBox.Text.Trim();

    // Ranks the game applies to actual combat mobs. Anything outside this
    // set is either a companion (TeamUp, InvulnerablePet), an objective
    // marker (PvPTower, PvPDefender, VisibleAlwaysHealth), or a scenery
    // prop (Prop) — none belong in a wave.
    private static readonly HashSet<string> s_combatRanks = new(StringComparer.OrdinalIgnoreCase)
    {
        "Popcorn", "Minion", "Elite", "EliteNamed", "Champion",
        "MiniBoss", "Boss", "BossNoOverheadInfo", "GroupBoss",
        "ShowdownBoss", "FakeBoss",
    };
    private static bool IsCombatantRank(string rank)
        => string.IsNullOrWhiteSpace(rank) == false && s_combatRanks.Contains(rank);

    // ---------------- Enemy catalog ----------------

    private async void LoadEnemies_Click(object sender, RoutedEventArgs e)
    {
        LoadBtn.IsEnabled = false;
        StatusText.Text = "loading enemy catalog…";
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.GetEnemyCatalogAsync();
            if (resp == null || resp.Entries.Count == 0)
            {
                StatusText.Text = "no enemies — server offline?";
                return;
            }

            // Only actual fightable enemies. Filter by the rank enums the
            // game data uses for real combatants — this excludes TeamUp
            // companions (Agent13, Rocket, …), InvulnerablePets, PvP
            // objective towers, mission-marker props, and the assorted
            // rank-less quest/vendor NPCs that slip past the server's
            // path filter. Also hides cut/deprecated content trees.
            _allEnemies.Clear();
            int hidden = 0;
            foreach (var entry in resp.Entries.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (IsCombatantRank(entry.Rank) == false)
                {
                    hidden++;
                    continue;
                }
                if (entry.Faction != null && entry.Faction.StartsWith("zzz", StringComparison.OrdinalIgnoreCase))
                {
                    hidden++;
                    continue;
                }
                if (entry.Path.IndexOf("/zzz", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hidden++;
                    continue;
                }
                _allEnemies.Add(new WaveEnemyCard(entry));
            }
            ApplyFilter();
            StatusText.Text = hidden > 0
                ? $"{_allEnemies.Count:N0} enemies ({hidden:N0} non-combat hidden)"
                : $"{_allEnemies.Count:N0} enemies";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"error: {ex.Message}";
        }
        finally
        {
            LoadBtn.IsEnabled = true;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string q = SearchBox.Text?.Trim() ?? "";
        ShownEnemies.Clear();
        int shown = 0;
        foreach (var card in _allEnemies)
        {
            if (q.Length > 0 &&
                card.Name.Contains(q, StringComparison.OrdinalIgnoreCase) == false &&
                card.Entry.Faction.Contains(q, StringComparison.OrdinalIgnoreCase) == false)
                continue;
            ShownEnemies.Add(card);
            if (++shown >= 500) break; // keep the list snappy; refine to narrow
        }
        CatalogHint.Text = shown >= 500 ? "showing first 500 — refine the search" : "select an enemy, then add it to the wave";
    }

    // ---------------- Plan building ----------------

    private void RefreshPlanRows()
    {
        PlanRows.Clear();
        for (int w = 0; w < _plan.Count; w++)
        {
            int totalInWave = _plan[w].Sum(x => x.Count);
            PlanRows.Add(new WavePlanRow
            {
                Title = $"WAVE {w + 1}   ({totalInWave} enemies)",
                IsHeader = true,
                RowKey = w.ToString(),
            });
            for (int i = 0; i < _plan[w].Count; i++)
            {
                PlanEntry entry = _plan[w][i];
                string label = entry.EnemyPhantom
                    ? $"     {entry.Count}× Enemy Phantom — {entry.HeroName ?? "random hero"}{(entry.Level > 0 ? $" (lvl {entry.Level})" : "")}"
                    : $"     {entry.Count}× {entry.AgentName}";
                PlanRows.Add(new WavePlanRow { Title = label, IsHeader = false, RowKey = $"{w}:{i}" });
            }
        }
        int totalWaves = _plan.Count(w => w.Count > 0);
        int totalEnemies = _plan.Sum(w => w.Sum(x => x.Count));
        BuilderTitle.Text = $"Wave Plan — {totalWaves} wave(s), {totalEnemies} enemies";
    }

    private void NewWave_Click(object sender, RoutedEventArgs e)
    {
        if (_plan.Count > 0 && _plan[^1].Count == 0) return; // don't stack empties
        _plan.Add(new List<PlanEntry>());
        RefreshPlanRows();
    }

    private void ClearPlan_Click(object sender, RoutedEventArgs e)
    {
        _plan.Clear();
        _plan.Add(new List<PlanEntry>());
        RefreshPlanRows();
    }

    private void AddEnemyToWave_Click(object sender, RoutedEventArgs e)
    {
        if (EnemyCatalogList.SelectedItem is not WaveEnemyCard card)
        {
            StatusText.Text = "select an enemy in the catalog first";
            return;
        }
        _plan[^1].Add(new PlanEntry
        {
            AgentRef = card.Entry.ProtoRef,
            AgentName = card.Name,
            Count = (int)AddCountBox.Value,
        });
        RefreshPlanRows();
    }

    private void AddPhantomsToWave_Click(object sender, RoutedEventArgs e)
    {
        var chosen = PhantomHeroCombo.SelectedItem as HeroOption;
        _plan[^1].Add(new PlanEntry
        {
            EnemyPhantom = true,
            HeroRef = chosen?.ProtoRef,
            HeroName = chosen?.ProtoRef == null ? null : chosen.Label,
            Count = (int)PhantomCountBox.Value,
            Level = (int)PhantomLevelBox.Value,
        });
        RefreshPlanRows();
    }

    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string key) return;

        string[] parts = key.Split(':');
        int w = int.Parse(parts[0]);
        if (w >= _plan.Count) return;

        if (parts.Length == 1)
        {
            // Header row — remove the whole wave (keep at least one).
            _plan.RemoveAt(w);
            if (_plan.Count == 0) _plan.Add(new List<PlanEntry>());
        }
        else
        {
            int i = int.Parse(parts[1]);
            if (i < _plan[w].Count) _plan[w].RemoveAt(i);
        }
        RefreshPlanRows();
    }

    // ---------------- Arena picker ----------------

    private void ArenaCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _arenaRef = (ArenaCombo.SelectedItem as ArenaOption)?.ProtoRef;
    }

    // ---------------- Run control ----------------

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var waves = _plan.Where(w => w.Count > 0)
            .Select(w => new
            {
                entries = w.Select(x => new
                {
                    agentRef = x.AgentRef,
                    heroRef = x.HeroRef,
                    enemyPhantom = x.EnemyPhantom,
                    count = x.Count,
                    level = x.Level,
                }).ToArray(),
            }).ToArray();

        if (waves.Length == 0)
        {
            RunStatusText.Text = "the plan is empty — add enemies first";
            return;
        }

        StartBtn.IsEnabled = false;
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostWavesStartAsync(new
            {
                playerName = TargetPlayer,
                intermissionMs = (int)(IntermissionBox.Value * 1000),
                arenaRegionRef = _arenaRef,
                clearArena = _arenaRef != null && ClearArenaCheck.IsChecked == true,
                waves,
            });
            RunStatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
        }
        catch (Exception ex)
        {
            RunStatusText.Text = $"error: {ex.Message}";
        }
        finally
        {
            StartBtn.IsEnabled = true;
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostWavesStopAsync(TargetPlayer, cleanup: true);
            RunStatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
        }
        catch (Exception ex)
        {
            RunStatusText.Text = $"error: {ex.Message}";
        }
    }

    // ---------------- Live status ----------------

    private async System.Threading.Tasks.Task PollStatusAsync()
    {
        if (_pollInFlight) return;
        _pollInFlight = true;
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.GetWavesStatusAsync(TargetPlayer);
            var s = resp?.Status;
            if (resp == null || resp.Ok == false || s == null)
            {
                LiveStateText.Text = resp?.Error ?? "server unreachable";
                return;
            }

            LiveWaveText.Text = s.TotalWaves > 0 ? $"wave {s.Wave} / {s.TotalWaves}" : "wave — / —";
            LiveStateText.Text = s.State switch
            {
                "WarpingToArena" => "warping to arena…",
                "SettlingArena" => "sterilizing arena…",
                "Fighting" => "FIGHT!",
                "Intermission" => $"next wave in {Math.Ceiling(s.IntermissionRemainingMs / 1000.0):0}s",
                "Done" => "run complete — you survived",
                _ => "idle",
            };
            LiveAliveText.Text = s.Alive.ToString();
            LiveKillsText.Text = s.Kills.ToString();
            LiveTimerText.Text = s.Active ? $"run time {s.RunSeconds / 60}m{s.RunSeconds % 60:00}s · {s.SpawnedTotal} spawned" : "";
        }
        catch { /* next poll */ }
        finally { _pollInFlight = false; }
    }
}
