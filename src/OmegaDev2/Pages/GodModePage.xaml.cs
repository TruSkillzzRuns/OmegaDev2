using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using OmegaDev2.Services;

namespace OmegaDev2.Pages;

// God Mode — runtime player buff cabinet. Ported from the standalone
// OmegaDev tool's GodTier page onto OmegaDev2's ServerApiClient/AppState
// conventions (target-player textbox instead of settings-bound base URL).
//
// Sends partial deltas via GodModeFlags bits so a slider drag doesn't
// clobber other toggles. The server's snapshot response is the source of
// truth — the UI always resyncs to it after every apply.
//
// _suppressEvents guards programmatic updates so syncing from a snapshot
// doesn't ping-pong back to the server.
public sealed partial class GodModePage : Page
{
    private readonly ServerApiClient _api = new();
    private bool _suppressEvents;
    private const float MasterDamage = 100.0f;
    private const float MasterSpeed = 3.0f;

    public GodModePage()
    {
        InitializeComponent();

        // Slider Min/Max/Value must be set in code-behind — same
        // parser-order quirk other sliders in this app already work around.
        _suppressEvents = true;

        DamageSlider.Maximum = 1000;
        DamageSlider.Minimum = 1;
        DamageSlider.Value = 1;
        DamageValueText.Text = "1.0x";

        SpeedSlider.Maximum = 20.0;
        SpeedSlider.Minimum = 0.1;
        SpeedSlider.Value = 1.0;
        SpeedValueText.Text = "1.0x";

        _suppressEvents = false;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => base.OnNavigatedTo(e);

    private string TargetPlayer => string.IsNullOrWhiteSpace(PlayerBox.Text) ? "*" : PlayerBox.Text.Trim();

    // ---- master GOD MODE --------------------------------------------------

    private async void MasterToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        bool on = MasterToggle.IsOn;

        _suppressEvents = true;
        InvulnToggle.IsOn = on;
        SpiritToggle.IsOn = on;
        CooldownToggle.IsOn = on;
        DamageSlider.Value = on ? MasterDamage : 0;
        SpeedSlider.Value = on ? MasterSpeed : 1.0;
        DamageValueText.Text = FormatMult((float)DamageSlider.Value);
        SpeedValueText.Text = FormatMult((float)SpeedSlider.Value);
        _suppressEvents = false;

        await PushAsync(ServerApiClient.GodModeFlags.All,
            invuln: on, spirit: on, noCd: on,
            damage: (float)DamageSlider.Value, speed: (float)SpeedSlider.Value);
    }

    // ---- individual toggles ------------------------------------------------

    private async void IndividualToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;

        ServerApiClient.GodModeFlags flag =
            ReferenceEquals(sender, InvulnToggle) ? ServerApiClient.GodModeFlags.Invulnerable
            : ReferenceEquals(sender, SpiritToggle) ? ServerApiClient.GodModeFlags.NoEnduranceCosts
            : ReferenceEquals(sender, CooldownToggle) ? ServerApiClient.GodModeFlags.NoCooldowns
            : ServerApiClient.GodModeFlags.None;
        if (flag == ServerApiClient.GodModeFlags.None) return;
        bool on = ((ToggleSwitch)sender).IsOn;

        await PushAsync(flag,
            invuln: flag == ServerApiClient.GodModeFlags.Invulnerable && on,
            spirit: flag == ServerApiClient.GodModeFlags.NoEnduranceCosts && on,
            noCd: flag == ServerApiClient.GodModeFlags.NoCooldowns && on,
            damage: 0, speed: 1.0f);
    }

    // ---- sliders ------------------------------------------------------------

    private async void DamageSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressEvents) return;
        DamageValueText.Text = FormatMult((float)e.NewValue);
        await PushAsync(ServerApiClient.GodModeFlags.DamageMult,
            invuln: false, spirit: false, noCd: false, damage: (float)e.NewValue, speed: 1.0f);
    }

    private async void SpeedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressEvents) return;
        SpeedValueText.Text = FormatMult((float)e.NewValue);
        await PushAsync(ServerApiClient.GodModeFlags.SpeedMult,
            invuln: false, spirit: false, noCd: false, damage: 0, speed: (float)e.NewValue);
    }

    // ---- reset ---------------------------------------------------------------

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        _suppressEvents = true;
        MasterToggle.IsOn = false;
        InvulnToggle.IsOn = false;
        SpiritToggle.IsOn = false;
        CooldownToggle.IsOn = false;
        DamageSlider.Value = 0;
        SpeedSlider.Value = 1.0;
        DamageValueText.Text = "1.0x";
        SpeedValueText.Text = "1.0x";
        _suppressEvents = false;

        await PushAsync(ServerApiClient.GodModeFlags.All,
            invuln: false, spirit: false, noCd: false, damage: 0, speed: 1.0f);
    }

    // ---- network ---------------------------------------------------------------

    private async Task PushAsync(ServerApiClient.GodModeFlags flags, bool invuln, bool spirit, bool noCd, float damage, float speed)
    {
        try
        {
            StatusText.Text = $"applying ({flags})…";
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostGodModeAsync(TargetPlayer, flags, invuln, spirit, noCd, damage, speed);
            HandleResponse(resp);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"error: {ex.Message}";
        }
    }

    private void HandleResponse(GodModeResponse? resp)
    {
        if (resp == null) { StatusText.Text = "no response"; return; }

        if (resp.Snapshot != null)
        {
            var s = resp.Snapshot;
            _suppressEvents = true;
            InvulnToggle.IsOn = s.Invulnerable;
            SpiritToggle.IsOn = s.NoEnduranceCosts;
            CooldownToggle.IsOn = s.NoCooldowns;
            DamageSlider.Value = s.DamageMult;
            DamageValueText.Text = FormatMult(s.DamageMult);
            SpeedSlider.Value = s.SpeedMult;
            SpeedValueText.Text = FormatMult(s.SpeedMult);
            MasterToggle.IsOn = InvulnToggle.IsOn && SpiritToggle.IsOn && CooldownToggle.IsOn
                && DamageSlider.Value >= MasterDamage && SpeedSlider.Value >= MasterSpeed;
            _suppressEvents = false;
        }

        StatusText.Text = resp.Ok ? $"OK  {resp.Message}" : $"error: {resp.Error ?? resp.Message ?? "unknown"}";
    }

    private static string FormatMult(float v) => v >= 10 ? $"{v:F0}x" : $"{v:F1}x";
}
