using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using OmegaDev2.Services;
using Windows.UI;

namespace OmegaDev2.Pages;

// Gear Picker — browse the item catalog served by the 1.52 fork
// (/webapi/items/catalog), basket items with per-entry count + rarity
// override, and deliver via /webapi/items/give. All item and rarity names
// come from the server's loaded game data at runtime; nothing game-specific
// is hardcoded here.

public sealed class GearItemCard : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public ItemCatalogEntry Entry { get; }
    public string ProtoRef => Entry.ProtoRef;
    // Localized name front and center ("The..." names), data leaf as the
    // fallback for items with no display string.
    public string Name => string.IsNullOrEmpty(Entry.DisplayName) ? Entry.Name : Entry.DisplayName!;
    public string Category => Entry.Category;
    public string SubLabel => Entry.IsUnique
        ? $"Unique · {Entry.Avatar ?? Entry.Category}"
        : Entry.Avatar ?? Entry.Slot ?? Entry.Category;
    public string Path => Entry.Path;
    public Brush CategoryBrush { get; }

    private BitmapImage? _icon;
    public BitmapImage? Icon { get => _icon; set { _icon = value; Raise(); } }
    public bool IconRequested;

    public GearItemCard(ItemCatalogEntry entry)
    {
        Entry = entry;
        // Uniques get the classic orange accent regardless of base category.
        CategoryBrush = entry.IsUnique
            ? new SolidColorBrush(Color.FromArgb(0xFF, 0xE0, 0x8A, 0x2E))
            : GearPickerPage.CategoryBrush(entry.Category);
    }
}

public sealed class GearCategory
{
    public string Name { get; }
    public int Count { get; }
    public string CountText => Count.ToString("N0");
    public GearCategory(string name, int count) { Name = name; Count = count; }
}

public sealed class BasketEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    // Shared rarity choices, populated from the server catalog at load time.
    // Index 0 is always the "roll by level" default.
    public sealed record RarityChoice(string Label, string Ref);
    public static readonly List<RarityChoice> RarityChoices = new() { new("(auto — roll by level)", "") };
    public List<string> RarityLabels => RarityChoices.Select(r => r.Label).ToList();

    public ItemCatalogEntry Entry { get; }
    public string ProtoRef => Entry.ProtoRef;
    public string Name => string.IsNullOrEmpty(Entry.DisplayName) ? Entry.Name : Entry.DisplayName!;

    private double _count;
    public double Count { get => _count; set { if (_count != value) { _count = Math.Max(1, value); Raise(); } } }

    public double Level { get; set; }

    private int _rarityIndex;
    public int RarityIndex
    {
        get => _rarityIndex;
        set
        {
            if (_rarityIndex == value || value < 0) return;
            _rarityIndex = Math.Clamp(value, 0, RarityChoices.Count - 1);
            Raise();
        }
    }
    public string RarityRef => RarityChoices[Math.Clamp(_rarityIndex, 0, RarityChoices.Count - 1)].Ref;

    public BasketEntry(ItemCatalogEntry entry, int count, int level)
    {
        Entry = entry;
        _count = count;
        Level = level;
    }
}

public sealed partial class GearPickerPage : Page
{
    private readonly List<GearItemCard> _allItems = new();
    public ObservableCollection<GearCategory> Categories { get; } = new();
    public ObservableCollection<GearItemCard> ShownItems { get; } = new();
    public ObservableCollection<BasketEntry> Basket { get; } = new();

    private string _activeCategory = "";
    private string _activeAvatar = "(any)";
    private string _activeSearch = "";
    private CancellationTokenSource? _iconCts;

    public GearPickerPage()
    {
        InitializeComponent();
        CategoryList.ItemsSource = Categories;
        ItemsGrid.ItemsSource = ShownItems;
        BasketList.ItemsSource = Basket;
        Basket.CollectionChanged += (_, _) => RefreshBasketStatus();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (_allItems.Count == 0) await LoadCatalogAsync();
    }

    // Category accent palette — generic bucket names, colors only.
    internal static SolidColorBrush CategoryBrush(string category) => new(category switch
    {
        "Artifact" => Color.FromArgb(0xFF, 0x9B, 0x6B, 0x2E),
        "Ring" => Color.FromArgb(0xFF, 0x7B, 0x4F, 0x8B),
        "Medal" => Color.FromArgb(0xFF, 0xC0, 0xA8, 0x4F),
        "Insignia" => Color.FromArgb(0xFF, 0x5A, 0x7B, 0x8F),
        "UruForged" => Color.FromArgb(0xFF, 0xB7, 0x4F, 0x2E),
        "Legendary" => Color.FromArgb(0xFF, 0xD0, 0x8A, 0x2A),
        "Relic" => Color.FromArgb(0xFF, 0x6B, 0x4A, 0x2A),
        "Armor" => Color.FromArgb(0xFF, 0x3F, 0x6B, 0x4A),
        "Costume" => Color.FromArgb(0xFF, 0x7F, 0x5F, 0x9B),
        "Crafting" => Color.FromArgb(0xFF, 0x4F, 0x5F, 0x6F),
        "Currency" => Color.FromArgb(0xFF, 0xB0, 0x9F, 0x3F),
        "Consumable" => Color.FromArgb(0xFF, 0x4F, 0x7F, 0x6F),
        "Pet" => Color.FromArgb(0xFF, 0x6F, 0x8B, 0x4F),
        "CharacterToken" => Color.FromArgb(0xFF, 0x8B, 0x5F, 0x5F),
        "TeamUpGear" => Color.FromArgb(0xFF, 0x5F, 0x8B, 0x7B),
        _ => Color.FromArgb(0xFF, 0x55, 0x55, 0x60),
    });

    private async void LoadCatalog_Click(object sender, RoutedEventArgs e) => await LoadCatalogAsync();

    private async Task LoadCatalogAsync()
    {
        try
        {
            LoadBtn.IsEnabled = false;
            StatusText.Text = "fetching catalog...";
            var s = SettingsService.Current;
            using var c = new ServerApiClient(s.ServerBaseUrl, s.BearerToken, TimeSpan.FromSeconds(60));
            var resp = await c.GetItemCatalogAsync();
            if (resp == null) { StatusText.Text = "no catalog (server offline?)"; return; }

            _allItems.Clear();
            foreach (var entry in resp.Items) _allItems.Add(new GearItemCard(entry));

            // Rarity dropdown choices from server data (tier + leaf name).
            BasketEntry.RarityChoices.Clear();
            BasketEntry.RarityChoices.Add(new BasketEntry.RarityChoice("(auto — roll by level)", ""));
            foreach (var r in resp.Rarities)
                BasketEntry.RarityChoices.Add(new BasketEntry.RarityChoice($"[T{r.Tier}] {r.Name}", r.ProtoRef));

            Categories.Clear();
            Categories.Add(new GearCategory("(All)", _allItems.Count));
            // Synthetic section: every unique in the game, regardless of
            // which slot/class it belongs to.
            int uniqueCount = _allItems.Count(i => i.Entry.IsUnique);
            if (uniqueCount > 0)
                Categories.Add(new GearCategory("Unique", uniqueCount));
            foreach (var g in _allItems.GroupBy(i => i.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
                Categories.Add(new GearCategory(g.Key, g.Count()));

            AvatarFilter.Items.Clear();
            AvatarFilter.Items.Add("(any)");
            foreach (var av in _allItems.Where(i => !string.IsNullOrEmpty(i.Entry.Avatar))
                                        .Select(i => i.Entry.Avatar!)
                                        .Distinct()
                                        .OrderBy(a => a, StringComparer.Ordinal))
                AvatarFilter.Items.Add(av);
            AvatarFilter.SelectedIndex = 0;

            int idx = 0;
            for (int i = 1; i < Categories.Count; i++)
                if (Categories[i].Name == "Artifact") { idx = i; break; }
            CategoryList.SelectedIndex = idx;

            StatusText.Text = $"{resp.TotalItems:N0} items, {resp.Categories.Count} categories, {resp.Rarities.Count} rarities";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { LoadBtn.IsEnabled = true; }
    }

    // === Filtering ==========================================================
    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is GearCategory cat)
        {
            _activeCategory = cat.Name == "(All)" ? "" : cat.Name;
            ApplyFilters();
        }
    }

    private void AvatarFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _activeAvatar = (AvatarFilter.SelectedItem as string) ?? "(any)";
        ApplyFilters();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _activeSearch = (SearchBox.Text ?? "").Trim();
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        ShownItems.Clear();
        int total = 0;
        foreach (var item in _allItems)
        {
            // "Unique" is a synthetic section — it cuts across all base
            // categories via the IsUnique flag.
            if (_activeCategory == "Unique")
            {
                if (item.Entry.IsUnique == false) continue;
            }
            else if (!string.IsNullOrEmpty(_activeCategory) && item.Category != _activeCategory) continue;

            if (_activeAvatar != "(any)" && (item.Entry.Avatar ?? "") != _activeAvatar) continue;
            if (!string.IsNullOrEmpty(_activeSearch))
            {
                // Match the localized display name, the data leaf name, and
                // the full path.
                if (item.Name.IndexOf(_activeSearch, StringComparison.OrdinalIgnoreCase) < 0 &&
                    item.Entry.Name.IndexOf(_activeSearch, StringComparison.OrdinalIgnoreCase) < 0 &&
                    item.Path.IndexOf(_activeSearch, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
            }
            if (ShownItems.Count < 1200) ShownItems.Add(item);
            total++;
        }
        ShownCountText.Text = ShownItems.Count < total
            ? $"showing {ShownItems.Count:N0} of {total:N0} (refine to narrow)"
            : $"{total:N0} items";

        _ = LoadIconsForShownAsync();
        PrioritizeShownIcons();
    }

    // === Icons — fetched from the server's portrait endpoint ===============
    //
    // Full-catalog sweep: EVERY item with an icon path gets resolved, not
    // just the currently filtered view. Items visible right now are moved
    // to the front of the queue so the open category fills in first; the
    // rest resolve in the background and are served from the server's disk
    // cache instantly on later sessions.
    private int _iconsResolved;
    private int _iconsFailed;

    private bool _iconSweepRunning;

    private async Task LoadIconsForShownAsync()
    {
        // One sweep covers the whole catalog — a filter change while it's
        // running doesn't need a restart (and cancelling mid-flight aborts
        // in-progress HTTP requests, which shows up as connection-abort
        // noise in the server log). The sweep is only cancelled when the
        // user leaves the page.
        if (_iconSweepRunning) return;
        _iconSweepRunning = true;

        _iconCts = new CancellationTokenSource();
        var ct = _iconCts.Token;

        try
        {
            await RunIconSweepAsync(ct);
        }
        finally { _iconSweepRunning = false; }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _iconCts?.Cancel();
    }

    private async Task RunIconSweepAsync(CancellationToken ct)
    {
        // Visible-first ordering over the whole catalog.
        var shownSet = new HashSet<GearItemCard>(ShownItems);
        var queue = ShownItems.Where(NeedsIcon)
            .Concat(_allItems.Where(i => NeedsIcon(i) && shownSet.Contains(i) == false))
            .ToList();
        if (queue.Count == 0) return;

        static bool NeedsIcon(GearItemCard i)
            => i.Icon == null && i.IconRequested == false && string.IsNullOrEmpty(i.Entry.IconPath) == false;

        foreach (var card in queue) card.IconRequested = true;

        // Progress bar setup for this sweep.
        int sweepTotal = queue.Count;
        int sweepDone = 0;
        IconProgressPanel.Visibility = Visibility.Visible;
        IconProgressBar.Maximum = sweepTotal;
        IconProgressBar.Value = 0;
        IconProgressText.Text = $"0 / {sweepTotal:N0} icons";

        var s = SettingsService.Current;
        using var client = new ServerApiClient(s.ServerBaseUrl, s.BearerToken, TimeSpan.FromSeconds(30));
        // The server extracts each uncached texture by shelling out to the
        // extractor — every request is independent, so wide concurrency
        // scales nearly linearly until the disk cache warms up.
        using var gate = new SemaphoreSlim(12, 12);

        void BumpProgress()
        {
            int done = Interlocked.Increment(ref sweepDone);
            if ((done & 7) == 0 || done == sweepTotal)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    IconProgressBar.Value = done;
                    IconProgressText.Text = $"{done:N0} / {sweepTotal:N0} icons";
                });
            }
        }

        string portraitBase = s.ServerBaseUrl.TrimEnd('/');

        var tasks = queue.Select(async card =>
        {
            await gate.WaitAsync(ct);
            try
            {
                bool ok = await FetchIconCoreAsync(client, portraitBase, card, ct);
                if (ct.IsCancellationRequested) { card.IconRequested = false; return; }
                if (ok) Interlocked.Increment(ref _iconsResolved);
                else Interlocked.Increment(ref _iconsFailed);
                BumpProgress();
            }
            catch (OperationCanceledException) { card.IconRequested = false; }
            catch { Interlocked.Increment(ref _iconsFailed); BumpProgress(); }
            finally { gate.Release(); }
        }).ToList();

        try { await Task.WhenAll(tasks); } catch { }

        if (ct.IsCancellationRequested == false)
        {
            int resolved = _iconsResolved, failed = _iconsFailed;
            DispatcherQueue.TryEnqueue(() =>
            {
                IconProgressPanel.Visibility = Visibility.Collapsed;
                StatusText.Text = failed > 0
                    ? $"icons: {resolved:N0} resolved, {failed:N0} unavailable"
                    : $"icons: {resolved:N0} resolved";
            });
        }
    }

    // The byte fetch does double duty: it confirms the server can actually
    // produce this texture AND warms the server's disk cache. Rendering then
    // uses a direct URI source — the Image control's own loader handles
    // decode reliably, and its second request lands on the just-warmed cache.
    // Tries the TFC-backed portrait endpoint first, then falls back to
    // texbyname (inline .upk mips) — item icons are split between the two
    // stores, same as hero banners.
    private async Task<bool> FetchIconCoreAsync(ServerApiClient client, string portraitBase, GearItemCard card, CancellationToken ct)
    {
        string iconUrl = $"{portraitBase}/webapi/portrait?path={Uri.EscapeDataString(card.Entry.IconPath!)}&w=128&h=128";
        byte[]? png = await client.GetPortraitPngAsync(card.Entry.IconPath!, 128, 128, ct);
        if (ct.IsCancellationRequested) return false;
        if (png == null || png.Length == 0)
        {
            png = await client.GetTexturePngAsync(card.Entry.IconPath!, ct);
            if (ct.IsCancellationRequested) return false;
            iconUrl = $"{portraitBase}/webapi/texbyname?name={Uri.EscapeDataString(card.Entry.IconPath!)}";
        }
        if (png == null || png.Length == 0) return false;

        DispatcherQueue.TryEnqueue(() =>
        {
            // No DecodePixelWidth: many icons are 64px naturals served
            // straight from the .upk — asking the decoder to upscale
            // (decode width > natural) renders nothing on WinUI. The
            // Image element scales the natural bitmap to its 72px box.
            try { card.Icon = new BitmapImage(new Uri(iconUrl)); }
            catch { }
        });
        return true;
    }

    // Filter-change fast lane: fetch icons for what's on screen right now,
    // regardless of where the big background sweep happens to be. The sweep
    // marks its whole queue IconRequested up front, so this path keys off
    // Icon == null plus its own de-dupe set; a double fetch just hits the
    // warm server cache.
    private readonly HashSet<string> _priorityIconFetched = new();

    private async void PrioritizeShownIcons()
    {
        var targets = ShownItems
            .Where(i => i.Icon == null && string.IsNullOrEmpty(i.Entry.IconPath) == false && _priorityIconFetched.Add(i.ProtoRef))
            .Take(150)
            .ToList();
        if (targets.Count == 0) return;

        var ct = _iconCts?.Token ?? CancellationToken.None;
        var s = SettingsService.Current;
        try
        {
            using var client = new ServerApiClient(s.ServerBaseUrl, s.BearerToken, TimeSpan.FromSeconds(30));
            using var gate = new SemaphoreSlim(8, 8);
            string portraitBase = s.ServerBaseUrl.TrimEnd('/');

            var tasks = targets.Select(async card =>
            {
                await gate.WaitAsync(ct);
                try { await FetchIconCoreAsync(client, portraitBase, card, ct); }
                catch { }
                finally { gate.Release(); }
            }).ToList();
            await Task.WhenAll(tasks);
        }
        catch { /* best effort — the background sweep still covers everything */ }
    }

    // === Basket ============================================================
    private void ItemsGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not GearItemCard card) return;

        int defCount = (int)Math.Max(1, DefaultCountBox.Value);
        int defLevel = (int)Math.Max(0, DefaultLevelBox.Value);

        var existing = Basket.FirstOrDefault(b => b.ProtoRef == card.ProtoRef);
        if (existing != null) existing.Count += defCount;
        else Basket.Add(new BasketEntry(card.Entry, defCount, defLevel));
    }

    private void RemoveFromBasket_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string protoRef) return;
        var entry = Basket.FirstOrDefault(b => b.ProtoRef == protoRef);
        if (entry != null) Basket.Remove(entry);
    }

    private void ClearBasket_Click(object sender, RoutedEventArgs e) => Basket.Clear();

    private void RefreshBasketStatus()
    {
        if (Basket.Count == 0) { BasketStatusText.Text = "empty"; return; }
        int total = Basket.Sum(b => (int)b.Count);
        BasketStatusText.Text = $"{Basket.Count} distinct item(s), {total} total";
    }

    // === Send ==============================================================
    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (Basket.Count == 0) { StatusText.Text = "basket is empty"; return; }
        SendBtn.IsEnabled = false;
        try
        {
            var raw = (PlayerBox.Text ?? "*").Trim();
            string? name = null; string? dbId = null;
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                dbId = raw;
            else
                name = raw;

            int defLevel = (int)Math.Max(0, DefaultLevelBox.Value);

            var s = SettingsService.Current;
            using var c = new ServerApiClient(s.ServerBaseUrl, s.BearerToken, TimeSpan.FromSeconds(60));
            var req = new ItemGiveRequest
            {
                PlayerName = name,
                PlayerDbId = dbId,
                Items = Basket.Select(b => new ItemGiveBatchEntry
                {
                    ItemProtoRef = b.ProtoRef,
                    Count = (int)b.Count,
                    Level = b.Level > 0 ? (int)b.Level : defLevel,
                    RarityProtoRef = string.IsNullOrEmpty(b.RarityRef) ? null : b.RarityRef,
                }).ToList(),
            };
            StatusText.Text = $"sending {req.Items.Count} entries...";
            var (status, body) = await c.PostItemGiveAsync(req);

            string summary = $"HTTP {status}";
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<ItemGiveResponse>(body,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (parsed != null)
                {
                    int requested = req.Items.Sum(i => i.Count);
                    summary = $"{parsed.Message} — {parsed.GivenCount}/{requested} delivered";
                    var failures = parsed.Results?.Where(r => r.StatusCode >= 400 || r.GivenCount == 0).Take(3).ToList();
                    if (failures != null && failures.Count > 0)
                        summary += " | failed: " + string.Join("; ", failures.Select(f => f.Message));
                }
            }
            catch { summary = $"HTTP {status}: {body}"; }
            StatusText.Text = summary;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { SendBtn.IsEnabled = true; }
    }
}
