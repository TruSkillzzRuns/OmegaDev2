using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using OmegaDev2.Services;

namespace OmegaDev2.Pages;

// Phantom Heroes control panel — the full !phantom command surface over the
// fork's /webapi/phantoms/* endpoints: spawn (random or specific hero with
// level/lock/costume), live roster with per-phantom costume/gear actions,
// and saved squads. All hero / costume identity comes from the server's
// loaded game data at runtime.

public sealed class PhantomHeroCard : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public PhantomHeroEntry Entry { get; }
    public string Name => string.IsNullOrEmpty(Entry.DisplayName) ? Entry.Name : Entry.DisplayName!;
    public string SubName => string.IsNullOrEmpty(Entry.DisplayName) ? "" : Entry.Name;

    // Roster badge — small "TEAM-UP" chip in the card. Visible only for
    // Kind=teamup entries so the user can tell them apart from playable
    // avatars at a glance.
    public bool IsTeamUp => Entry.IsTeamUp;
    public Microsoft.UI.Xaml.Visibility TeamUpBadgeVisibility
        => IsTeamUp ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    private BitmapImage? _portrait;
    public BitmapImage? Portrait { get => _portrait; set { _portrait = value; Raise(); } }
    public bool PortraitRequested;

    public PhantomHeroCard(PhantomHeroEntry entry) => Entry = entry;
}

public sealed class ActivePhantomRow
{
    public PhantomInfoEntry Entry { get; }
    public string Username => Entry.Username ?? "";
    public string Title => $"{Entry.HeroName}  ({Entry.Username})";
    public string Detail => Entry.LockLevel
        ? $"level {Entry.Level} (locked){(Entry.InWorld ? "" : " · not in world")}"
        : $"level {Entry.Level} (auto){(Entry.InWorld ? "" : " · not in world")}";
    public ActivePhantomRow(PhantomInfoEntry entry) => Entry = entry;
}

public sealed class SquadRow
{
    public PhantomSquadEntry Entry { get; }
    public string Name => Entry.Name;
    public string Detail => $"{Entry.Heroes.Count} phantom(s)";
    public string HeroesTip => string.Join(", ", Entry.Heroes);
    public SquadRow(PhantomSquadEntry entry) => Entry = entry;
}

public sealed partial class PhantomsPage : Page
{
    private readonly ServerApiClient _api = new();
    private readonly List<PhantomHeroCard> _allHeroes = new();
    public ObservableCollection<PhantomHeroCard> ShownHeroes { get; } = new();
    public ObservableCollection<ActivePhantomRow> ActivePhantoms { get; } = new();
    public ObservableCollection<SquadRow> Squads { get; } = new();

    private PhantomHeroCard? _selectedHero;
    private List<PhantomCostumeEntry> _costumes = new();
    private bool _portraitSweepRunning;
    private CancellationTokenSource? _pageCts = new();

    public PhantomsPage()
    {
        InitializeComponent();
        HeroList.ItemsSource = ShownHeroes;
        ActiveList.ItemsSource = ActivePhantoms;
        SquadList.ItemsSource = Squads;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _pageCts = new();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
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
            await RefreshActiveAsync();
            await RefreshSquadsAsync();
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
                        // Hero banners are cooked inline in the UI .upk, not
                        // TFC-streamed, so use /webapi/texbyname (inline-mip
                        // extraction with TFC fallback). Byte-fetch warms the
                        // server cache, then the Image decodes straight from
                        // the URI (same pattern as Gear Picker).
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

    // ---------------- Hero selection + costumes ----------------

    private async void HeroList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedHero = HeroList.SelectedItem as PhantomHeroCard;
        if (_selectedHero == null)
        {
            SelectedHeroText.Text = "(random heroes)";
            CostumeCombo.IsEnabled = false;
            CostumeCombo.ItemsSource = null;
            CountBox.IsEnabled = true;
            return;
        }

        SelectedHeroText.Text = _selectedHero.Name;
        CountBox.IsEnabled = false; // specific hero = one spawn per click
        CostumeCombo.IsEnabled = false;
        CostumeCombo.ItemsSource = new List<string> { "loading…" };
        CostumeCombo.SelectedIndex = 0;

        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.GetPhantomCostumesAsync(_selectedHero.Entry.ProtoRef);
            _costumes = resp?.Costumes ?? new List<PhantomCostumeEntry>();
        }
        catch
        {
            _costumes = new List<PhantomCostumeEntry>();
        }

        var labels = new List<string> { "(random costume)" };
        foreach (var c in _costumes)
            labels.Add(string.IsNullOrEmpty(c.DisplayName) ? c.Name : c.DisplayName!);
        CostumeCombo.ItemsSource = labels;
        CostumeCombo.SelectedIndex = 0;
        CostumeCombo.IsEnabled = true;
    }

    private void ClearHeroSelection_Click(object sender, RoutedEventArgs e)
    {
        HeroList.SelectedItem = null;
    }

    // ---------------- Spawn ----------------

    private async void Spawn_Click(object sender, RoutedEventArgs e)
    {
        SpawnBtn.IsEnabled = false;
        SpawnStatusText.Text = "spawning…";
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            int level = (int)LevelBox.Value;
            bool lockLevel = LockLevelCheck.IsChecked == true;
            bool bypassCap = BypassCapCheck.IsChecked == true;

            PhantomSpawnResponse? resp;
            if (_selectedHero != null)
            {
                string? costumeRef = null;
                int ci = CostumeCombo.SelectedIndex;
                if (ci > 0 && ci - 1 < _costumes.Count)
                    costumeRef = _costumes[ci - 1].ProtoRef;

                resp = await _api.PostPhantomSpawnAsync(new
                {
                    playerName = TargetPlayer,
                    bypassCap,
                    heroes = new[]
                    {
                        new { avatarRef = _selectedHero.Entry.ProtoRef, level, lockLevel, costumeRef },
                    },
                });
            }
            else
            {
                resp = await _api.PostPhantomSpawnAsync(new
                {
                    playerName = TargetPlayer,
                    count = (int)CountBox.Value,
                    level,
                    lockLevel,
                    bypassCap,
                });
            }

            if (resp == null)
                SpawnStatusText.Text = "no response from server";
            else if (string.IsNullOrEmpty(resp.Error) == false)
                SpawnStatusText.Text = resp.Error;
            else
                SpawnStatusText.Text = resp.Failed > 0
                    ? $"spawned {resp.Spawned}, failed {resp.Failed}: {resp.FirstError}"
                    : $"spawned {resp.Spawned}";

            await RefreshActiveAsync();
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

    // ---------------- Active phantoms ----------------

    private async void RefreshActive_Click(object sender, RoutedEventArgs e) => await RefreshActiveAsync();

    private async Task RefreshActiveAsync()
    {
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.GetPhantomStatusAsync(TargetPlayer);
            ActivePhantoms.Clear();
            if (resp == null || resp.Ok == false)
            {
                StatusText.Text = resp?.Error ?? "status failed";
                return;
            }
            foreach (var p in resp.Phantoms)
                ActivePhantoms.Add(new ActivePhantomRow(p));
            // Cap is null when the player's current region doesn't gate
            // party size at all (Town/PublicCombatZone/MatchPlay) — the
            // game's real story/raid caps otherwise apply here too.
            string capSuffix = resp.Cap.HasValue ? $"/{resp.Cap.Value}" : "";
            StatusText.Text = $"{resp.Count}{capSuffix} active for {resp.Player}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"error: {ex.Message}";
        }
    }

    private async void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostPhantomClearAsync(TargetPlayer);
            StatusText.Text = resp != null ? $"cleared {resp.Removed}" : "no response";
            await RefreshActiveAsync();
        }
        catch (Exception ex) { StatusText.Text = $"error: {ex.Message}"; }
    }

    private async void RerollAllGear_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostPhantomGearAsync(TargetPlayer, null);
            StatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
        }
        catch (Exception ex) { StatusText.Text = $"error: {ex.Message}"; }
    }

    private async void RandomizeAllCostumes_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostPhantomCostumeAsync(new { playerName = TargetPlayer, randomizeAll = true });
            StatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
        }
        catch (Exception ex) { StatusText.Text = $"error: {ex.Message}"; }
    }

    private async void RowRandomCostume_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string username || username.Length == 0) return;
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostPhantomCostumeAsync(new { playerName = TargetPlayer, phantomQuery = username, costume = "random" });
            StatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
        }
        catch (Exception ex) { StatusText.Text = $"error: {ex.Message}"; }
    }

    private async void RowRerollGear_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string username || username.Length == 0) return;
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostPhantomGearAsync(TargetPlayer, username);
            StatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
        }
        catch (Exception ex) { StatusText.Text = $"error: {ex.Message}"; }
    }

    // ---------------- Squads ----------------

    private async void RefreshSquads_Click(object sender, RoutedEventArgs e) => await RefreshSquadsAsync();

    private async Task RefreshSquadsAsync()
    {
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.GetPhantomSquadsAsync(TargetPlayer);
            Squads.Clear();
            if (resp == null || resp.Ok == false)
            {
                SquadStatusText.Text = resp?.Error ?? "squad list failed";
                return;
            }
            foreach (var s in resp.Squads)
                Squads.Add(new SquadRow(s));
            SquadStatusText.Text = $"{resp.Squads.Count} squad(s)";
        }
        catch (Exception ex)
        {
            SquadStatusText.Text = $"error: {ex.Message}";
        }
    }

    private async void SaveSquad_Click(object sender, RoutedEventArgs e)
    {
        string name = SquadNameBox.Text?.Trim() ?? "";
        if (name.Length == 0) { SquadStatusText.Text = "enter a squad name first"; return; }
        await SquadOpAsync("save", name);
    }

    private async void SpawnSquad_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string name) return;
        await SquadOpAsync("spawn", name);
        await RefreshActiveAsync();
    }

    private async void DeleteSquad_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string name) return;
        await SquadOpAsync("delete", name);
    }

    private async Task SquadOpAsync(string op, string name)
    {
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostPhantomSquadOpAsync(TargetPlayer, op, name);
            string message = resp?.Message ?? resp?.Error ?? "no response";
            await RefreshSquadsAsync();
            SquadStatusText.Text = message;
        }
        catch (Exception ex)
        {
            SquadStatusText.Text = $"error: {ex.Message}";
        }
    }
}
