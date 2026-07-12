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
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using OmegaDev2.Services;
using Windows.UI;

namespace OmegaDev2.Pages;

public sealed class StashContainerRow
{
    public InventoryContainer Entry { get; }
    public string Name { get; }
    public string SubLabel { get; }
    public string CountText { get; }

    public StashContainerRow(InventoryContainer entry)
    {
        Entry = entry;
        Name = entry.Name;
        SubLabel = entry.Category ?? "";
        CountText = entry.Capacity > 0 ? $"{entry.Count}/{entry.Capacity}" : entry.Count.ToString();
    }
}

public sealed class StashItemCard : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    // Rarity tier -> accent color. Colors only — the scale runs from muted
    // grey up through the classic loot ladder hues.
    private static readonly Color[] s_tierColors =
    {
        Color.FromArgb(0xFF, 0x9B, 0x9B, 0x9B), // 0/unknown
        Color.FromArgb(0xFF, 0xD8, 0xD8, 0xD8), // T1
        Color.FromArgb(0xFF, 0x4C, 0xB0, 0x4C), // T2
        Color.FromArgb(0xFF, 0x3E, 0x8E, 0xD0), // T3
        Color.FromArgb(0xFF, 0x9B, 0x59, 0xD0), // T4
        Color.FromArgb(0xFF, 0xD0, 0x3A, 0x3A), // T5
        Color.FromArgb(0xFF, 0xE0, 0xB0, 0x4A), // T6
        Color.FromArgb(0xFF, 0xE0, 0x8A, 0x2E), // T7+
    };

    public InventoryItemEntry Entry { get; }
    public string EntityId => Entry.EntityId;
    public string Name => Entry.Name;
    public string SubLabel
    {
        get
        {
            var parts = new List<string>(3);
            if (string.IsNullOrEmpty(Entry.Rarity) == false) parts.Add(Entry.Rarity!);
            if (Entry.Level > 0) parts.Add($"lvl {Entry.Level}");
            if (Entry.Stack > 1) parts.Add($"×{Entry.Stack}");
            return string.Join(" · ", parts);
        }
    }
    public string Tooltip => Entry.Path;
    public Brush RarityBrush { get; }

    private BitmapImage? _icon;
    public BitmapImage? Icon { get => _icon; set { _icon = value; Raise(); } }
    public bool IconRequested;

    public StashItemCard(InventoryItemEntry entry)
    {
        Entry = entry;
        int tier = Math.Clamp(entry.RarityTier, 0, s_tierColors.Length - 1);
        RarityBrush = new SolidColorBrush(s_tierColors[tier]);
    }
}

// Stash Manager — containers on the left, item cards (icon, name, rarity
// accent, stack) on the right, per-item delete with server-side ownership
// verification.
public sealed partial class StashManagerPage : Page
{
    private readonly ServerApiClient _api = new();
    public ObservableCollection<StashContainerRow> Containers { get; } = new();
    public ObservableCollection<StashItemCard> ShownItems { get; } = new();

    private readonly List<StashItemCard> _currentContainerItems = new();
    private CancellationTokenSource? _pageCts = new();

    public StashManagerPage()
    {
        InitializeComponent();
        ContainerList.ItemsSource = Containers;
        ItemsGrid.ItemsSource = ShownItems;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _pageCts?.Cancel();
        base.OnNavigatedFrom(e);
    }

    private string TargetPlayer => string.IsNullOrWhiteSpace(PlayerBox.Text) ? "*" : PlayerBox.Text.Trim();

    private async void LoadInventory_Click(object sender, RoutedEventArgs e)
    {
        LoadBtn.IsEnabled = false;
        StatusText.Text = "loading inventory…";
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.GetInventoryAsync(TargetPlayer);
            if (resp == null || resp.Ok == false)
            {
                StatusText.Text = resp?.Error ?? "server unreachable";
                return;
            }

            int selected = ContainerList.SelectedIndex;
            Containers.Clear();
            foreach (var container in resp.Containers)
                Containers.Add(new StashContainerRow(container));

            StatusText.Text = $"{resp.Player}: {resp.TotalItems:N0} item(s) across {resp.Containers.Count} container(s)";
            ContainerList.SelectedIndex = selected >= 0 && selected < Containers.Count ? selected : (Containers.Count > 0 ? 0 : -1);
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

    private void ContainerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _currentContainerItems.Clear();
        if (ContainerList.SelectedItem is StashContainerRow row)
            foreach (var item in row.Entry.Items)
                _currentContainerItems.Add(new StashItemCard(item));
        ApplyItemFilter();
        _ = FetchIconsAsync();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyItemFilter();

    private void ApplyItemFilter()
    {
        string q = SearchBox.Text?.Trim() ?? "";
        ShownItems.Clear();
        foreach (var card in _currentContainerItems)
        {
            if (q.Length > 0 &&
                card.Name.Contains(q, StringComparison.OrdinalIgnoreCase) == false &&
                card.Entry.Path.Contains(q, StringComparison.OrdinalIgnoreCase) == false)
                continue;
            ShownItems.Add(card);
        }
    }

    private async Task FetchIconsAsync()
    {
        var targets = _currentContainerItems
            .Where(c => c.Icon == null && c.IconRequested == false && string.IsNullOrEmpty(c.Entry.IconPath) == false)
            .ToList();
        if (targets.Count == 0) return;

        var ct = _pageCts?.Token ?? CancellationToken.None;
        string portraitBase = AppState.ServerUrl.TrimEnd('/');
        try
        {
            using var throttle = new SemaphoreSlim(8);
            var tasks = targets.Select(async card =>
            {
                card.IconRequested = true;
                await throttle.WaitAsync(ct);
                try
                {
                    // Portrait endpoint first (TFC store), texbyname fallback
                    // (.upk inline mips) — same split the Gear Picker handles.
                    string url = $"{portraitBase}/webapi/portrait?path={Uri.EscapeDataString(card.Entry.IconPath!)}&w=128&h=128";
                    byte[]? png = await _api.GetPortraitPngAsync(card.Entry.IconPath!, 128, 128, ct);
                    if (png == null || png.Length == 0)
                    {
                        png = await _api.GetTexturePngAsync(card.Entry.IconPath!, ct);
                        url = $"{portraitBase}/webapi/texbyname?name={Uri.EscapeDataString(card.Entry.IconPath!)}";
                    }
                    if (png == null || png.Length == 0) return;

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        try { card.Icon = new BitmapImage(new Uri(url)); }
                        catch { }
                    });
                }
                catch { }
                finally { throttle.Release(); }
            }).ToList();
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private async void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string entityId) return;

        var card = _currentContainerItems.FirstOrDefault(c => c.EntityId == entityId);
        string itemName = card?.Name ?? "this item";

        // AAA rule: destructive actions confirm first.
        var dialog = new ContentDialog
        {
            Title = "Delete item?",
            Content = $"Permanently delete \"{itemName}\"? This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostInventoryDeleteAsync(TargetPlayer, entityId);
            StatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
            if (resp?.Ok == true && card != null)
            {
                _currentContainerItems.Remove(card);
                ShownItems.Remove(card);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"error: {ex.Message}";
        }
    }
}
