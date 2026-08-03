using System;
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

// Pricing formula the server sent for this refresh — pure numbers (plus
// the icon set, once loaded) shared by every card on the board so each
// one can recompute its own cost/reward as its tier picker moves without
// a server round-trip. Mirrors (and is only ever sourced from)
// Player.BountyHunt.cs's own constants.
public sealed class BountyFormula
{
    public int AcceptCostPerTier;
    public int EsPerTier;
    public int CsPerTier;
    public int LmPerTier;
    public int GuaranteedBisTier;
    public int PlayerCredits;

    public BitmapImage? CreditsIcon;
    public BitmapImage? EsIcon;
    public BitmapImage? CsIcon;
    public BitmapImage? LmIcon;
}

// One bounty poster on the board. Wraps the same NemesisEntryDto the
// Enemy Phantoms roster reads (this player's nemesis history) but with
// its own tier state and per-card status text, since the board renders
// every entry as an independent card rather than a table row.
public sealed class BountyCard : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public string HeroRef { get; }
    public string Title { get; }
    public string Detail { get; }
    public bool Defeated { get; }
    public Visibility DefeatedVisibility => Defeated ? Visibility.Visible : Visibility.Collapsed;

    // Bounty Hunt is disabled for defeated entries — same rule the old
    // Enemy Phantoms roster panel used, mirroring SetBountyTarget's own
    // server-side validation ("pick an active one") — plus, new here,
    // disabled when the player can't afford this tier's accept cost.
    public bool CanBountyHunt => Defeated == false && CanAfford;

    // Separate from CanBountyHunt (which also folds in affordability) —
    // the tier picker itself should stay editable even when the player is
    // temporarily short on credits, only the Post Bounty button locks.
    public bool CanEditTier => Defeated == false;

    private readonly BountyFormula _formula;

    private int _tier = 5;
    public int Tier
    {
        get => _tier;
        set
        {
            if (_tier == value) return;
            _tier = value;
            Raise(); Raise(nameof(TierLabel)); Raise(nameof(AcceptCostText)); Raise(nameof(RewardText));
            Raise(nameof(IsGuaranteedBis)); Raise(nameof(BisVisibility)); Raise(nameof(CanAfford)); Raise(nameof(CanBountyHunt));
            Raise(nameof(PostBountyLabel));
        }
    }

    private static readonly string[] s_tierNames =
    {
        "Trivial", "Easy", "Easy", "Moderate", "Moderate",
        "Hard", "Hard", "Brutal", "Brutal", "Legendary"
    };
    public string TierLabel => $"TIER {Tier} — {s_tierNames[Math.Clamp(Tier, 1, 10) - 1]}";

    public int AcceptCost => _formula.AcceptCostPerTier * Tier;
    public string AcceptCostText => $"{AcceptCost:N0}";
    public bool CanAfford => _formula.PlayerCredits >= AcceptCost;
    public string PostBountyLabel => CanAfford ? "Post Bounty" : "Not Enough Credits";

    public bool IsGuaranteedBis => Tier >= _formula.GuaranteedBisTier;
    public Visibility BisVisibility => IsGuaranteedBis ? Visibility.Visible : Visibility.Collapsed;
    public string EsRewardText => $"{_formula.EsPerTier * Tier:N0}";
    public string CsRewardText => $"{_formula.CsPerTier * Tier:N0}";
    public string LmRewardText => $"{_formula.LmPerTier * Tier:N0}";
    public string RewardText => $"{EsRewardText} Eternity Splinters · {CsRewardText} Cube Shards · {LmRewardText} Legendary Marks";

    // Icons are shared per-formula (one fetch for the whole board), so
    // these just forward to it — RaiseIconsChanged() is called once the
    // sweep resolves them, since the forward alone won't trip WinUI's
    // change detection.
    public BitmapImage? CreditsIcon => _formula.CreditsIcon;
    public BitmapImage? EsIcon => _formula.EsIcon;
    public BitmapImage? CsIcon => _formula.CsIcon;
    public BitmapImage? LmIcon => _formula.LmIcon;
    public void RaiseIconsChanged()
    {
        Raise(nameof(CreditsIcon)); Raise(nameof(EsIcon)); Raise(nameof(CsIcon)); Raise(nameof(LmIcon));
    }

    private string _cardStatus = "";
    public string CardStatus { get => _cardStatus; set { _cardStatus = value; Raise(); } }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { _isBusy = value; Raise(); } }

    // Portrait — resolved server-side from the same AvatarPrototype /
    // AgentPrototype the Enemy Phantoms roster's own portrait sweep reads,
    // just keyed straight off this entry's HeroRef instead of a name match.
    private BitmapImage? _portrait;
    public BitmapImage? Portrait { get => _portrait; set { _portrait = value; Raise(); } }
    public bool PortraitRequested;
    public System.Collections.Generic.List<string>? PortraitCandidates { get; }

    public BountyCard(NemesisEntryDto e, BountyFormula formula)
    {
        _formula = formula;
        HeroRef = e.HeroRef ?? string.Empty;
        Defeated = e.Defeated;
        PortraitCandidates = e.PortraitCandidates;

        string niceHero = string.IsNullOrEmpty(e.HeroName)
            ? HeroRef
            : e.HeroName.Split('/').Last();
        string suffix = string.IsNullOrEmpty(e.Suffix) ? "" : " " + e.Suffix;
        int safeRank = Math.Clamp(e.Rank, 0, 5);
        string stars = safeRank > 0 ? new string('★', safeRank) : "";

        string baseTitle = string.IsNullOrEmpty(e.LastKillerName) ? niceHero : e.LastKillerName;
        Title = $"{stars} {baseTitle}{suffix}".Trim();

        string revenge = e.RevengeKills > 0 ? $"  ·  your revenge {e.RevengeKills}" : "";
        Detail = $"{niceHero}  ·  {(e.IsBoss ? "boss" : "nemesis")}  ·  their kills {e.Kills}{revenge}";
    }
}

// Bounty Board — a standalone tool separate from the Enemy Phantoms
// roster panel it grew out of. Pick a nemesis, set a tier (1-10), post
// the bounty (pays a credits deposit up front); the player warps to a
// random Trial-of-the-Impossible arena and the chosen nemesis ambushes
// them there.
public sealed partial class BountyBoardPage : Page
{
    private readonly ServerApiClient _api = new();
    private readonly System.Collections.Generic.List<BountyCard> _allCards = new();
    public ObservableCollection<BountyCard> ShownCards { get; } = new();

    private BountyFormula _formula = new();
    private bool _portraitSweepRunning;
    private CancellationTokenSource? _pageCts = new();

    public BountyBoardPage()
    {
        InitializeComponent();
        BountyGrid.ItemsSource = ShownCards;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _pageCts = new();
        _ = RefreshAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _pageCts?.Cancel();
        base.OnNavigatedFrom(e);
    }

    private string TargetPlayer => string.IsNullOrWhiteSpace(PlayerBox.Text) ? "*" : PlayerBox.Text.Trim();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        RefreshBtn.IsEnabled = false;
        StatusText.Text = "loading board…";
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.GetNemesisListAsync(TargetPlayer);
            if (resp == null || resp.Ok == false)
            {
                StatusText.Text = resp?.Error ?? "bounty board load failed";
                return;
            }

            // Reuse the icons we've already fetched this session (they
            // never change), only the numbers refresh every call.
            _formula = new BountyFormula
            {
                AcceptCostPerTier = resp.BountyAcceptCostCreditsPerTier,
                EsPerTier = resp.BountyRewardEternitySplintersPerTier,
                CsPerTier = resp.BountyRewardCubeShardsPerTier,
                LmPerTier = resp.BountyRewardLegendaryMarksPerTier,
                GuaranteedBisTier = resp.BountyGuaranteedBisTier,
                PlayerCredits = resp.PlayerCredits,
                CreditsIcon = _formula.CreditsIcon,
                EsIcon = _formula.EsIcon,
                CsIcon = _formula.CsIcon,
                LmIcon = _formula.LmIcon,
            };
            CreditsBalanceText.Text = $"You have {resp.PlayerCredits:N0} credits";

            _allCards.Clear();
            foreach (var n in resp.Nemeses) _allCards.Add(new BountyCard(n, _formula));
            ApplyFilter();

            int active = _allCards.Count(c => c.Defeated == false);
            StatusText.Text = _allCards.Count == 0
                ? "no nemeses yet — die to an enemy phantom to earn a spot on the board"
                : $"{active} bounties available  ·  {_allCards.Count} total";

            _ = RunPortraitSweepAsync();
            _ = RunCurrencyIconSweepAsync(resp.CurrencyIcons);
        }
        catch (Exception ex) { StatusText.Text = $"error: {ex.Message}"; }
        finally { RefreshBtn.IsEnabled = true; }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ShowDefeatedSwitch_Toggled(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string q = SearchBox.Text?.Trim() ?? "";
        bool showDefeated = ShowDefeatedSwitch.IsOn;
        ShownCards.Clear();
        foreach (var card in _allCards)
        {
            if (card.Defeated && showDefeated == false) continue;
            if (q.Length > 0 && card.Title.Contains(q, StringComparison.OrdinalIgnoreCase) == false) continue;
            ShownCards.Add(card);
        }
    }

    // Same "warm the server cache via a byte fetch, then decode straight
    // from the /webapi/texbyname URI" pattern Enemy Phantoms uses for its
    // hero/boss rosters — just keyed off the candidates the server already
    // resolved for us per nemesis, no name lookup needed here.
    private async Task RunPortraitSweepAsync()
    {
        if (_portraitSweepRunning) return;
        _portraitSweepRunning = true;
        var ct = _pageCts?.Token ?? CancellationToken.None;
        try
        {
            string portraitBase = AppState.ServerUrl.TrimEnd('/');
            using var throttle = new SemaphoreSlim(8);
            var tasks = new System.Collections.Generic.List<Task>();
            foreach (var card in _allCards)
            {
                var candidates = card.PortraitCandidates;
                if (card.PortraitRequested || candidates is not { Count: > 0 }) continue;
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

    // Currency icons (Credits for the accept cost, ES/CS/LM for the
    // reward line) are the same for every card and never change within a
    // session — fetched once into the shared BountyFormula, then every
    // existing card is told to re-pull them via RaiseIconsChanged().
    private async Task RunCurrencyIconSweepAsync(CurrencyIconsDto? icons)
    {
        if (icons == null) return;
        var ct = _pageCts?.Token ?? CancellationToken.None;
        string portraitBase = AppState.ServerUrl.TrimEnd('/');

        async Task<BitmapImage?> ResolveAsync(System.Collections.Generic.List<string>? candidates)
        {
            if (candidates is not { Count: > 0 }) return null;
            foreach (string candidate in candidates)
            {
                try
                {
                    byte[]? png = await _api.GetTexturePngAsync(candidate, ct);
                    if (png == null || png.Length == 0) continue;
                    string url = $"{portraitBase}/webapi/texbyname?name={Uri.EscapeDataString(candidate)}";
                    return new BitmapImage(new Uri(url)) { DecodePixelWidth = 32 };
                }
                catch { }
            }
            return null;
        }

        try
        {
            bool needCredits = _formula.CreditsIcon == null;
            bool needEs = _formula.EsIcon == null;
            bool needCs = _formula.CsIcon == null;
            bool needLm = _formula.LmIcon == null;
            if (needCredits == false && needEs == false && needCs == false && needLm == false) return;

            var credits = needCredits ? await ResolveAsync(icons.Credits) : null;
            var es = needEs ? await ResolveAsync(icons.EternitySplinters) : null;
            var cs = needCs ? await ResolveAsync(icons.CubeShards) : null;
            var lm = needLm ? await ResolveAsync(icons.LegendaryMarks) : null;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (credits != null) _formula.CreditsIcon = credits;
                if (es != null) _formula.EsIcon = es;
                if (cs != null) _formula.CsIcon = cs;
                if (lm != null) _formula.LmIcon = lm;
                CreditsIconImage.Source = _formula.CreditsIcon;
                foreach (var card in _allCards) card.RaiseIconsChanged();
            });
        }
        catch { }
    }

    private void TierNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        // x:Bind already keeps BountyCard.Tier in sync (TwoWay) and Tier's
        // own setter raises every derived property — nothing else to do.
    }

    private async void PostBounty_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BountyCard card) return;
        var button = sender as Button;
        card.IsBusy = true;
        if (button != null) button.IsEnabled = false;
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostNemesisBountyHuntStartAsync(TargetPlayer, card.HeroRef, card.Tier);
            card.CardStatus = resp?.Message ?? resp?.Error ?? "no response";
            // Credits were spent server-side (or the attempt failed) either
            // way — refresh so the toolbar balance and every card's
            // CanAfford reflect the real post-spend total.
            await RefreshAsync();
        }
        catch (Exception ex) { card.CardStatus = $"error: {ex.Message}"; }
        finally
        {
            card.IsBusy = false;
            if (button != null) button.IsEnabled = card.CanBountyHunt;
        }
    }
}
