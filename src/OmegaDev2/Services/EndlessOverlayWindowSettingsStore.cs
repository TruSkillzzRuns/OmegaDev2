using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OmegaDev2.Services;

/// <summary>Position/size persisted across sessions for the Endless Wave overlay window.</summary>
public sealed class EndlessOverlayWindowSettings
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 260;
    public int Height { get; set; } = 220;
}

/// <summary>
/// Persists <see cref="EndlessOverlayWindowSettings"/> to
/// <c>%LocalAppData%\OmegaDev2\endless-overlay-window.json</c> — same
/// JSON-in-LocalAppData shape PreferencesService/BookmarksService/
/// PresetService already use.
/// </summary>
public sealed class EndlessOverlayWindowSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmegaDev2", "endless-overlay-window.json");

    public EndlessOverlayWindowSettings? LoadSync()
    {
        if (!File.Exists(SettingsPath)) return null;
        try
        {
            string json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<EndlessOverlayWindowSettings>(json, JsonOptions);
        }
        catch { return null; }
    }

    public async Task SaveAsync(EndlessOverlayWindowSettings settings, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using FileStream stream = File.Create(SettingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
    }
}
