using System;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OmegaDev2.Services;

namespace OmegaDev2.Pages;

public sealed class LiveTuningEventRow
{
    private static Brush Resource(string key) => Application.Current.Resources[key] as Brush;

    public string Name { get; }
    public bool IsActive { get; }

    public string StatusLabel => IsActive ? "Active" : "Off";
    public Brush StatusDotBrush => Resource(IsActive ? "OmegaDev2.SuccessBrush" : "OmegaDev2.TextTertiaryBrush");
    public Brush StatusTextBrush => Resource(IsActive ? "OmegaDev2.SuccessBrush" : "OmegaDev2.TextSecondaryBrush");

    // Plain button content/state — deliberately NOT a two-way-bound ToggleSwitch. WinUI fires
    // Toggled on ANY IsOn change, including ones caused by container virtualization recycling a
    // ToggleSwitch into a different row during RefreshAsync()'s Events.Clear()/Add() cycle, which
    // previously caused a feedback loop (toggle -> API call -> refresh -> spurious re-toggle ->
    // API call -> ...). A Button's Click only fires on a real user gesture, so that loop can't happen.
    public string ToggleButtonLabel => IsActive ? "Turn Off" : "Turn On";
    public Brush ToggleButtonBrush => IsActive ? Resource("OmegaDev2.PanelSecondaryBrush") : Resource("OmegaDev2.AccentBrush");

    public LiveTuningEventRow(string name, bool isActive)
    {
        Name = name;
        IsActive = isActive;
    }
}

// Live Events — forces one of MHO's real live-tuning events (loot/XP
// multiplier holidays) on via LiveTuningEventOverrideWriter instead of
// waiting for the calendar. No reflection, no prototype mutation — this is
// the cheap Phase A half of the Events tool; MetaGame/MetaState field
// editing is a separate, heavier page.
public sealed partial class EventsLiveTuningPage : Page
{
    private readonly ServerApiClient _api = new();
    public ObservableCollection<LiveTuningEventRow> Events { get; } = new();

    public EventsLiveTuningPage()
    {
        InitializeComponent();
        EventList.ItemsSource = Events;
        _ = RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    // Reentrancy guard: RefreshAsync() and toggle clicks are both async and both call the API,
    // so without this a fast double-click (or a click landing mid-refresh) could overlap two
    // read-modify-write cycles against the same override file.
    private bool _busy;

    private async System.Threading.Tasks.Task RefreshAsync()
    {
        if (_busy)
            return;
        _busy = true;
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.GetLiveTuningEventsAsync();
            if (resp == null || resp.Ok == false)
            {
                StatusText.Text = resp?.Error ?? "server unreachable";
                return;
            }

            Events.Clear();
            foreach (var name in resp.KnownEvents)
                Events.Add(new LiveTuningEventRow(name, resp.ActiveToday.Contains(name)));

            StatusText.Text = resp.OverrideActive
                ? $"override active: {resp.ActiveToday.Count} event(s) forced on"
                : $"{resp.ActiveToday.Count} active today (calendar-driven, no override)";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"error: {ex.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    private async void RowToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        if ((sender as FrameworkElement)?.DataContext is not LiveTuningEventRow row)
            return;

        _busy = true;
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = row.IsActive
                ? await _api.PostLiveTuningDeactivateAsync(row.Name)
                : await _api.PostLiveTuningActivateAsync(row.Name);
            StatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"error: {ex.Message}";
        }
        finally
        {
            _busy = false;
        }

        await RefreshAsync();
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostLiveTuningClearAsync();
            StatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"error: {ex.Message}";
        }
    }
}
