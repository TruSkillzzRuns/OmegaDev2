using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace OmegaDev2.Pages;

public sealed class IniKeyRow
{
    public string Key { get; }
    public string Value { get; set; }
    public int LineIndex { get; }

    public IniKeyRow(string key, string value, int lineIndex)
    {
        Key = key;
        Value = value;
        LineIndex = lineIndex;
    }
}

public sealed class IniSection
{
    public string Name { get; }
    public ObservableCollection<IniKeyRow> Rows { get; } = new();

    public IniSection(string name) => Name = name;
}

// One editable panel — Server Config.ini or the game client's engine
// config. Both are personal, per-machine files with no fixed path (the
// server's Config.ini is explicitly never assumed/shipped; the client's
// engine ini lives wherever the user installed the game), so this always
// starts from a file picker rather than guessing a path.
//
// Editing is line-preserving: only the value half of a recognized
// "key=value" line is ever touched. Everything else — comments, blank
// lines, section headers, whitespace, key ordering — passes through
// byte-for-byte. A single ".bak" backup of the pre-edit file is written
// on first save per load, so a bad edit has a one-step way back.
public sealed class ConfigEditorPanel
{
    private List<string> _rawLines = new();
    private string? _bakPath;
    private bool _backedUpThisLoad;

    public string? FilePath { get; private set; }
    public ObservableCollection<IniSection> Sections { get; } = new();

    public async Task<string> LoadAsync(string path)
    {
        string text = await File.ReadAllTextAsync(path);
        var (lines, sections) = ParseIni(text);
        _rawLines = lines;
        FilePath = path;
        _bakPath = path + ".bak";
        _backedUpThisLoad = false;

        Sections.Clear();
        foreach (var s in sections) Sections.Add(s);

        int keyCount = sections.Sum(s => s.Rows.Count);
        return $"loaded {sections.Count} section(s), {keyCount} setting(s)";
    }

    public async Task<string> SaveAsync()
    {
        if (FilePath == null) return "nothing loaded";

        try
        {
            if (_backedUpThisLoad == false)
            {
                File.Copy(FilePath, _bakPath!, overwrite: true);
                _backedUpThisLoad = true;
            }

            var lines = new List<string>(_rawLines);
            foreach (var section in Sections)
            {
                foreach (var row in section.Rows)
                {
                    string original = lines[row.LineIndex];
                    int leadingWs = original.Length - original.TrimStart().Length;
                    string indent = original[..leadingWs];
                    lines[row.LineIndex] = $"{indent}{row.Key}={row.Value}";
                }
            }

            await File.WriteAllTextAsync(FilePath, string.Join(Environment.NewLine, lines));
            return $"saved — backup at {Path.GetFileName(_bakPath)}";
        }
        catch (Exception ex)
        {
            return $"save failed: {ex.Message}";
        }
    }

    private static (List<string> lines, List<IniSection> sections) ParseIni(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').ToList();
        var sections = new List<IniSection>();
        IniSection? current = null;

        for (int i = 0; i < lines.Count; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith(";") || trimmed.StartsWith("#")) continue;

            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
            {
                current = new IniSection(trimmed[1..^1]);
                sections.Add(current);
                continue;
            }

            int eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;

            string key = trimmed[..eq].Trim();
            string value = trimmed[(eq + 1)..].Trim();

            current ??= AddGeneralSection(sections);
            current.Rows.Add(new IniKeyRow(key, value, i));
        }

        return (lines, sections);
    }

    private static IniSection AddGeneralSection(List<IniSection> sections)
    {
        var s = new IniSection("(General)");
        sections.Add(s);
        return s;
    }
}

public sealed partial class ConfigEditorPage : Page
{
    private sealed class RememberedPaths
    {
        public string? ServerConfigPath { get; set; }
        public string? ClientConfigPath { get; set; }
    }

    private static string SettingsFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmegaDev2", "configeditor.json");

    public ConfigEditorPanel ServerPanel { get; } = new();
    public ConfigEditorPanel ClientPanel { get; } = new();

    public ConfigEditorPage()
    {
        InitializeComponent();
        ServerSections.ItemsSource = ServerPanel.Sections;
        ClientSections.ItemsSource = ClientPanel.Sections;
        _ = TryLoadRememberedAsync();
    }

    private async Task TryLoadRememberedAsync()
    {
        var remembered = LoadRememberedPaths();
        if (remembered.ServerConfigPath != null && File.Exists(remembered.ServerConfigPath))
        {
            ServerPathText.Text = remembered.ServerConfigPath;
            ServerStatusText.Text = await ServerPanel.LoadAsync(remembered.ServerConfigPath);
        }
        if (remembered.ClientConfigPath != null && File.Exists(remembered.ClientConfigPath))
        {
            ClientPathText.Text = remembered.ClientConfigPath;
            ClientStatusText.Text = await ClientPanel.LoadAsync(remembered.ClientConfigPath);
        }
    }

    private static RememberedPaths LoadRememberedPaths()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
                return JsonSerializer.Deserialize<RememberedPaths>(File.ReadAllText(SettingsFilePath)) ?? new();
        }
        catch { }
        return new();
    }

    private static void SaveRememberedPaths(RememberedPaths paths)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);
            File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(paths, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static async Task<string?> PickIniFileAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".ini");
        picker.FileTypeFilter.Add(".cfg");
        picker.FileTypeFilter.Add("*");
        // Unpackaged WinUI 3: the picker needs the window handle explicitly.
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private async void LoadServerConfig_Click(object sender, RoutedEventArgs e)
    {
        string? path = await PickIniFileAsync();
        if (path == null) return;

        ServerStatusText.Text = await ServerPanel.LoadAsync(path);
        ServerPathText.Text = path;

        var remembered = LoadRememberedPaths();
        remembered.ServerConfigPath = path;
        SaveRememberedPaths(remembered);
    }

    private async void SaveServerConfig_Click(object sender, RoutedEventArgs e)
    {
        ServerStatusText.Text = await ServerPanel.SaveAsync();
    }

    private async void LoadClientConfig_Click(object sender, RoutedEventArgs e)
    {
        string? path = await PickIniFileAsync();
        if (path == null) return;

        ClientStatusText.Text = await ClientPanel.LoadAsync(path);
        ClientPathText.Text = path;

        var remembered = LoadRememberedPaths();
        remembered.ClientConfigPath = path;
        SaveRememberedPaths(remembered);
    }

    private async void SaveClientConfig_Click(object sender, RoutedEventArgs e)
    {
        ClientStatusText.Text = await ClientPanel.SaveAsync();
    }
}
