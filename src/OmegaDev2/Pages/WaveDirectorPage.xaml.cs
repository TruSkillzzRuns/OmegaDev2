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
    public Visibility HeaderOnlyVisible => IsHeader ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class WavePlanNameRow
{
    public string Name { get; set; } = "";
}

public sealed class WaveHistoryRow
{
    public string Summary { get; set; } = "";
    public string Detail { get; set; } = "";
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
    public ObservableCollection<WavePlanNameRow> SavedPlans { get; } = new();
    public ObservableCollection<WaveHistoryRow> History { get; } = new();

    private readonly List<List<PlanEntry>> _plan = new() { new List<PlanEntry>() };
    // Kept in lockstep with _plan (same index) — null = use the run's global intermission.
    private readonly List<int?> _waveIntermissionOverrides = new() { null };

    private readonly DispatcherQueueTimer _timer;
    private bool _pollInFlight;
    // Edge-trigger for the completion ping — only fire once per transition into "Done".
    private string? _lastPolledState;

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
        SavedPlansList.ItemsSource = SavedPlans;
        HistoryList.ItemsSource = History;
        RefreshPlanRows();

        _ = PopulateArenaComboAsync();
        _ = PopulateHeroComboAsync();
        _ = RefreshPlansAsync();
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
            int? overrideMs = w < _waveIntermissionOverrides.Count ? _waveIntermissionOverrides[w] : null;
            string overrideSuffix = overrideMs.HasValue ? $" · intermission {overrideMs.Value / 1000.0:0.#}s" : "";
            PlanRows.Add(new WavePlanRow
            {
                Title = $"WAVE {w + 1}   ({totalInWave} enemies){overrideSuffix}",
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
        _waveIntermissionOverrides.Add(null);
        RefreshPlanRows();
    }

    private void DuplicateWave_Click(object sender, RoutedEventArgs e)
    {
        if (_plan.Count == 0 || _plan[^1].Count == 0) return;
        var copy = _plan[^1].Select(x => new PlanEntry
        {
            AgentRef = x.AgentRef,
            AgentName = x.AgentName,
            EnemyPhantom = x.EnemyPhantom,
            HeroRef = x.HeroRef,
            HeroName = x.HeroName,
            Count = x.Count,
            Level = x.Level,
        }).ToList();
        _plan.Add(copy);
        _waveIntermissionOverrides.Add(_waveIntermissionOverrides[^1]);
        RefreshPlanRows();
    }

    private void ClearPlan_Click(object sender, RoutedEventArgs e)
    {
        _plan.Clear();
        _plan.Add(new List<PlanEntry>());
        _waveIntermissionOverrides.Clear();
        _waveIntermissionOverrides.Add(null);
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
            if (w < _waveIntermissionOverrides.Count) _waveIntermissionOverrides.RemoveAt(w);
            if (_plan.Count == 0)
            {
                _plan.Add(new List<PlanEntry>());
                _waveIntermissionOverrides.Add(null);
            }
        }
        else
        {
            int i = int.Parse(parts[1]);
            if (i < _plan[w].Count) _plan[w].RemoveAt(i);
        }
        RefreshPlanRows();
    }

    private void MoveWaveUp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string key) return;
        int w = int.Parse(key);
        if (w <= 0 || w >= _plan.Count) return;
        (_plan[w - 1], _plan[w]) = (_plan[w], _plan[w - 1]);
        (_waveIntermissionOverrides[w - 1], _waveIntermissionOverrides[w]) = (_waveIntermissionOverrides[w], _waveIntermissionOverrides[w - 1]);
        RefreshPlanRows();
    }

    private void MoveWaveDown_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string key) return;
        int w = int.Parse(key);
        if (w < 0 || w >= _plan.Count - 1) return;
        (_plan[w + 1], _plan[w]) = (_plan[w], _plan[w + 1]);
        (_waveIntermissionOverrides[w + 1], _waveIntermissionOverrides[w]) = (_waveIntermissionOverrides[w], _waveIntermissionOverrides[w + 1]);
        RefreshPlanRows();
    }

    private void SetWaveIntermission_Click(object sender, RoutedEventArgs e)
    {
        if (_waveIntermissionOverrides.Count == 0) return;
        int value = (int)(WaveIntermissionOverrideBox.Value * 1000);
        _waveIntermissionOverrides[^1] = value > 0 ? value : null;
        RefreshPlanRows();
    }

    // ---------------- Arena picker ----------------

    private void ArenaCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _arenaRef = (ArenaCombo.SelectedItem as ArenaOption)?.ProtoRef;
    }

    // ---------------- Run control ----------------

    private object[] BuildWavePayload()
    {
        var result = new List<object>();
        for (int idx = 0; idx < _plan.Count; idx++)
        {
            if (_plan[idx].Count == 0) continue;
            result.Add(new
            {
                intermissionMsOverride = idx < _waveIntermissionOverrides.Count ? _waveIntermissionOverrides[idx] : null,
                entries = _plan[idx].Select(x => new
                {
                    agentRef = x.AgentRef,
                    heroRef = x.HeroRef,
                    enemyPhantom = x.EnemyPhantom,
                    count = x.Count,
                    level = x.Level,
                }).ToArray(),
            });
        }
        return result.ToArray();
    }

    private string RewardModeTag => (RewardModeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "None";
    private string? RewardLootTableRefText => string.IsNullOrWhiteSpace(RewardLootTableBox.Text) ? null : RewardLootTableBox.Text.Trim();

    // Full run recipe, shared by Start (playerName added separately) and
    // Save Plan (this object IS the plan body).
    private object BuildRunSettings() => new
    {
        intermissionMs = (int)(IntermissionBox.Value * 1000),
        arenaRegionRef = _arenaRef,
        clearArena = _arenaRef != null && ClearArenaCheck.IsChecked == true,
        loop = LoopCheck.IsChecked == true,
        countScalePerWave = (float)(CountScaleBox.Value / 100.0),
        levelBumpPerWave = (int)LevelBumpBox.Value,
        rewardMode = RewardModeTag,
        rewardLootTableRef = RewardLootTableRefText,
        waves = BuildWavePayload(),
    };

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var waves = BuildWavePayload();
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
                loop = LoopCheck.IsChecked == true,
                countScalePerWave = (float)(CountScaleBox.Value / 100.0),
                levelBumpPerWave = (int)LevelBumpBox.Value,
                rewardMode = RewardModeTag,
                rewardLootTableRef = RewardLootTableRefText,
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

    private bool _paused;

    private async void PauseResume_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            _paused = !_paused;
            var resp = await _api.PostWavesPauseAsync(TargetPlayer, _paused);
            RunStatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
            PauseResumeBtn.Content = _paused ? "Resume" : "Pause";
        }
        catch (Exception ex)
        {
            RunStatusText.Text = $"error: {ex.Message}";
        }
    }

    private async void Skip_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostWavesSkipAsync(TargetPlayer);
            RunStatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
        }
        catch (Exception ex)
        {
            RunStatusText.Text = $"error: {ex.Message}";
        }
    }

    // ---------------- Saved plans ----------------

    private async Task RefreshPlansAsync()
    {
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.GetWavePlansAsync(TargetPlayer);
            SavedPlans.Clear();
            if (resp == null || resp.Ok == false)
            {
                PlanStatusText.Text = resp?.Error ?? "plan list failed";
                return;
            }
            foreach (string name in resp.Plans ?? new List<string>())
                SavedPlans.Add(new WavePlanNameRow { Name = name });
        }
        catch (Exception ex)
        {
            PlanStatusText.Text = $"error: {ex.Message}";
        }
    }

    private async void SavePlan_Click(object sender, RoutedEventArgs e)
    {
        string name = PlanNameBox.Text?.Trim() ?? "";
        if (name.Length == 0) { PlanStatusText.Text = "enter a plan name first"; return; }

        var waves = BuildWavePayload();
        if (waves.Length == 0) { PlanStatusText.Text = "the plan is empty — add enemies first"; return; }

        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var settings = BuildRunSettings();
            var resp = await _api.PostWavePlanSaveAsync(TargetPlayer, name, settings);
            PlanStatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
            await RefreshPlansAsync();
        }
        catch (Exception ex)
        {
            PlanStatusText.Text = $"error: {ex.Message}";
        }
    }

    private async void StartPlan_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string name) return;
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostWavePlanOpAsync(TargetPlayer, "start", name);
            PlanStatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
        }
        catch (Exception ex)
        {
            PlanStatusText.Text = $"error: {ex.Message}";
        }
    }

    private async void DeletePlan_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string name) return;
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostWavePlanOpAsync(TargetPlayer, "delete", name);
            PlanStatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
            await RefreshPlansAsync();
        }
        catch (Exception ex)
        {
            PlanStatusText.Text = $"error: {ex.Message}";
        }
    }

    private async void RefreshPlans_Click(object sender, RoutedEventArgs e) => await RefreshPlansAsync();

    // ---------------- Sharing codes ----------------

    private async void ExportWaveCode_Click(object sender, RoutedEventArgs e)
    {
        var waves = _plan.Where(w => w.Count > 0).ToList();
        if (waves.Count == 0)
        {
            RunStatusText.Text = "the plan is empty — add enemies first";
            return;
        }

        var payload = new WaveCode.Payload
        {
            Name = (PlanNameBox.Text?.Trim() is { Length: > 0 } n) ? n : null,
            IntermissionMs = (int)(IntermissionBox.Value * 1000),
            ArenaRegionRef = _arenaRef,
            ClearArena = _arenaRef != null && ClearArenaCheck.IsChecked == true,
            Loop = LoopCheck.IsChecked == true,
            CountScalePerWave = (float)(CountScaleBox.Value / 100.0),
            LevelBumpPerWave = (int)LevelBumpBox.Value,
            RewardMode = RewardModeTag,
            RewardLootTableRef = RewardLootTableRefText,
        };
        for (int idx = 0; idx < _plan.Count; idx++)
        {
            if (_plan[idx].Count == 0) continue;
            var wave = new WaveCode.Wave { IntermissionMsOverride = idx < _waveIntermissionOverrides.Count ? _waveIntermissionOverrides[idx] : null };
            foreach (var x in _plan[idx])
            {
                wave.Entries.Add(new WaveCode.Entry
                {
                    AgentRef = x.AgentRef,
                    AgentName = x.AgentName,
                    EnemyPhantom = x.EnemyPhantom,
                    HeroRef = x.HeroRef,
                    HeroName = x.HeroName,
                    Count = x.Count,
                    Level = x.Level,
                });
            }
            payload.Waves.Add(wave);
        }

        string code = WaveCode.Encode(payload);

        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(code);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);

        var text = new TextBox
        {
            Text = code,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            MinHeight = 90,
        };
        var dlg = new ContentDialog
        {
            Title = $"Wave Code ({payload.Waves.Count} wave" + (payload.Waves.Count == 1 ? "" : "s") + ")",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Copied to clipboard. Share it anywhere — Discord, notes, wherever. Recipient pastes it into Import Code.", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 },
                    text,
                },
            },
            PrimaryButtonText = "Copy Again",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        dlg.PrimaryButtonClick += (_, args) =>
        {
            var d = new Windows.ApplicationModel.DataTransfer.DataPackage();
            d.SetText(code);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(d);
            args.Cancel = true;
        };
        await dlg.ShowAsync();
    }

    private async void ImportWaveCode_Click(object sender, RoutedEventArgs e)
    {
        var input = new TextBox
        {
            PlaceholderText = "paste wave code here…",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            MinHeight = 90,
        };
        var dlg = new ContentDialog
        {
            Title = "Import Wave Code",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Only proto-ref IDs and run settings are transferred — no game files. This replaces your current plan. Any enemy/hero not on your install is skipped.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 },
                    input,
                },
            },
            PrimaryButtonText = "Import",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        var result = await dlg.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var payload = WaveCode.TryDecode(input.Text ?? string.Empty, out string error);
        if (payload is null)
        {
            RunStatusText.Text = $"import failed: {error}";
            return;
        }

        _plan.Clear();
        _waveIntermissionOverrides.Clear();
        foreach (var wave in payload.Waves)
        {
            var entries = wave.Entries.Select(x => new PlanEntry
            {
                AgentRef = x.AgentRef,
                AgentName = x.AgentName,
                EnemyPhantom = x.EnemyPhantom,
                HeroRef = x.HeroRef,
                HeroName = x.HeroName,
                Count = x.Count,
                Level = x.Level,
            }).ToList();
            _plan.Add(entries);
            _waveIntermissionOverrides.Add(wave.IntermissionMsOverride);
        }
        if (_plan.Count == 0)
        {
            _plan.Add(new List<PlanEntry>());
            _waveIntermissionOverrides.Add(null);
        }

        if (!string.IsNullOrEmpty(payload.Name)) PlanNameBox.Text = payload.Name;
        IntermissionBox.Value = payload.IntermissionMs / 1000.0;
        ClearArenaCheck.IsChecked = payload.ClearArena;
        LoopCheck.IsChecked = payload.Loop;
        CountScaleBox.Value = payload.CountScalePerWave * 100.0;
        LevelBumpBox.Value = payload.LevelBumpPerWave;
        RewardLootTableBox.Text = payload.RewardLootTableRef ?? "";

        for (int i = 0; i < RewardModeCombo.Items.Count; i++)
        {
            if ((RewardModeCombo.Items[i] as ComboBoxItem)?.Tag as string == payload.RewardMode)
            {
                RewardModeCombo.SelectedIndex = i;
                break;
            }
        }

        bool arenaMatched = false;
        if (payload.ArenaRegionRef != null)
        {
            for (int i = 0; i < _arenaOptions.Count; i++)
            {
                if (string.Equals(_arenaOptions[i].ProtoRef, payload.ArenaRegionRef, StringComparison.OrdinalIgnoreCase))
                {
                    ArenaCombo.SelectedIndex = i;
                    arenaMatched = true;
                    break;
                }
            }
        }
        if (arenaMatched == false)
        {
            ArenaCombo.SelectedIndex = 0;
            _arenaRef = null;
        }

        RefreshPlanRows();
        RunStatusText.Text = arenaMatched || payload.ArenaRegionRef == null
            ? $"imported {payload.Waves.Count} wave(s) from code"
            : $"imported {payload.Waves.Count} wave(s) — arena not found on this server, defaulted to none";
    }

    // ---------------- History ----------------

    private async void RefreshHistory_Click(object sender, RoutedEventArgs e) => await RefreshHistoryAsync();

    private async Task RefreshHistoryAsync()
    {
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.GetWaveHistoryAsync(TargetPlayer);
            History.Clear();
            if (resp == null || resp.Ok == false) return;

            foreach (var h in resp.Entries.Take(20))
            {
                var started = DateTimeOffset.FromUnixTimeMilliseconds(h.StartedAtMs).LocalDateTime;
                long durationSec = Math.Max(0, (h.EndedAtMs - h.StartedAtMs) / 1000);
                History.Add(new WaveHistoryRow
                {
                    Summary = $"{(h.Completed ? "✓" : "✕")} wave {h.WavesCompleted}/{h.TotalWaves} — {h.Kills} kills",
                    Detail = $"{started:MM/dd HH:mm} · {durationSec / 60}m{durationSec % 60:00}s · {h.SpawnedTotal} spawned",
                });
            }
        }
        catch { /* leave list as-is */ }
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

            if (resp == null)
            {
                LiveStateText.Text = "server unreachable";
                return;
            }
            if (resp.Ok == false)
            {
                LiveStateText.Text = resp.Error ?? "server error";
                return;
            }
            var s = resp.Status;
            if (s == null)
            {
                LiveStateText.Text = "no status returned";
                return;
            }

            LiveWaveText.Text = s.TotalWaves > 0 ? $"wave {s.Wave} / {s.TotalWaves}" : "wave — / —";
            LiveStateText.Text = s.State switch
            {
                "WarpingToArena" => "warping to arena…",
                "SettlingArena" => "sterilizing arena…",
                "Fighting" => s.Paused ? "PAUSED (fighting)" : "FIGHT!",
                "Intermission" => s.Paused ? "PAUSED (intermission)" : $"next wave in {Math.Ceiling(s.IntermissionRemainingMs / 1000.0):0}s",
                "Done" => s.Loop ? "looping…" : "run complete — you survived",
                _ => "idle",
            };
            LiveAliveText.Text = s.Alive.ToString();
            LiveKillsText.Text = s.Kills.ToString();
            LiveTimerText.Text = s.Active ? $"run time {s.RunSeconds / 60}m{s.RunSeconds % 60:00}s · {s.SpawnedTotal} spawned{(s.Loop ? " · LOOP" : "")}" : "";

            // Edge-triggered completion ping — fires once per transition
            // into Done, not once per poll while it stays Done.
            if (s.State == "Done" && _lastPolledState != "Done" && NotifyOnCompleteCheck.IsChecked == true)
            {
                CompletionInfoBar.Message = $"{s.Wave}/{s.TotalWaves} waves cleared · {s.Kills} kills · {s.RunSeconds / 60}m{s.RunSeconds % 60:00}s";
                CompletionInfoBar.IsOpen = true;
                try { Console.Beep(880, 200); } catch { /* audio device unavailable */ }
                await RefreshHistoryAsync();
            }
            _lastPolledState = s.State;
        }
        catch { /* next poll */ }
        finally { _pollInFlight = false; }
    }
}
