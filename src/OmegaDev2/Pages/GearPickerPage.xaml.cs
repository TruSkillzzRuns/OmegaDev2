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
    public string Name => Entry.Name;
    public string Category => Entry.Category;
    public string SubLabel => Entry.Avatar ?? Entry.Slot ?? Entry.Category;
    public string Path => Entry.Path;
    public Brush CategoryBrush { get; }

    private BitmapImage? _icon;
    public BitmapImage? Icon { get => _icon; set { _icon = value; Raise(); } }
    public bool IconRequested;

    public GearItemCard(ItemCatalogEntry entry)
    {
        Entry = entry;
        CategoryBrush = GearPickerPage.CategoryBrush(entry.Category);
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
    public string Name => Entry.Name;

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
            if (!string.IsNullOrEmpty(_activeCategory) && item.Category != _activeCategory) continue;
            if (_activeAvatar != "(any)" && (item.Entry.Avatar ?? "") != _activeAvatar) continue;
            if (!string.IsNullOrEmpty(_activeSearch))
            {
                if (item.Name.IndexOf(_activeSearch, StringComparison.OrdinalIgnoreCase) < 0 &&
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

    private async Task LoadIconsForShownAsync()
    {
        _iconCts?.Cancel();
        var cts = new CancellationTokenSource();
        _iconCts = cts;
        var ct = cts.Token;

        // Visible-first ordering over the whole catalog.
        var shownSet = new HashSet<GearItemCard>(ShownItems);
        var queue = ShownItems.Where(NeedsIcon)
            .Concat(_allItems.Where(i => NeedsIcon(i) && shownSet.Contains(i) == false))
            .ToList();
        if (queue.Count == 0) return;

        static bool NeedsIcon(GearItemCard i)
            => i.Icon == null && i.IconRequested == false && string.IsNullOrEmpty(i.Entry.IconPath) == false;

        foreach (var card in queue) card.IconRequested = true;

        var s = SettingsService.Current;
        using var client = new ServerApiClient(s.ServerBaseUrl, s.BearerToken, TimeSpan.FromSeconds(30));
        using var gate = new SemaphoreSlim(6, 6);

        var tasks = queue.Select(async card =>
        {
            await gate.WaitAsync(ct);
            try
            {
                byte[]? png = await client.GetPortraitPngAsync(card.Entry.IconPath!, 96, 96, ct);
                if (ct.IsCancellationRequested) { card.IconRequested = false; return; }
                if (png == null || png.Length == 0)
                {
                    Interlocked.Increment(ref _iconsFailed);
                    return;
                }
                DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                        await ms.WriteAsync(png.AsBuffer());
                        ms.Seek(0);
                        await bmp.SetSourceAsync(ms);
                        card.Icon = bmp;
                    }
                    catch { }
                });
                int done = Interlocked.Increment(ref _iconsResolved);
                if ((done & 127) == 0)
                    DispatcherQueue.TryEnqueue(() => StatusText.Text = $"icons: {done:N0} resolved...");
            }
            catch (OperationCanceledException) { card.IconRequested = false; }
            catch { Interlocked.Increment(ref _iconsFailed); }
            finally { gate.Release(); }
        }).ToList();

        try { await Task.WhenAll(tasks); } catch { }

        if (ct.IsCancellationRequested == false)
        {
            int resolved = _iconsResolved, failed = _iconsFailed;
            DispatcherQueue.TryEnqueue(() =>
                StatusText.Text = failed > 0
                    ? $"icons: {resolved:N0} resolved, {failed:N0} unavailable"
                    : $"icons: {resolved:N0} resolved");
        }
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
