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

// Visual Squad Builder — click heroes from the roster into a lineup, tune
// level / lock / costume per slot, then save via the squads "savelist" op
// (an explicit member list, unlike the Phantoms page snapshot save). Saved
// squads spawn through the same backend as `!phantom squad spawn`.

public sealed class LineupSlot : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private static int s_nextId;
    public string SlotId { get; } = (++s_nextId).ToString();

    public PhantomHeroEntry Hero { get; }
    public string HeroName => string.IsNullOrEmpty(Hero.DisplayName) ? Hero.Name : Hero.DisplayName!;

    private BitmapImage? _portrait;
    public BitmapImage? Portrait { get => _portrait; set { _portrait = value; Raise(); } }

    private double _level;
    public double Level { get => _level; set { if (_level != value) { _level = Math.Clamp(value, 0, 60); Raise(); } } }

    private bool _lockLevel;
    public bool LockLevel { get => _lockLevel; set { if (_lockLevel != value) { _lockLevel = value; Raise(); } } }

    private bool _invincible;
    public bool Invincible { get => _invincible; set { if (_invincible != value) { _invincible = value; Raise(); } } }

    // Costume dropdown: index 0 = random, then the hero's costume pool.
    public List<PhantomCostumeEntry> Costumes { get; private set; } = new();
    private ObservableCollection<string> _costumeLabels = new() { "(random costume)" };
    public ObservableCollection<string> CostumeLabels { get => _costumeLabels; private set { _costumeLabels = value; Raise(); } }

    private int _costumeIndex;
    public int CostumeIndex { get => _costumeIndex; set { if (_costumeIndex != value && value >= 0) { _costumeIndex = value; Raise(); } } }

    public string? SelectedCostumeRef =>
        _costumeIndex > 0 && _costumeIndex - 1 < Costumes.Count ? Costumes[_costumeIndex - 1].ProtoRef : null;

    public LineupSlot(PhantomHeroEntry hero, int level, bool lockLevel, bool invincible = false)
    {
        Hero = hero;
        _level = level;
        _lockLevel = lockLevel;
        _invincible = invincible;
    }

    public void SetCostumes(List<PhantomCostumeEntry> costumes, string? selectRef = null)
    {
        Costumes = costumes;
        var labels = new ObservableCollection<string> { "(random costume)" };
        int select = 0;
        for (int i = 0; i < costumes.Count; i++)
        {
            labels.Add(string.IsNullOrEmpty(costumes[i].DisplayName) ? costumes[i].Name : costumes[i].DisplayName!);
            if (selectRef != null && string.Equals(costumes[i].ProtoRef, selectRef, StringComparison.OrdinalIgnoreCase))
                select = i + 1;
        }
        CostumeLabels = labels;
        CostumeIndex = select;
    }
}

public sealed partial class SquadBuilderPage : Page
{
    private readonly ServerApiClient _api = new();
    private readonly List<PhantomHeroCard> _allHeroes = new();
    public ObservableCollection<PhantomHeroCard> ShownHeroes { get; } = new();
    public ObservableCollection<LineupSlot> Lineup { get; } = new();
    public ObservableCollection<SquadRow> Squads { get; } = new();

    // Costume pools cache: heroProtoRef → costumes. Shared across slots so
    // adding the same hero twice doesn't refetch.
    private readonly Dictionary<string, List<PhantomCostumeEntry>> _costumeCache = new(StringComparer.OrdinalIgnoreCase);

    private bool _portraitSweepRunning;
    private CancellationTokenSource? _pageCts = new();

    public SquadBuilderPage()
    {
        InitializeComponent();
        HeroList.ItemsSource = ShownHeroes;
        LineupList.ItemsSource = Lineup;
        SquadList.ItemsSource = Squads;
        Lineup.CollectionChanged += (_, _) => LineupTitle.Text = $"Lineup ({Lineup.Count})";
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
                        foreach (string candidate in candidates)
                        {
                            byte[]? png = await _api.GetTexturePngAsync(candidate, ct);
                            if (png == null || png.Length == 0) continue;
                            string url = $"{portraitBase}/webapi/texbyname?name={Uri.EscapeDataString(candidate)}";
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                try
                                {
                                    var bmp = new BitmapImage(new Uri(url)) { DecodePixelWidth = 96 };
                                    card.Portrait = bmp;
                                    // Any lineup slots already holding this hero pick it up too.
                                    foreach (var slot in Lineup)
                                        if (string.Equals(slot.Hero.ProtoRef, card.Entry.ProtoRef, StringComparison.OrdinalIgnoreCase))
                                            slot.Portrait = bmp;
                                }
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

    // ---------------- Lineup ----------------

    private async void HeroList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PhantomHeroCard card) return;

        var slot = new LineupSlot(card.Entry, level: 0, lockLevel: false) { Portrait = card.Portrait };
        Lineup.Add(slot);
        await LoadCostumesIntoSlotAsync(slot, null);
    }

    private async Task LoadCostumesIntoSlotAsync(LineupSlot slot, string? selectRef)
    {
        try
        {
            if (_costumeCache.TryGetValue(slot.Hero.ProtoRef, out var cached) == false)
            {
                _api.BaseUrl = AppState.ServerUrl;
                var resp = await _api.GetPhantomCostumesAsync(slot.Hero.ProtoRef);
                cached = resp?.Costumes ?? new List<PhantomCostumeEntry>();
                _costumeCache[slot.Hero.ProtoRef] = cached;
            }
            slot.SetCostumes(cached, selectRef);
        }
        catch
        {
            slot.SetCostumes(new List<PhantomCostumeEntry>(), null);
        }
    }

    private void RemoveSlot_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string slotId) return;
        var slot = Lineup.FirstOrDefault(s => s.SlotId == slotId);
        if (slot != null) Lineup.Remove(slot);
    }

    private void ClearLineup_Click(object sender, RoutedEventArgs e) => Lineup.Clear();

    // ---------------- Save / spawn ----------------

    private object[] BuildMemberPayload()
        => Lineup.Select(s => (object)new
        {
            avatarRef = s.Hero.ProtoRef,
            level = (int)s.Level,
            lockLevel = s.LockLevel,
            costumeRef = s.SelectedCostumeRef,
            invincible = s.Invincible,
        }).ToArray();

    private async Task<bool> SaveLineupAsync(string name)
    {
        if (Lineup.Count == 0) { SquadStatusText.Text = "lineup is empty — click heroes to add them"; return false; }

        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostPhantomSquadSaveListAsync(TargetPlayer, name, BuildMemberPayload());
            string message = resp?.Message ?? resp?.Error ?? "no response";
            await RefreshSquadsAsync();
            SquadStatusText.Text = message;
            return resp?.Message != null && resp.Message.Contains("saved", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            SquadStatusText.Text = $"error: {ex.Message}";
            return false;
        }
    }

    private async void SaveSquad_Click(object sender, RoutedEventArgs e)
    {
        string name = SquadNameBox.Text?.Trim() ?? "";
        if (name.Length == 0) { SquadStatusText.Text = "enter a squad name first"; return; }
        await SaveLineupAsync(name);
    }

    private async void SaveAndSpawn_Click(object sender, RoutedEventArgs e)
    {
        string name = SquadNameBox.Text?.Trim() ?? "";
        if (name.Length == 0) { SquadStatusText.Text = "enter a squad name first"; return; }
        if (await SaveLineupAsync(name) == false) return;
        await SquadOpAsync("spawn", name);
    }

    // ---------------- Saved squads ----------------

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
        }
        catch (Exception ex)
        {
            SquadStatusText.Text = $"error: {ex.Message}";
        }
    }

    private async void EditSquad_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string name) return;
        var row = Squads.FirstOrDefault(s => s.Name == name);
        if (row?.Entry.Members == null || row.Entry.Members.Count == 0)
        {
            SquadStatusText.Text = "squad has no member data — refresh and retry";
            return;
        }
        if (_allHeroes.Count == 0)
        {
            SquadStatusText.Text = "load the roster first";
            return;
        }

        Lineup.Clear();
        SquadNameBox.Text = name;
        foreach (var m in row.Entry.Members)
        {
            var card = _allHeroes.FirstOrDefault(h => string.Equals(h.Entry.ProtoRef, m.AvatarRef, StringComparison.OrdinalIgnoreCase));
            if (card == null) continue;
            var slot = new LineupSlot(card.Entry, m.LockLevel ? m.Level : 0, m.LockLevel, m.Invincible) { Portrait = card.Portrait };
            Lineup.Add(slot);
            await LoadCostumesIntoSlotAsync(slot, m.CostumeRef);
        }
        SquadStatusText.Text = $"editing '{name}' — Save Squad overwrites it";
    }

    private async void SpawnSquad_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string name) return;
        await SquadOpAsync("spawn", name);
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

    // ---------------- Rotation picker ----------------

    private async void OpenRotation_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string slotId) return;
        var slot = Lineup.FirstOrDefault(s => s.SlotId == slotId);
        if (slot == null) return;

        _api.BaseUrl = AppState.ServerUrl;

        var loading = new TextBlock { Text = "loading powers…", Opacity = 0.7 };
        var body = new StackPanel { Spacing = 8, MinWidth = 420 };
        body.Children.Add(new TextBlock
        {
            Text = "Preference applies to every phantom of this hero — not just this squad slot. It survives region hops and character switches.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            FontSize = 12,
        });
        body.Children.Add(loading);

        var dlg = new ContentDialog
        {
            Title = $"Rotation — {slot.HeroName}",
            Content = body,
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Clear preference",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            IsSecondaryButtonEnabled = false,
            XamlRoot = this.XamlRoot,
        };

        RotationResponse? loaded = null;
        try
        {
            loaded = await _api.GetRotationAsync(TargetPlayer, slot.Hero.ProtoRef);
        }
        catch (Exception ex) { SquadStatusText.Text = $"error: {ex.Message}"; return; }

        body.Children.Remove(loading);
        if (loaded == null || loaded.Ok == false)
        {
            body.Children.Add(new TextBlock { Text = loaded?.Error ?? "rotation lookup failed", Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Salmon) });
            await dlg.ShowAsync();
            return;
        }

        string current = loaded.PreferredPower ?? string.Empty;
        var group = "RotSlot_" + slot.SlotId;

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 380 };
        var list = new StackPanel { Spacing = 3 };
        scroll.Content = list;

        var noneRadio = new RadioButton
        {
            Content = "No preference (default AI)",
            GroupName = group,
            IsChecked = string.IsNullOrEmpty(current),
            Tag = string.Empty,
        };
        list.Children.Add(noneRadio);
        list.Children.Add(new Border { Height = 1, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray), Opacity = 0.2, Margin = new Thickness(0, 4, 0, 4) });

        string chosenRef = current;
        foreach (var p in loaded.Powers)
        {
            var container = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var rb = new RadioButton
            {
                GroupName = group,
                IsChecked = string.Equals(p.Ref, current, StringComparison.OrdinalIgnoreCase),
                Tag = p.Ref,
            };
            rb.Checked += (_, _) => { chosenRef = (rb.Tag as string) ?? string.Empty; dlg.IsPrimaryButtonEnabled = true; };
            container.Children.Add(rb);
            var text = new StackPanel { Spacing = 0 };
            text.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(p.Name) ? p.Ref : p.Name, FontSize = 13 });
            text.Children.Add(new TextBlock { Text = p.Level > 0 ? $"unlocks at level {p.Level}" : (p.FullRef ?? ""), FontSize = 10, Opacity = 0.55 });
            container.Children.Add(text);
            list.Children.Add(container);
        }
        noneRadio.Checked += (_, _) => { chosenRef = string.Empty; dlg.IsPrimaryButtonEnabled = true; };

        body.Children.Add(scroll);
        dlg.IsSecondaryButtonEnabled = !string.IsNullOrEmpty(current);

        var result = await dlg.ShowAsync();
        try
        {
            if (result == ContentDialogResult.Primary)
            {
                var resp = await _api.PostRotationAsync(TargetPlayer, slot.Hero.ProtoRef, string.IsNullOrEmpty(chosenRef) ? null : chosenRef);
                SquadStatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
            }
            else if (result == ContentDialogResult.Secondary)
            {
                var resp = await _api.PostRotationAsync(TargetPlayer, slot.Hero.ProtoRef, null);
                SquadStatusText.Text = resp?.Message ?? resp?.Error ?? "preference cleared";
            }
        }
        catch (Exception ex) { SquadStatusText.Text = $"error: {ex.Message}"; }
    }

    // ---------------- Sharing codes ----------------

    private async void ExportCode_Click(object sender, RoutedEventArgs e)
    {
        if (Lineup.Count == 0)
        {
            StatusText.Text = "add heroes to the lineup before exporting";
            return;
        }

        var payload = new SquadCode.Payload
        {
            Name = (SquadNameBox.Text?.Trim() is { Length: > 0 } n) ? n : null,
            Members = Lineup.Select(s => new SquadCode.Member
            {
                HeroRef    = s.Hero.ProtoRef,
                Level      = (int)s.Level,
                LockLevel  = s.LockLevel,
                CostumeRef = s.SelectedCostumeRef,
                Invincible = s.Invincible,
            }).ToList(),
        };
        string code = SquadCode.Encode(payload);

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
            Title = $"Squad Code ({Lineup.Count} hero" + (Lineup.Count == 1 ? "" : "s") + ")",
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

    private async void ImportCode_Click(object sender, RoutedEventArgs e)
    {
        if (_allHeroes.Count == 0)
        {
            StatusText.Text = "load the roster first — Import needs it to resolve heroes";
            return;
        }

        var input = new TextBox
        {
            PlaceholderText = "paste squad code here…",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            MinHeight = 90,
        };
        var replaceCheck = new CheckBox { Content = "Replace current lineup (uncheck to append)", IsChecked = true };
        var dlg = new ContentDialog
        {
            Title = "Import Squad Code",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Only proto-ref IDs are transferred — no game files. Any hero or costume not on your install falls back to defaults.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 },
                    input,
                    replaceCheck,
                },
            },
            PrimaryButtonText = "Import",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        var result = await dlg.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var payload = SquadCode.TryDecode(input.Text ?? string.Empty, out string error);
        if (payload is null)
        {
            SquadStatusText.Text = $"import failed: {error}";
            return;
        }

        if (replaceCheck.IsChecked == true) Lineup.Clear();
        if (!string.IsNullOrEmpty(payload.Name)) SquadNameBox.Text = payload.Name;

        int added = 0, skipped = 0;
        foreach (var m in payload.Members)
        {
            var card = _allHeroes.FirstOrDefault(h => string.Equals(h.Entry.ProtoRef, m.HeroRef, StringComparison.OrdinalIgnoreCase));
            if (card == null) { skipped++; continue; }
            var slot = new LineupSlot(card.Entry, m.LockLevel ? m.Level : 0, m.LockLevel, m.Invincible) { Portrait = card.Portrait };
            Lineup.Add(slot);
            await LoadCostumesIntoSlotAsync(slot, m.CostumeRef);
            added++;
        }

        SquadStatusText.Text = skipped == 0
            ? $"imported {added} hero" + (added == 1 ? "" : "s") + " from code"
            : $"imported {added}, skipped {skipped} (unknown hero refs — different install?)";
    }
}
