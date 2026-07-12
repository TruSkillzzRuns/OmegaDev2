using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OmegaDev2.Services;
using Windows.UI;

namespace OmegaDev2.Pages;

public sealed class LogLine
{
    public string Text { get; }
    public Brush LineBrush { get; }

    private static readonly Brush s_error = new SolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0x5C, 0x5C));
    private static readonly Brush s_warn = new SolidColorBrush(Color.FromArgb(0xFF, 0xE0, 0xB0, 0x4A));
    private static readonly Brush s_normal = new SolidColorBrush(Color.FromArgb(0xFF, 0xC8, 0xC8, 0xD2));

    public bool IsErrorOrWarn { get; }

    public LogLine(string text)
    {
        Text = text;
        if (text.Contains("[Error]", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("[Fatal]", StringComparison.OrdinalIgnoreCase))
        { LineBrush = s_error; IsErrorOrWarn = true; }
        else if (text.Contains("[Warn]", StringComparison.OrdinalIgnoreCase))
        { LineBrush = s_warn; IsErrorOrWarn = true; }
        else
        { LineBrush = s_normal; IsErrorOrWarn = false; }
    }
}

// Live Log Viewer — polls /webapi/logs/tail every 2s while "Following" is on.
// Filtering and error-only are applied client-side over the last fetch so
// they respond instantly without another server round-trip.
public sealed partial class LogViewerPage : Page
{
    private readonly ServerApiClient _api = new();
    private readonly DispatcherQueueTimer _timer;
    private List<LogLine> _lastFetch = new();
    public ObservableCollection<LogLine> ShownLines { get; } = new();

    private int _lines = 200;
    private bool _fetchInFlight;

    public LogViewerPage()
    {
        // Create the timer BEFORE InitializeComponent: the XAML sets
        // FollowToggle IsChecked="True" and a default ComboBox selection,
        // which raise Checked / SelectionChanged during parse — those
        // handlers touch _timer, so it must already exist.
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += async (_, _) => await FetchAsync();

        InitializeComponent();
        LogList.ItemsSource = ShownLines;

        _timer.Start();
        _ = FetchAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _timer.Stop();
        base.OnNavigatedFrom(e);
    }

    private async System.Threading.Tasks.Task FetchAsync()
    {
        if (_fetchInFlight) return;
        _fetchInFlight = true;
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.GetLogsTailAsync(_lines);
            if (resp == null || resp.Ok == false)
            {
                StatusText.Text = resp?.Error ?? "server unreachable";
                return;
            }

            _lastFetch = resp.Lines.Select(l => new LogLine(l)).ToList();
            StatusText.Text = $"{resp.File} · {resp.Count} lines";
            ApplyFilter();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _fetchInFlight = false;
        }
    }

    private void ApplyFilter()
    {
        string q = FilterBox.Text?.Trim() ?? "";
        bool errorsOnly = ErrorsOnlyCheck.IsChecked == true;

        IEnumerable<LogLine> filtered = _lastFetch;
        if (errorsOnly) filtered = filtered.Where(l => l.IsErrorOrWarn);
        if (q.Length > 0) filtered = filtered.Where(l => l.Text.Contains(q, StringComparison.OrdinalIgnoreCase));

        ShownLines.Clear();
        foreach (var line in filtered) ShownLines.Add(line);

        // Keep the tail in view while following.
        if (FollowToggle.IsChecked == true && ShownLines.Count > 0)
            LogList.ScrollIntoView(ShownLines[ShownLines.Count - 1]);
    }

    private void FollowToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (_timer == null || FollowToggle == null) return; // fired during XAML parse
        FollowToggle.Content = "Following";
        _timer.Start();
        _ = FetchAsync();
    }

    private void FollowToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_timer == null || FollowToggle == null) return;
        FollowToggle.Content = "Paused";
        _timer.Stop();
    }

    private async void RefreshNow_Click(object sender, RoutedEventArgs e) => await FetchAsync();

    private void LinesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LinesCombo?.SelectedItem is ComboBoxItem item && item.Content is string label)
        {
            string digits = new(label.TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(digits, out int n)) _lines = n;
            if (LogList != null) _ = FetchAsync(); // skip the parse-time firing
        }
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ErrorsOnly_Changed(object sender, RoutedEventArgs e) => ApplyFilter();
}
