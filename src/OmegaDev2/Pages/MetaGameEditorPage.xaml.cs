using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmegaDev2.Services;

namespace OmegaDev2.Pages;

public sealed class ProtoDiscoverRow
{
    public string ProtoRef { get; }
    public string Name { get; }
    public string Path { get; }

    public ProtoDiscoverRow(ProtoEditorDiscoverEntry entry)
    {
        ProtoRef = entry.ProtoRef;
        Name = entry.Name;
        Path = entry.Path;
    }
}

public sealed class FieldRow
{
    public string FieldName { get; }
    public string TypeLabel { get; }
    public string ValueText { get; }
    public bool IsEditable { get; }
    public Microsoft.UI.Xaml.Visibility ReadOnlyBadgeVisibility
        => IsEditable ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    public FieldRow(FieldSnapshotDto dto)
    {
        FieldName = dto.FieldName;
        TypeLabel = dto.IsArray ? $"{dto.FieldTypeName}[]" : dto.FieldTypeName;
        IsEditable = dto.IsEditable;
        ValueText = FormatValue(dto.Value);
    }

    private static string FormatValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            return "[" + string.Join(", ", value.EnumerateArray().Select(e => e.ToString())) + "]";
        return value.ValueKind == JsonValueKind.Undefined ? "" : value.ToString();
    }
}

// Region Events — browse/edit real MetaGame/MetaState prototypes at runtime
// via RuntimePrototypeEditor. This is the heavier Phase B half of the
// Events tool: every write here is a global, live, unrevertable field
// mutation on an already-loaded prototype — see the page's warning banner.
// No new prototype is ever created; this repurposes an existing one.
public sealed partial class MetaGameEditorPage : Page
{
    private readonly ServerApiClient _api = new();
    public ObservableCollection<ProtoDiscoverRow> Results { get; } = new();
    public ObservableCollection<FieldRow> Fields { get; } = new();

    private string? _selectedProtoRef;

    public MetaGameEditorPage()
    {
        InitializeComponent();
        DiscoverList.ItemsSource = Results;
        FieldList.ItemsSource = Fields;
        BaseTypeCombo.SelectionChanged += (_, _) => _ = RunDiscoverAsync();
        _ = RunDiscoverAsync();
    }

    private string TargetPlayer => string.IsNullOrWhiteSpace(PlayerBox.Text) ? "*" : PlayerBox.Text.Trim();

    private async void Search_Click(object sender, RoutedEventArgs e) => await RunDiscoverAsync();

    // Runs on page load (empty query = every loaded prototype of the selected
    // base type, up to the server's default limit) and again whenever the
    // MetaGame/MetaState toggle changes or Search is clicked — the operator
    // shouldn't have to click Search just to see what's there.
    private async System.Threading.Tasks.Task RunDiscoverAsync()
    {
        string baseType = (BaseTypeCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "MetaGame";
        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.GetProtoEditorDiscoverAsync(baseType, SearchBox.Text);
            Results.Clear();
            if (resp == null || resp.Ok == false)
            {
                WriteStatusText.Text = resp?.Error ?? "search failed";
                return;
            }
            foreach (var entry in resp.Results)
                Results.Add(new ProtoDiscoverRow(entry));
            WriteStatusText.Text = $"{resp.Results.Count} result(s){(resp.Truncated ? " (truncated)" : "")}";
        }
        catch (Exception ex)
        {
            WriteStatusText.Text = $"error: {ex.Message}";
        }
    }

    private async void DiscoverList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DiscoverList.SelectedItem is not ProtoDiscoverRow row) return;
        _selectedProtoRef = row.ProtoRef;
        SelectedProtoText.Text = $"{row.Name}  ({row.ProtoRef})";

        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.GetProtoEditorFieldsAsync(row.ProtoRef);
            Fields.Clear();
            if (resp == null || resp.Ok == false)
            {
                WriteStatusText.Text = resp?.Error ?? "failed to read fields";
                return;
            }
            foreach (var f in resp.Fields)
                Fields.Add(new FieldRow(f));
        }
        catch (Exception ex)
        {
            WriteStatusText.Text = $"error: {ex.Message}";
        }
    }

    private async void Write_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedProtoRef))
        {
            WriteStatusText.Text = "select a prototype first";
            return;
        }
        string path = WritePathBox.Text?.Trim() ?? "";
        if (path.Length == 0) { WriteStatusText.Text = "enter a field path"; return; }

        string valueType = (WriteValueTypeCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "String";
        object value = ConvertInputValue(valueType, WriteValueBox.Text ?? "");

        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostProtoEditorWriteAsync(_selectedProtoRef, path, valueType, value);
            if (resp == null || resp.Ok == false)
            {
                WriteStatusText.Text = resp?.Error ?? "write failed";
                return;
            }
            WriteStatusText.Text = $"'{path}': {resp.PreviousValue} -> {resp.NewValue}";
            // Refresh so the field table reflects the mutation.
            var fieldsResp = await _api.GetProtoEditorFieldsAsync(_selectedProtoRef);
            if (fieldsResp?.Ok == true)
            {
                Fields.Clear();
                foreach (var f in fieldsResp.Fields)
                    Fields.Add(new FieldRow(f));
            }
        }
        catch (Exception ex)
        {
            WriteStatusText.Text = $"error: {ex.Message}";
        }
    }

    private void UseSelectedAsSource_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProtoRef != null) CloneSourceBox.Text = _selectedProtoRef;
    }

    private void UseSelectedAsTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProtoRef != null) CloneTargetBox.Text = _selectedProtoRef;
    }

    private async void Clone_Click(object sender, RoutedEventArgs e)
    {
        string source = CloneSourceBox.Text?.Trim() ?? "";
        string target = CloneTargetBox.Text?.Trim() ?? "";
        string path = ClonePathBox.Text?.Trim() ?? "";
        string? targetPath = string.IsNullOrWhiteSpace(CloneTargetPathBox.Text) ? null : CloneTargetPathBox.Text.Trim();

        if (source.Length == 0 || target.Length == 0 || path.Length == 0)
        {
            CloneStatusText.Text = "source, target, and path are all required";
            return;
        }

        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var resp = await _api.PostProtoEditorCloneAsync(source, target, path, targetPath);
            CloneStatusText.Text = resp == null
                ? "no response"
                : resp.Ok
                    ? $"cloned — previous value on target was: {resp.PreviousValue}"
                    : (resp.Error ?? "clone failed");
        }
        catch (Exception ex)
        {
            CloneStatusText.Text = $"error: {ex.Message}";
        }
    }

    private async void Warp_Click(object sender, RoutedEventArgs e)
    {
        string regionRef = WarpRegionBox.Text?.Trim() ?? "";
        if (regionRef.Length == 0) { WriteStatusText.Text = "enter a region protoRef to warp into"; return; }

        try
        {
            _api.BaseUrl = AppState.ServerUrl;
            var (status, body) = await _api.PostPlayerWarpAsync(TargetPlayer, null, regionRef, allowUnsafe: true);
            WriteStatusText.Text = $"warp HTTP {status}: {body}";
        }
        catch (Exception ex)
        {
            WriteStatusText.Text = $"error: {ex.Message}";
        }
    }

    private static object ConvertInputValue(string valueType, string raw) => valueType switch
    {
        "Float" => float.TryParse(raw, out float f) ? f : 0f,
        "Integer" => int.TryParse(raw, out int i) ? i : 0,
        "Boolean" => bool.TryParse(raw, out bool b) && b,
        "PrototypeId" or "PrototypeGuid" or "LocaleStringId" => ParseRefValue(raw),
        _ => raw,
    };

    private static ulong ParseRefValue(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) raw = raw[2..];
        return ulong.TryParse(raw, System.Globalization.NumberStyles.HexNumber, null, out ulong v) ? v : 0;
    }
}
