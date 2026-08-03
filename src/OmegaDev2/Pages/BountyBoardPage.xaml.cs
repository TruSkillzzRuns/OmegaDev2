using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using OmegaDev2.Services;

namespace OmegaDev2.Pages;

// Shared display/icon state for the whole board — fetched once per
// refresh, referenced by every card so cards don't each carry their own
// copy of the reward-currency icons.
public sealed class BountyBoardFormula
{
    public int PlayerCredits;
    public int MaxLosses;
    public int EsPerTier;
    public int CsPerTier;
    public int LmPerTier;
    public int GuaranteedBisTier;

    public BitmapImage? CreditsIcon;
    public BitmapImage? EsIcon;
    public BitmapImage? CsIcon;
    public BitmapImage? LmIcon;
}

// One "WANTED" poster on the board — a slot the server rolled, not
// something the player configures. Tier/cost/reward are read-only here;
// only action is Post Bounty (pay AcceptCost, warp, get ambushed).
public sealed class BountyBoardCard : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public int SlotIndex { get; }
    public string HeroRef { get; }
    public string Title { get; }
    public string Detail { get; }
    public int Rank { get; }
    public int LossCount { get; }
    public bool Defeated { get; }
    public bool Fled { get; }
    public bool RewardCollected { get; }

    // A slot is only truly "done" (dimmed, no more actions) once it's Fled
    // or its reward has actually been collected — a Defeated-but-uncollected
    // slot stays fully visible so "go collect your reward" doesn't fade
    // into the background.
    public bool IsFullyDone => Fled || (Defeated && RewardCollected);
    public bool Resolved => IsFullyDone;
    public bool CanPost => Defeated == false && Fled == false && CanAfford;
    public bool CanCollect => Defeated && RewardCollected == false;

    private readonly BountyBoardFormula _formula;

    private static readonly string[] s_tierNames =
    {
        "Trivial", "Easy", "Easy", "Moderate", "Moderate",
        "Hard", "Hard", "Brutal", "Brutal", "Legendary"
    };
    public string TierLabel => $"RANK {Rank} — {s_tierNames[Math.Clamp(Rank, 1, 10) - 1]}";

    public int AcceptCost { get; }
    public string AcceptCostText => $"{AcceptCost:N0}";
    public bool CanAfford => _formula.PlayerCredits >= AcceptCost;

    public string StatusBadgeText => RewardCollected ? "COLLECTED" : Defeated ? "DEFEATED — COLLECT REWARD" : Fled ? "FLED" : $"{LossCount}/{_formula.MaxLosses} LOSSES";
    public Visibility StatusBadgeVisibility => Defeated || Fled || LossCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    private static readonly Microsoft.UI.Xaml.Media.Brush s_defeatedBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x55, 0x55, 0x60));
    private static readonly Microsoft.UI.Xaml.Media.Brush s_fledBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xB0, 0x3A, 0x3A));
    private static readonly Microsoft.UI.Xaml.Media.Brush s_lossBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x8A, 0x6D, 0x1A));
    private static readonly Microsoft.UI.Xaml.Media.Brush s_collectBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xE0, 0xB8, 0x4B));
    public Microsoft.UI.Xaml.Media.Brush StatusBadgeBrush => CanCollect ? s_collectBrush : RewardCollected ? s_defeatedBrush : Fled ? s_fledBrush : s_lossBrush;
    public double CardOpacity => IsFullyDone ? 0.55 : 1.0;
    public Visibility LossWarningVisibility => Defeated == false && Fled == false && LossCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string LossWarningText => LossCount >= _formula.MaxLosses - 1
        ? "One more loss and this bounty flees for good!"
        : $"Lose {_formula.MaxLosses - LossCount} more time(s) and it flees.";

    public string ActionLabel => Fled
        ? "Bounty Fled"
        : RewardCollected
            ? "Reward Collected"
            : Defeated
                ? "Collect Rewards"
                : (CanAfford ? "Post Bounty" : "Not Enough Credits");
    public bool ActionEnabled => CanPost || CanCollect;

    public bool IsGuaranteedBis => Rank >= _formula.GuaranteedBisTier;
    public Visibility BisVisibility => IsGuaranteedBis ? Visibility.Visible : Visibility.Collapsed;
    public string EsRewardText => $"{_formula.EsPerTier * Rank:N0}";
    public string CsRewardText => $"{_formula.CsPerTier * Rank:N0}";
    public string LmRewardText => $"{_formula.LmPerTier * Rank:N0}";

    public BitmapImage? CreditsIcon => _formula.CreditsIcon;
    public BitmapImage? EsIcon => _formula.EsIcon;
    public BitmapImage? CsIcon => _formula.CsIcon;
    public BitmapImage? LmIcon => _formula.LmIcon;
    public void RaiseIconsChanged()
    {
        Raise(nameof(CreditsIcon)); Raise(nameof(EsIcon)); Raise(nameof(CsIcon)); Raise(nameof(LmIcon));
    }
    public void RaiseAffordabilityChanged()
    {
        Raise(nameof(CanAfford)); Raise(nameof(CanPost));
    }

    private string _cardStatus = "";
    public string CardStatus { get => _cardStatus; set { _cardStatus = value; Raise(); } }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { _isBusy = value; Raise(); } }

    private BitmapImage? _portrait;
    public BitmapImage? Portrait { get => _portrait; set { _portrait = value; Raise(); } }
    public bool PortraitRequested;
    public System.Collections.Generic.List<string>? PortraitCandidates { get; }

    public BountyBoardCard(BountyBoardSlotDto e, BountyBoardFormula formula)
    {
        _formula = formula;
        SlotIndex = e.SlotIndex;
        HeroRef = e.HeroRef ?? string.Empty;
        Rank = e.Rank;
        LossCount = e.LossCount;
        Defeated = e.Defeated;
        Fled = e.Fled;
        RewardCollected = e.RewardCollected;
        AcceptCost = e.AcceptCost;
        PortraitCandidates = e.PortraitCandidates;

        string niceHero = string.IsNullOrEmpty(e.HeroName) ? HeroRef : e.HeroName.Split('/').Last();
        Title = niceHero;
        Detail = e.IsBoss ? "boss" : "nemesis";
    }
}

// Bounty Board — 6 randomly-rolled bounties shown at once, independent of
// the player's personal Nemesis roster/kill history. Post a bounty (pays
// its rank-scaled Credits cost), warp to a random arena, get ambushed
// 30-60s after arrival. Losing ranks the bounty up (tougher next time);
// the 3rd loss to the same bounty makes it flee for good. Once every slot
// is resolved (defeated or fled) the whole board rerolls.
public sealed partial class BountyBoardPage : Page
{
    private readonly ServerApiClient _api = new();
    private readonly System.Collections.Generic.List<BountyBoardCard> _cards = new();
    public ObservableCollection<BountyBoardCard> ShownCards { get; } = new();

    private BountyBoardFormula _formula = new();
    private bool _portraitSweepRunning;
    private bool _refreshInFlight;
    private CancellationTokenSource? _pageCts = new();
    private readonly DispatcherQueueTimer _timer;

    public BountyBoardPage()
    {
        // Auto-refresh so a bounty defeated/lost in-game shows up here
        // without a manual click — same "poll on a timer while the page is
        // visible" pattern EnemyPhantomsPage uses for its live health bars,
        // just on a slower cadence since board state only changes on a
        // kill/loss event (30-60s+ after posting), not every second.
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(5);
        _timer.Tick += async (_, _) => await RefreshAsync();

        InitializeComponent();
        BountyGrid.ItemsSource = ShownCards;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _pageCts = new();
        _ = RefreshAsync();
        _timer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _timer.Stop();
        _pageCts?.Cancel();
        base.OnNavigatedFrom(e);
    }

    private string TargetPlayer => string.IsNullOrWhiteSpace(PlayerBox.Text) ? "*" : PlayerBox.Text.Trim();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        // Guards against the 5s auto-refresh timer overlapping a manual
        // click or a just-finished Post/Collect action's own follow-up
        // refresh — GetBountyBoardAsync isn't instant, and two in flight
        // at once would just double the server hit for no benefit.
        if (_refreshInFlight) return;
        _refreshInFlight = true;

        // Only show the "loading…" status on the very first load — once
        // cards are already showing, a silent background refresh every 5s
        // shouldn't flicker the status line away from whatever it's saying.
        bool isBackgroundRefresh = _cards.Count > 0;
        RefreshBtn.IsEnabled = false;
        if (isBackgroundRefresh == false) StatusText.Text = "loading board…";
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.GetBountyBoardAsync(TargetPlayer);
            if (resp == null || resp.Ok == false)
            {
                StatusText.Text = resp?.Error ?? "bounty board load failed";
                return;
            }

            _formula = new BountyBoardFormula
            {
                PlayerCredits = resp.PlayerCredits,
                MaxLosses = resp.MaxLosses,
                EsPerTier = resp.BountyRewardEternitySplintersPerTier,
                CsPerTier = resp.BountyRewardCubeShardsPerTier,
                LmPerTier = resp.BountyRewardLegendaryMarksPerTier,
                GuaranteedBisTier = resp.BountyGuaranteedBisTier,
                // Icons never change within a session — carry forward.
                CreditsIcon = _formula.CreditsIcon,
                EsIcon = _formula.EsIcon,
                CsIcon = _formula.CsIcon,
                LmIcon = _formula.LmIcon,
            };
            CreditsBalanceText.Text = $"You have {resp.PlayerCredits:N0} credits";

            _cards.Clear();
            foreach (var slot in resp.Slots.OrderBy(s => s.SlotIndex))
                _cards.Add(new BountyBoardCard(slot, _formula));
            ShownCards.Clear();
            foreach (var c in _cards) ShownCards.Add(c);

            int active = _cards.Count(c => c.Resolved == false);
            int defeated = _cards.Count(c => c.Defeated);
            int fled = _cards.Count(c => c.Fled);
            StatusText.Text = $"{active} active  ·  {defeated} defeated  ·  {fled} fled";

            _ = RunPortraitSweepAsync();
            _ = RunCurrencyIconSweepAsync(resp.CurrencyIcons);
        }
        catch (Exception ex) { StatusText.Text = $"error: {ex.Message}"; }
        finally { RefreshBtn.IsEnabled = true; _refreshInFlight = false; }
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
            var tasks = new System.Collections.Generic.List<Task>();
            foreach (var card in _cards)
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
                                try { card.Portrait = new BitmapImage(new Uri(url)) { DecodePixelWidth = 144 }; }
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
                    return new BitmapImage(new Uri(url)) { DecodePixelWidth = 48 };
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
                foreach (var card in _cards) card.RaiseIconsChanged();
            });
        }
        catch { }
    }

    // Same button, two actions depending on card state — Post Bounty
    // (pay + warp) while active, Collect Rewards once Defeated. Never both
    // at once (ActionEnabled/CanCollect are mutually exclusive with
    // CanPost), so routing on card.CanCollect is unambiguous.
    private async void PostBounty_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BountyBoardCard card) return;
        var button = sender as Button;
        bool collecting = card.CanCollect;
        card.IsBusy = true;
        if (button != null) button.IsEnabled = false;
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = collecting
                ? await _api.PostBountyBoardCollectAsync(TargetPlayer, card.SlotIndex)
                : await _api.PostBountyBoardStartAsync(TargetPlayer, card.SlotIndex);
            card.CardStatus = resp?.Message ?? resp?.Error ?? "no response";
            // Credits were spent / reward was granted (or the attempt
            // failed) either way — refresh so the toolbar balance and
            // every card's state reflect the real result.
            await RefreshAsync();
        }
        catch (Exception ex) { card.CardStatus = $"error: {ex.Message}"; }
        finally
        {
            card.IsBusy = false;
            if (button != null) button.IsEnabled = card.ActionEnabled;
        }
    }
}
