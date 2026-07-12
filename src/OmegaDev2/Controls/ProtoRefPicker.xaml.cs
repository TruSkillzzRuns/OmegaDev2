using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmegaDev2.Services;

namespace OmegaDev2.Controls;

/// <summary>
/// Reusable prototype-reference picker. Set <see cref="CatalogClass"/> in XAML (e.g.
/// "PowerPrototype"), and the control fetches /webapi/protocatalog?class=X on first
/// focus, caches by class name, and exposes an AutoSuggestBox over the entries.
///
/// - Type-to-filter across Leaf, ProtoPath, and Ref (path OR hex ref both match)
/// - <see cref="SelectedRef"/> is the 0x-hex string of the chosen entry, or "" if nothing chosen
/// - <see cref="Text"/> mirrors the AutoSuggestBox text for backwards compat with code
///   that used to read a TextBox.Text
///
/// Zero manual typing beyond the filter keystrokes — dropdown drives the selection.
/// </summary>
public sealed partial class ProtoRefPicker : UserControl
{
    // Cache keyed by catalog class name so multiple ProtoRefPickers of the same class
    // (e.g. two "PowerPrototype" pickers on one page) share one server round-trip.
    private static readonly ConcurrentDictionary<string, Task<List<ProtoCatalogEntry>>> s_cache = new();

    public static readonly DependencyProperty CatalogClassProperty =
        DependencyProperty.Register(nameof(CatalogClass), typeof(string), typeof(ProtoRefPicker),
            new PropertyMetadata(""));

    public static readonly DependencyProperty PlaceholderTextProperty =
        DependencyProperty.Register(nameof(PlaceholderText), typeof(string), typeof(ProtoRefPicker),
            new PropertyMetadata("Start typing to search…", OnPlaceholderChanged));

    public static readonly DependencyProperty SelectedRefProperty =
        DependencyProperty.Register(nameof(SelectedRef), typeof(string), typeof(ProtoRefPicker),
            new PropertyMetadata(""));

    // TextProperty is a real DP (not a passthrough get/set) so {Binding Text, Mode=TwoWay} works
    // in DataTemplate scenarios like LootStudio's rule editor.
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(ProtoRefPicker),
            new PropertyMetadata("", OnTextChanged));

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ProtoRefPicker p && p.Box != null)
        {
            string newText = (string)e.NewValue;
            if (p.Box.Text != newText) p.Box.Text = newText;
        }
    }

    public string CatalogClass
    {
        get => (string)GetValue(CatalogClassProperty);
        set => SetValue(CatalogClassProperty, value);
    }

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    /// <summary>The 0x-hex prototype reference of the selected entry. "" if nothing chosen.</summary>
    public string SelectedRef
    {
        get => (string)GetValue(SelectedRefProperty);
        set => SetValue(SelectedRefProperty, value);
    }

    /// <summary>Two-way bindable mirror of the box text. Backwards compat with pages that read TextBox.Text.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public event EventHandler<ProtoCatalogEntry>? SelectionChanged;

    private List<ProtoCatalogEntry>? _entries;
    private bool _loadInFlight;

    public ProtoRefPicker()
    {
        InitializeComponent();
        Box.PlaceholderText = PlaceholderText;
    }

    private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ProtoRefPicker p && p.Box != null)
            p.Box.PlaceholderText = (string)e.NewValue;
    }

    private async void Box_GotFocus(object sender, RoutedEventArgs e)
    {
        if (_entries != null || _loadInFlight) return;
        if (string.IsNullOrWhiteSpace(CatalogClass))
        {
            Box.PlaceholderText = "(ProtoRefPicker: CatalogClass not set)";
            return;
        }
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loadInFlight = true;
        try
        {
            var task = s_cache.GetOrAdd(CatalogClass, FetchFreshAsync);
            _entries = await task.ConfigureAwait(true);
        }
        catch
        {
            _entries = new List<ProtoCatalogEntry>();
        }
        finally
        {
            _loadInFlight = false;
        }
    }

    private static async Task<List<ProtoCatalogEntry>> FetchFreshAsync(string className)
    {
        var s = SettingsService.Current;
        if (string.IsNullOrWhiteSpace(s.ServerBaseUrl)) return new List<ProtoCatalogEntry>();

        using var c = new ServerApiClient(s.ServerBaseUrl, s.BearerToken, TimeSpan.FromSeconds(30));
        var (status, body) = await c.GetProtoCatalogAsync(className).ConfigureAwait(false);
        if (status != 200 || string.IsNullOrEmpty(body)) return new List<ProtoCatalogEntry>();
        return ProtoCatalogEntry.Parse(body);
    }

    private void Box_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // Sync typed input back to the Text DP so two-way bindings see the user's edits.
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && Text != sender.Text)
            SetValue(TextProperty, sender.Text ?? "");

        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        if (_entries == null) return;

        string q = sender.Text?.Trim() ?? "";
        if (q.Length == 0) { sender.ItemsSource = null; return; }

        // Match against Leaf, ProtoPath, or Ref — user can paste a hex ref or type a name.
        sender.ItemsSource = _entries
            .Where(e => e.Leaf.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || e.ProtoPath.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || e.Ref.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(50)
            .ToList();
    }

    private void Box_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not ProtoCatalogEntry entry) return;
        SelectedRef = entry.Ref;
        // Update Text via the DP so any two-way binding gets notified.
        SetValue(TextProperty, entry.Ref);
        SelectionChanged?.Invoke(this, entry);
    }
}
