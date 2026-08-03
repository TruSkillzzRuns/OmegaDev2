using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OmegaDev2.Services;

namespace OmegaDev2.Pages;

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
    // server-side validation ("pick an active one").
    public bool CanBountyHunt => Defeated == false;

    private int _tier = 5;
    public int Tier
    {
        get => _tier;
        set { if (_tier == value) return; _tier = value; Raise(); Raise(nameof(TierLabel)); }
    }

    private static readonly string[] s_tierNames =
    {
        "Trivial", "Easy", "Easy", "Moderate", "Moderate",
        "Hard", "Hard", "Brutal", "Brutal", "Legendary"
    };
    public string TierLabel => $"TIER {Tier} — {s_tierNames[Math.Clamp(Tier, 1, 10) - 1]}";

    private string _cardStatus = "";
    public string CardStatus { get => _cardStatus; set { _cardStatus = value; Raise(); } }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { _isBusy = value; Raise(); } }

    public BountyCard(NemesisEntryDto e)
    {
        HeroRef = e.HeroRef ?? string.Empty;
        Defeated = e.Defeated;

        string niceHero = string.IsNullOrEmpty(e.HeroName)
            ? HeroRef
            : e.HeroName.Split('/').Last();
        string suffix = string.IsNullOrEmpty(e.Suffix) ? "" : " " + e.Suffix;
        int safeRank = Math.Clamp(e.Rank, 0, 5);
        string stars = safeRank > 0 ? new string('★', safeRank) : "";

        string baseTitle = string.IsNullOrEmpty(e.LastKillerName) ? niceHero : e.LastKillerName;
        Title = $"{stars} {baseTitle}{suffix}".Trim();

        string revenge = e.RevengeKills > 0 ? $"  ·  your revenge {e.RevengeKills}" : "";
        CardStatus = $"{niceHero}  ·  their kills {e.Kills}{revenge}";
        Detail = $"rank {safeRank}  ·  {(e.IsBoss ? "boss" : "nemesis")}";
    }
}

// Bounty Board — a standalone tool separate from the Enemy Phantoms
// roster panel it grew out of. Pick a nemesis, set a tier (1-10), post
// the bounty; the player warps to a random Trial-of-the-Impossible arena
// and the chosen nemesis ambushes them there.
public sealed partial class BountyBoardPage : Page
{
    private readonly ServerApiClient _api = new();
    private readonly System.Collections.Generic.List<BountyCard> _allCards = new();
    public ObservableCollection<BountyCard> ShownCards { get; } = new();

    public BountyBoardPage()
    {
        InitializeComponent();
        BountyGrid.ItemsSource = ShownCards;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = RefreshAsync();
    }

    private string TargetPlayer => string.IsNullOrWhiteSpace(PlayerBox.Text) ? "*" : PlayerBox.Text.Trim();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async System.Threading.Tasks.Task RefreshAsync()
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

            _allCards.Clear();
            foreach (var n in resp.Nemeses) _allCards.Add(new BountyCard(n));
            ApplyFilter();

            int active = _allCards.Count(c => c.CanBountyHunt);
            StatusText.Text = _allCards.Count == 0
                ? "no nemeses yet — die to an enemy phantom to earn a spot on the board"
                : $"{active} bounties postable  ·  {_allCards.Count} total";
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

    private void TierNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        // x:Bind already keeps BountyCard.Tier in sync (TwoWay); this just
        // exists so the box registers as a handled event in XAML — no
        // extra work needed since Tier's setter raises TierLabel itself.
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
        }
        catch (Exception ex) { card.CardStatus = $"error: {ex.Message}"; }
        finally
        {
            card.IsBusy = false;
            if (button != null) button.IsEnabled = card.CanBountyHunt;
        }
    }
}
