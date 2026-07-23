using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OmegaDev2.Services;

namespace OmegaDev2.Pages;

// Endless Challenge — a standalone rogue-lite tool split out of Wave
// Director (which still has the full manual wave-plan builder). Shares
// the same server-side engine (StartEndlessChallenge et al.) and the same
// enemy-catalog/phantom-hero/arena-picker conventions, but keeps its own
// copies of that plumbing rather than depending on WaveDirectorPage.
public sealed partial class EndlessChallengePage : Page
{
    private sealed class HeroOption
    {
        public string Label { get; init; } = "";
        public string? ProtoRef { get; init; }
    }
    private readonly List<HeroOption> _heroOptions = new();

    private sealed class ArenaOption
    {
        public string Label { get; init; } = "";
        public string? ProtoRef { get; init; }
    }
    private readonly List<ArenaOption> _arenaOptions = new();
    private string? _arenaRef;

    // Same curated arena allowlist as Wave Director — see that page for
    // the reasoning (data-file identifiers, not content names; every
    // match must also be flagged isSafe by the server).
    private static readonly string[] s_arenaPathMatches =
    {
        // Original 5.
        "CH0207", "CH0803", "CH0809", "CH0906", "MrSinisterBase",
        // NOTE: XDefense, HoloSim, UltronRaid, SurturRaid, AxisRaid were
        // tried and removed (2026-07-23) — confirmed live these all go
        // through the game's matchmaking/party-invite flow (accept an
        // invite to warp) instead of a direct instance warp, so landing in
        // one starts the region's OWN native encounter (e.g. a normal
        // Holo-Sim run), not an empty stage for our waves.
        // Other isolated single-encounter boss-room instances.
        "CH0206", "CH0409", "CH0503", "CH0605", "CH0606", "CH0705",
        "CH0707", "CH0808", "CH0903",
        // REMOVED (2026-07-23): CosmicGate, DrStrangeTimesSquare, and all
        // Daily* Terminal remixes (DailyGSinisterLab/Taskmaster/DoomCastle/
        // AsgardINST/AIMFacility/StrykerBunker) — same DailyQueue/Challenges
        // RegionQueueMethod flag (Teleporter.cs's IsQueueRegion check) that
        // broke HoloSim/the raids, so pulled preemptively without waiting to
        // reconfirm each one live.
        // Chapter 1-9 single-encounter instances (remake story), same shape
        // as the confirmed-working originals above.
        "CH0101HellsKitchen", "CH0102PowerPlant", "CH0103NYPD", "CH0104Subway",
        "CH0105Nightclub", "CH0106KPWarehouse",
        "CH0201ShippingYard", "CH0202HoodSightingContainer", "CH0203RhinoBarge",
        "CH0204Q36AIMLab", "CH0205Construction", "CH0208Cannery", "CH0209HoodsHideout",
        "CH0302HydraOutpost", "CH0303Watermill", "CH0304PoisonGlade",
        "CH0305ReconPost", "CH0306PrincessBar", "CH0307HandTower",
        "CH0403MGHStorage", "CH0404MGHFactory", "CH0405WaxMuseum",
        "CH0406Subway", "CH0407NYPDRooftop", "CH0408MaggiaRestaurant", "CH0410FiskTower",
        "CH0502MutantWarehouse", "CH0504PurifierChurch",
        "CH0602DeepCavern", "CH0603CircusSideshow", "CH0604AIMWeaponsLab",
        "CH0702SauronCaves", "CH0703BroodCaves", "CH0706MutateCaves",
        "CH0801AIMWeaponFacility", "CH0802HYDRAIsland",
        "CH0902NorwayDarkForest", "CH0905Canal",
        // Original (pre-remake) Story single instances, same category as
        // the confirmed-working MrSinisterBase.
        "HellsKitchen01Region", "NightclubRegion", "BrooklynRegion",
        "HellsKitchen02RedlightRegion", "UpperEastSideRegion", "FiskTowerRegion",
        // One-Shot Stories — single-encounter instances, not queued content.
        "WakandaP1RegionL60",
    };

    private static readonly HashSet<string> s_combatRanks = new(StringComparer.OrdinalIgnoreCase)
    {
        "Popcorn", "Minion", "Elite", "EliteNamed", "Champion",
        "MiniBoss", "Boss", "BossNoOverheadInfo", "GroupBoss",
        "ShowdownBoss", "FakeBoss",
    };
    private static bool IsCombatantRank(string rank)
        => string.IsNullOrWhiteSpace(rank) == false && s_combatRanks.Contains(rank);

    private readonly ServerApiClient _api = new();
    private readonly List<WaveEnemyCard> _allEnemies = new();
    public ObservableCollection<WaveEnemyCard> ShownEnemies { get; } = new();

    private readonly DispatcherQueueTimer _timer;
    private bool _pollInFlight;
    private string? _lastPolledState;

    public EndlessChallengePage()
    {
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += async (_, _) => await PollStatusAsync();

        InitializeComponent();
        EnemyCatalogList.ItemsSource = ShownEnemies;

        _ = PopulateArenaComboAsync();
        _ = PopulateHeroComboAsync();
        _timer.Start();
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
    private string? RewardLootTableRefText_ => string.IsNullOrWhiteSpace(RewardLootTableRefText.Text) ? null : RewardLootTableRefText.Text.Trim();

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

            _allEnemies.Clear();
            int hidden = 0;
            foreach (var entry in resp.Entries.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (IsCombatantRank(entry.Rank) == false) { hidden++; continue; }
                if (entry.Faction != null && entry.Faction.StartsWith("zzz", StringComparison.OrdinalIgnoreCase)) { hidden++; continue; }
                if (entry.Path.IndexOf("/zzz", StringComparison.OrdinalIgnoreCase) >= 0) { hidden++; continue; }
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
            if (++shown >= 500) break;
        }
        CatalogHint.Text = shown >= 500 ? "showing first 500 — refine the search" : "select an enemy for the 'Regular enemy' source";
    }

    // ---------------- Hero / arena pickers ----------------

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

    private async void RefreshArenas_Click(object sender, RoutedEventArgs e) => await PopulateArenaComboAsync();

    private async Task PopulateArenaComboAsync()
    {
        _arenaOptions.Clear();
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.GetRegionListAsync();
            if (resp == null)
            {
                ArenaStatusText.Text = "no response from server — is it running?";
                _arenaOptions.Add(new ArenaOption { Label = "(none — spawn where you stand)", ProtoRef = null });
                ArenaCombo.ItemsSource = _arenaOptions;
                ArenaCombo.SelectedIndex = 0;
                return;
            }

            var byMatch = resp.Regions ?? new List<RegionListEntry>();
            _arenaOptions.Add(new ArenaOption { Label = "(none — spawn where you stand)", ProtoRef = null });

            int matched = 0;
            foreach (string match in s_arenaPathMatches)
            {
                var region = byMatch.FirstOrDefault(r =>
                    r.IsSafe && (r.Path.Contains(match, StringComparison.OrdinalIgnoreCase)
                              || r.Name.Replace(" ", "").Contains(match, StringComparison.OrdinalIgnoreCase)));
                if (region == null) continue;
                _arenaOptions.Add(new ArenaOption { Label = region.Name, ProtoRef = region.ProtoRef });
                matched++;
            }

            ArenaStatusText.Text = $"server returned {byMatch.Count} region(s) total, {matched}/{s_arenaPathMatches.Length} curated arenas matched";
        }
        catch (Exception ex)
        {
            ArenaStatusText.Text = $"error: {ex.Message}";
            if (_arenaOptions.Count == 0)
                _arenaOptions.Add(new ArenaOption { Label = "(none — spawn where you stand)", ProtoRef = null });
        }

        ArenaCombo.ItemsSource = _arenaOptions;
        ArenaCombo.SelectedIndex = 0;
    }

    private void ArenaCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _arenaRef = (ArenaCombo.SelectedItem as ArenaOption)?.ProtoRef;
    }

    // ---------------- Run control ----------------

    private async void StartEndless_Click(object sender, RoutedEventArgs e)
    {
        bool usePhantom = EndlessSourceCombo.SelectedIndex == 0;
        var entry = new Dictionary<string, object?>
        {
            ["count"] = (int)EndlessCountBox.Value,
        };

        if (usePhantom)
        {
            var chosen = PhantomHeroCombo.SelectedItem as HeroOption;
            entry["enemyPhantom"] = true;
            entry["heroRef"] = chosen?.ProtoRef;
            entry["level"] = (int)PhantomLevelBox.Value;
            entry["rank"] = (int)EndlessStartRankBox.Value;
        }
        else
        {
            if (EnemyCatalogList.SelectedItem is not WaveEnemyCard card)
            {
                RunStatusText.Text = "select an enemy in the catalog first, or switch source to Enemy Phantom";
                return;
            }
            entry["enemyPhantom"] = false;
            entry["agentRef"] = card.Entry.ProtoRef;
        }

        StartEndlessBtn.IsEnabled = false;
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostEndlessStartAsync(new
            {
                playerName = TargetPlayer,
                arenaRegionRef = _arenaRef,
                clearArena = _arenaRef != null && ClearArenaCheck.IsChecked == true,
                countScalePerWave = (float)(CountScaleBox.Value / 100.0),
                levelBumpPerWave = (int)LevelBumpBox.Value,
                rewardLootTableRef = RewardLootTableRefText_,
                entry,
            });
            RunStatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
        }
        catch (Exception ex)
        {
            RunStatusText.Text = $"error: {ex.Message}";
        }
        finally
        {
            StartEndlessBtn.IsEnabled = true;
        }
    }

    private async void ExtractEndless_Click(object sender, RoutedEventArgs e)
    {
        ExtractEndlessBtn.IsEnabled = false;
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostEndlessExtractAsync(TargetPlayer);
            RunStatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
        }
        catch (Exception ex)
        {
            RunStatusText.Text = $"error: {ex.Message}";
        }
        finally
        {
            ExtractEndlessBtn.IsEnabled = true;
        }
    }

    // ---------------- Live status ----------------

    private async Task PollStatusAsync()
    {
        if (_pollInFlight) return;
        _pollInFlight = true;
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.GetEndlessStatusAsync(TargetPlayer);
            var s = resp?.Status;
            if (resp == null || resp.Ok == false || s == null || s.Active == false)
            {
                LiveStateText.Text = "not running";
                _lastPolledState = null;
                return;
            }

            string stateText = s.State switch
            {
                "Fighting" => s.Paused ? "PAUSED (fighting)" : "FIGHT!",
                "Intermission" => s.Paused ? "PAUSED (intermission)" : $"next wave in {Math.Ceiling(s.IntermissionRemainingMs / 1000.0):0}s",
                "WarpingToArena" => "warping to arena…",
                "SettlingArena" => "sterilizing arena…",
                _ => s.State,
            };
            LiveStateText.Text = stateText;
            LiveWavesText.Text = s.WavesSurvived.ToString();
            LiveRankText.Text = s.PeakRank.ToString();
            LiveAliveText.Text = s.Alive.ToString();
            LiveKillsText.Text = s.Kills.ToString();
            LiveTimerText.Text = $"run time {s.RunSeconds / 60}m{s.RunSeconds % 60:00}s";

            _lastPolledState = s.State;
        }
        catch
        {
            if (_lastPolledState == null) LiveStateText.Text = "server unreachable";
        }
        finally { _pollInFlight = false; }
    }
}
