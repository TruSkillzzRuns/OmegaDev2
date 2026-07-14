using System;
using System.IO;
using System.Text.Json;
using Microsoft.UI.Xaml;

namespace OmegaDev2.Services;

/// <summary>
/// User preferences persisted to <c>%LocalAppData%\OmegaDev2\prefs.json</c>.
/// Follows the same JSON-in-LocalAppData shape as AssetSetupService,
/// BookmarksService, and PresetService so every preference lives in one
/// discoverable folder.
/// </summary>
public static class PreferencesService
{
    private static readonly string PrefsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OmegaDev2", "prefs.json");

    /// <summary>
    /// UI theme override. <c>System</c> follows the Windows setting (WinUI 3
    /// default), <c>Light</c>/<c>Dark</c> pin the app regardless of the OS.
    /// </summary>
    public enum ThemeMode { System, Light, Dark }

    private sealed class PrefsBlob
    {
        public string Theme { get; set; } = nameof(ThemeMode.System);
    }

    private static PrefsBlob _blob = Load();

    public static ThemeMode Theme
    {
        get => Enum.TryParse<ThemeMode>(_blob.Theme, ignoreCase: true, out var m) ? m : ThemeMode.System;
        set
        {
            var newName = value.ToString();
            if (_blob.Theme == newName) return;
            _blob.Theme = newName;
            Save();
            ThemeChanged?.Invoke(value);
        }
    }

    /// <summary>Fires after <see cref="Theme"/> is set to a new value.</summary>
    public static event Action<ThemeMode>? ThemeChanged;

    /// <summary>
    /// Convert the stored preference into the WinUI 3 ElementTheme value used
    /// on the root FrameworkElement. <c>System</c> maps to <c>Default</c>,
    /// which delegates to the OS setting.
    /// </summary>
    public static ElementTheme ToElementTheme(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => ElementTheme.Light,
        ThemeMode.Dark  => ElementTheme.Dark,
        _               => ElementTheme.Default,
    };

    private static PrefsBlob Load()
    {
        try
        {
            if (File.Exists(PrefsPath) == false) return new PrefsBlob();
            var raw = File.ReadAllText(PrefsPath);
            return JsonSerializer.Deserialize<PrefsBlob>(raw) ?? new PrefsBlob();
        }
        catch
        {
            // Corrupt/unreadable prefs file — fall back to defaults rather
            // than crashing the app at startup.
            return new PrefsBlob();
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PrefsPath)!);
            File.WriteAllText(PrefsPath, JsonSerializer.Serialize(_blob, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // If we can't write prefs (locked file, no disk space) the app
            // still runs — the setting just doesn't persist across restarts.
        }
    }
}
