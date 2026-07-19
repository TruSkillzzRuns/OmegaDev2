using System;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmegaDev2.Services;

namespace OmegaDev2.Pages;

public sealed class LiveTuningEventRow
{
    public string Name { get; }
    public bool IsActive { get; }
    public Microsoft.UI.Xaml.Visibility ActiveBadgeVisibility
        => IsActive ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

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

    private async System.Threading.Tasks.Task RefreshAsync()
    {
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
                ? $"override active: '{resp.OverrideEventName}' forced on"
                : $"{resp.ActiveToday.Count} active today (calendar-driven, no override)";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"error: {ex.Message}";
        }
    }

    private async void RowActivate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string eventName || string.IsNullOrWhiteSpace(eventName))
            return;

        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostLiveTuningActivateAsync(eventName);
            StatusText.Text = resp?.Message ?? resp?.Error ?? "no response";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"error: {ex.Message}";
        }
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
