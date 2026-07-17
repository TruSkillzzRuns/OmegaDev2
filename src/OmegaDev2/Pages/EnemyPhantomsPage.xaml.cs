using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using OmegaDev2.Services;
using Windows.UI;

namespace OmegaDev2.Pages;

public sealed class NemesisRow
{
    public string HeroRef { get; }
    public string Title { get; }
    public string Detail { get; }
    public string RankBadge { get; }

    public NemesisRow(NemesisEntryDto e)
    {
        HeroRef = e.HeroRef ?? string.Empty;
        string heroRefSafe = HeroRef;
        string niceHero = string.IsNullOrEmpty(e.HeroName)
            ? heroRefSafe
            : e.HeroName.Split('/').Last();
        string suffix = string.IsNullOrEmpty(e.Suffix) ? "" : " " + e.Suffix;
        int safeRank = System.Math.Clamp(e.Rank, 0, 5);
        string stars = safeRank > 0 ? new string('★', safeRank) : "";

        string baseTitle = string.IsNullOrEmpty(e.LastKillerName) ? niceHero : e.LastKillerName;
        Title = e.Defeated
            ? $"{baseTitle}{suffix}  (DEFEATED)"
            : $"{stars} {baseTitle}{suffix}".Trim();

        string status = e.Defeated ? "DEFEATED" : "ACTIVE";
        string revenge = e.RevengeKills > 0 ? $"  ·  your revenge {e.RevengeKills}" : "";
        Detail = $"{niceHero}  ·  {status}  ·  their kills {e.Kills}{revenge}";
        RankBadge = $"RANK {safeRank}";
    }
}

public sealed class EnemyRow
{
    private static readonly Brush s_alive = new SolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0x5C, 0x5C));
    private static readonly Brush s_dead = new SolidColorBrush(Color.FromArgb(0xFF, 0x55, 0x55, 0x60));

    public string Title { get; }
    public string HealthText { get; }
    public double BarWidth { get; }
    public Brush BarBrush { get; }
    public string AvatarIdText { get; }

    public EnemyRow(EnemyPhantomEntry e)
    {
        Title = $"{e.HeroName}  ·  level {e.Level}";
        HealthText = e.Dead ? "DOWN" : $"{e.HealthPct}%";
        BarWidth = e.Dead ? 0 : Math.Max(2.0, 3.6 * e.HealthPct);
        BarBrush = e.Dead ? s_dead : s_alive;
        AvatarIdText = e.AvatarId.ToString();
    }
}

// Enemy Phantoms — hostile AI heroes. Roster on the left, live hostiles
// with health bars in the middle (1s poll), spawn controls on the right.
public sealed partial class EnemyPhantomsPage : Page
{
    private readonly ServerApiClient _api = new();
    private readonly List<PhantomHeroCard> _allHeroes = new();
    public ObservableCollection<PhantomHeroCard> ShownHeroes { get; } = new();
    public ObservableCollection<EnemyRow> Enemies { get; } = new();
    public ObservableCollection<NemesisRow> NemesisEntries { get; } = new();

    private PhantomHeroCard? _selectedHero;
    private bool _portraitSweepRunning;
    private bool _pollInFlight;
    private bool _rogueSuppressToggleEvent;
    private CancellationTokenSource? _pageCts = new();
    private readonly DispatcherQueueTimer _timer;

    public EnemyPhantomsPage()
    {
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += async (_, _) => await PollEnemiesAsync();

        InitializeComponent();
        HeroList.ItemsSource = ShownHeroes;
        EnemyList.ItemsSource = Enemies;
        NemesisList.ItemsSource = NemesisEntries;

        _timer.Start();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _pageCts = new();
        _timer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _timer.Stop();
        _pageCts?.Cancel();
        base.OnNavigatedFrom(e);
    }

    private string TargetPlayer => string.IsNullOrWhiteSpace(PlayerBox.Text) ? "*" : PlayerBox.Text.Trim();

    // ---------------- Roster ----------------

    private async void LoadRoster_Click(object sender, RoutedEventArgs e)
    {
        LoadBtn.IsEnabled = false;
        StatusText.Text = "loading roster…";
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.GetPhantomHeroesAsync();
            if (resp == null || resp.Heroes.Count == 0)
            {
                StatusText.Text = "no heroes — server offline?";
                return;
            }

            _allHeroes.Clear();
            foreach (var hero in resp.Heroes)
                _allHeroes.Add(new PhantomHeroCard(hero));
            ApplyHeroFilter();
            StatusText.Text = $"{resp.TotalHeroes} heroes";

            _ = RunPortraitSweepAsync();
            _ = RefreshNemesisAsync();
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

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyHeroFilter();

    private void ApplyHeroFilter()
    {
        string q = SearchBox.Text?.Trim() ?? "";
        ShownHeroes.Clear();
        foreach (var card in _allHeroes)
        {
            if (q.Length > 0 &&
                card.Name.Contains(q, StringComparison.OrdinalIgnoreCase) == false &&
                card.Entry.Name.Contains(q, StringComparison.OrdinalIgnoreCase) == false)
                continue;
            ShownHeroes.Add(card);
        }
    }

    private async Task RunPortraitSweepAsync()
    {
        if (_portraitSweepRunning) return;
        _portraitSweepRunning = true;
        var ct = _pageCts?.Token ?? CancellationToken.None;
        try
        {
            string portraitBase = AppState.ServerUrl.TrimEnd('/');
            using var throttle = new SemaphoreSlim(8);
            var tasks = new List<Task>();
            foreach (var card in _allHeroes)
            {
                var candidates = card.Entry.PortraitCandidates is { Count: > 0 }
                    ? card.Entry.PortraitCandidates
                    : (string.IsNullOrEmpty(card.Entry.PortraitPath) ? null : new List<string> { card.Entry.PortraitPath! });
                if (card.PortraitRequested || candidates == null) continue;
                card.PortraitRequested = true;
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
                                try { card.Portrait = new BitmapImage(new Uri(url)) { DecodePixelWidth = 96 }; }
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

    private void HeroList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedHero = HeroList.SelectedItem as PhantomHeroCard;
        SelectedHeroText.Text = _selectedHero?.Name ?? "(random heroes)";
        // A specific hero (or a ranked nemesis, which is always exactly one
        // spawn) means Count doesn't apply — same convention Phantom Heroes'
        // quick-spawn already uses.
        CountBox.IsEnabled = _selectedHero == null && RankCombo.SelectedIndex == 0;
    }

    private void ClearHeroSelection_Click(object sender, RoutedEventArgs e) => HeroList.SelectedItem = null;

    private void RankCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CountBox == null) return; // parse-time firing
        CountBox.IsEnabled = _selectedHero == null && RankCombo.SelectedIndex == 0;
    }

    // ---------------- Spawn / clear ----------------

    private async void Spawn_Click(object sender, RoutedEventArgs e)
    {
        SpawnBtn.IsEnabled = false;
        SpawnStatusText.Text = "spawning hostiles…";
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            int rank = RankCombo.SelectedIndex; // 0 = off, 1-5 = nemesis rank
            var resp = await _api.PostEnemyPhantomSpawnAsync(new
            {
                playerName = TargetPlayer,
                heroes = new[]
                {
                    new
                    {
                        avatarRef = _selectedHero?.Entry.ProtoRef,
                        level = (int)LevelBox.Value,
                        count = (int)CountBox.Value,
                        rank,
                    },
                },
            });

            if (resp == null)
                SpawnStatusText.Text = "no response from server";
            else if (string.IsNullOrEmpty(resp.Error) == false)
                SpawnStatusText.Text = resp.Error;
            else
                SpawnStatusText.Text = resp.Failed > 0
                    ? $"spawned {resp.Spawned}, failed {resp.Failed}: {resp.FirstError}"
                    : $"{resp.Spawned} hostile(s) inbound — good luck";

            await PollEnemiesAsync();
        }
        catch (Exception ex)
        {
            SpawnStatusText.Text = $"error: {ex.Message}";
        }
        finally
        {
            SpawnBtn.IsEnabled = true;
        }
    }

    private async void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostEnemyPhantomClearAsync(TargetPlayer);
            StatusText.Text = resp != null ? $"cleared {resp.Removed}" : "no response";
            await PollEnemiesAsync();
        }
        catch (Exception ex) { StatusText.Text = $"error: {ex.Message}"; }
    }

    private async void RemoveOne_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string idText || ulong.TryParse(idText, out ulong id) == false || id == 0) return;
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostEnemyPhantomDespawnOneAsync(TargetPlayer, id);
            StatusText.Text = resp == null ? "no response" : (resp.Ok ? "removed" : (resp.Error ?? "not found"));
            await PollEnemiesAsync();
        }
        catch (Exception ex) { StatusText.Text = $"error: {ex.Message}"; }
    }

    // ---------------- Live hostiles ----------------

    private async Task PollEnemiesAsync()
    {
        if (_pollInFlight) return;
        _pollInFlight = true;
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.GetEnemyPhantomStatusAsync(TargetPlayer);
            Enemies.Clear();
            if (resp != null && resp.Ok)
                foreach (var enemy in resp.Enemies.OrderByDescending(x => x.HealthPct))
                    Enemies.Add(new EnemyRow(enemy));

            // Piggyback the 1s poll for a compact "your own DPS" readout too
            // — friendly-phantom vs. enemy-phantom damage can't be split out
            // server-side yet, but the human player is always the one
            // non-phantom combatant, so that entry is reliably "you."
            try
            {
                var dps = await _api.GetDpsAsync(TargetPlayer);
                var you = dps?.Combatants?.FirstOrDefault(c => c.IsPhantom == false);
                YourDpsText.Text = you != null && you.DpsOverall > 0
                    ? $"your DPS: {you.DpsOverall:N0}"
                    : "";
            }
            catch { /* non-critical, skip this tick */ }

            // Piggyback the 1s poll to keep the Rogue Encounter status
            // fresh — cooldown countdown displays live as it ticks down.
            var rogue = await _api.GetRogueEncounterStatusAsync(TargetPlayer);
            if (rogue != null && rogue.Ok)
            {
                _rogueSuppressToggleEvent = true;
                RogueSwitch.IsOn = rogue.Enabled;
                _rogueSuppressToggleEvent = false;

                if (rogue.Enabled == false)
                    RogueStatusText.Text = "";
                else if (rogue.CooldownRemainingMs > 0)
                    RogueStatusText.Text = $"cooldown {Math.Ceiling(rogue.CooldownRemainingMs / 1000.0):0}s";
                else
                    RogueStatusText.Text = "ready — anything could happen";
            }
        }
        catch { /* poll again next second */ }
        finally { _pollInFlight = false; }
    }

    private async void RogueSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_rogueSuppressToggleEvent) return;
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            await _api.PostRogueEncounterAsync(TargetPlayer, RogueSwitch.IsOn, triggerNow: false);
        }
        catch (Exception ex) { StatusText.Text = $"error: {ex.Message}"; }
    }

    private async void RogueTriggerNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostRogueEncounterAsync(TargetPlayer, enabled: null, triggerNow: true);
            StatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
            await PollEnemiesAsync();
        }
        catch (Exception ex) { StatusText.Text = $"error: {ex.Message}"; }
    }

    // ---------------- Nemesis ----------------

    private async void NemesisRefresh_Click(object sender, RoutedEventArgs e) => await RefreshNemesisAsync();

    private async Task RefreshNemesisAsync()
    {
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.GetNemesisListAsync(TargetPlayer);
            NemesisEntries.Clear();
            if (resp == null || resp.Ok == false)
            {
                NemesisStatusText.Text = resp?.Error ?? "nemesis list failed";
                return;
            }
            foreach (var n in resp.Nemeses) NemesisEntries.Add(new NemesisRow(n));
            int active = 0, defeated = 0;
            foreach (var r in resp.Nemeses) { if (r.Defeated) defeated++; else active++; }
            NemesisStatusText.Text = NemesisEntries.Count == 0
                ? "no nemeses yet — die to an enemy phantom to earn a spot in your history"
                : $"{active} active, {defeated} defeated  ·  {NemesisEntries.Count} total";
        }
        catch (Exception ex) { NemesisStatusText.Text = $"error: {ex.Message}"; }
    }

    private async void NemesisFightNow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string heroRef) return;
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostNemesisSpawnNowAsync(TargetPlayer, heroRef);
            StatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
            await PollEnemiesAsync();
        }
        catch (Exception ex) { StatusText.Text = $"error: {ex.Message}"; }
    }

    private async void NemesisBanish_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string heroRef) return;
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostNemesisBanishAsync(TargetPlayer, heroRef);
            NemesisStatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
            await RefreshNemesisAsync();
        }
        catch (Exception ex) { NemesisStatusText.Text = $"error: {ex.Message}"; }
    }
}
