using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmegaDev2.Services;

namespace OmegaDev2.Pages;

public sealed partial class TeleportPadPage : Page
{
    private readonly ServerApiClient _api = new();
    private List<RegionRow> _all = new();
    private readonly ObservableCollection<RegionRow> _filtered = new();

    public TeleportPadPage()
    {
        InitializeComponent();
        RegionList.ItemsSource = _filtered;
        SeedCategories(new List<string>()); // empty until first load
        Loaded += async (_, _) => await LoadRegionsAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadRegionsAsync();

    private async Task LoadRegionsAsync()
    {
        try
        {
            _api.BaseUrl = ServerUrlBox.Text?.Trim() ?? "http://localhost:8080";
            SetStatus("Loading regions from server…", accent: false);
            RefreshButton.IsEnabled = false;

            var resp = await _api.GetJsonAsync<ListResponse>("/webapi/regions/list");
            if (resp?.Ok != true)
            {
                SetStatus($"Server returned ok=false. Is MHServerEmu running with the RegionsWebHandler? ({resp?.Error ?? "no response"})", accent: false);
                return;
            }

            _all = (resp.Regions ?? new List<RegionRow>()).OrderBy(r => r.DisplayName).ToList();
            SeedCategories(_all.Select(r => Categorize(r.ShortName)).Distinct().OrderBy(c => c).ToList());
            ApplyFilter();
            SetStatus($"Loaded {_all.Count} safe-warp regions.", accent: true);
        }
        catch (Exception ex)
        {
            SetStatus($"Load failed: {ex.Message}", accent: false);
        }
        finally { RefreshButton.IsEnabled = true; }
    }

    private void SeedCategories(List<string> found)
    {
        CategoryCombo.Items.Clear();
        CategoryCombo.Items.Add("All categories");
        foreach (var c in found) CategoryCombo.Items.Add(c);
        CategoryCombo.SelectedIndex = 0;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void CategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string q = (SearchBox.Text ?? "").Trim();
        string cat = (CategoryCombo.SelectedItem as string) ?? "All categories";

        _filtered.Clear();
        foreach (var r in _all)
        {
            if (cat != "All categories" && Categorize(r.ShortName) != cat) continue;
            if (!string.IsNullOrEmpty(q) &&
                !r.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !r.Path.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;
            _filtered.Add(r);
        }
        CountText.Text = $"{_filtered.Count} shown / {_all.Count} total";
    }

    private void RegionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var r = RegionList.SelectedItem as RegionRow;
        if (r == null)
        {
            SelectedName.Text = "—";
            SelectedPath.Text = "";
            TeleportButton.IsEnabled = false;
            return;
        }
        SelectedName.Text = r.DisplayName;
        SelectedPath.Text = r.Path;
        TeleportButton.IsEnabled = true;
    }

    private async void TeleportButton_Click(object sender, RoutedEventArgs e)
    {
        var r = RegionList.SelectedItem as RegionRow;
        if (r == null) return;
        TeleportButton.IsEnabled = false;
        LastActionText.Text = $"Sending teleport → {r.DisplayName}…";
        try
        {
            var resp = await _api.PostJsonAsync<TeleportResponse>("/webapi/regions/teleport", new { regionRef = r.Path });
            if (resp?.Ok == true)
                LastActionText.Text = $"✓ Queued teleport to {r.DisplayName}. Watch your client for the load screen.";
            else
                LastActionText.Text = $"✗ Server rejected: {resp?.Error ?? "unknown error"}";
        }
        catch (Exception ex)
        {
            LastActionText.Text = $"✗ Request failed: {ex.Message}";
        }
        finally { TeleportButton.IsEnabled = true; }
    }

    private void SetStatus(string msg, bool accent)
    {
        StatusText.Text = msg;
    }

    /// <summary>
    /// Rough bucketing by prototype short-name prefix so users can filter
    /// hundreds of regions down to the family they want.
    /// </summary>
    private static string Categorize(string shortName)
    {
        if (string.IsNullOrEmpty(shortName)) return "Other";
        if (shortName.StartsWith("CH", StringComparison.Ordinal))
        {
            int digits = 0;
            while (digits + 2 < shortName.Length && char.IsDigit(shortName[2 + digits])) digits++;
            if (digits >= 2)
            {
                string ch = shortName.Substring(2, 2);
                return $"Story · Chapter {int.Parse(ch)}";
            }
            return "Story · Chapters";
        }
        if (shortName.Contains("HUB", StringComparison.Ordinal))       return "Hub";
        if (shortName.Contains("Terminal", StringComparison.Ordinal))  return "Terminal";
        if (shortName.Contains("Raid", StringComparison.Ordinal))      return "Raid";
        if (shortName.Contains("PVP", StringComparison.OrdinalIgnoreCase) ||
            shortName.Contains("XDefense", StringComparison.OrdinalIgnoreCase)) return "PvP · X-Defense";
        if (shortName.Contains("DangerRoom", StringComparison.Ordinal)) return "Danger Room";
        if (shortName.StartsWith("NPE", StringComparison.Ordinal))      return "New Player Experience";
        if (shortName.StartsWith("Holo", StringComparison.Ordinal))     return "Holo-Sim";
        if (shortName.StartsWith("Tutorial", StringComparison.Ordinal) ||
            shortName.Contains("Tutorial", StringComparison.Ordinal))   return "Tutorial";
        return "Other";
    }

    // DTOs matching /webapi/regions/list response.
    private class ListResponse
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public int Count { get; set; }
        public List<RegionRow>? Regions { get; set; }
    }

    private class TeleportResponse
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
    }

    public class RegionRow
    {
        public ulong Id { get; set; }
        public string ShortName { get; set; } = "";
        public string Path { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }
}
