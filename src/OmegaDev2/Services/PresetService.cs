using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace OmegaDev2.Services;

// User-defined bundles. A preset holds an arbitrary payload dict that any tool
// can read/write on activation. Common uses: "level-60 warm-start", "farming
// midtown loadout", "test wave-survival config". Stored per-user under
// %LocalAppData%/OmegaDev/presets.json.
public sealed class PresetService
{
    public sealed class Preset
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public Dictionary<string, string> Payload { get; set; } = new();
        public long CreatedAtMs { get; set; }
    }

    private static readonly object _lock = new();
    private static PresetService _instance;
    public static PresetService Current => _instance ??= new PresetService();
    public List<Preset> Presets { get; private set; } = new();
    private static string StorePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmegaDev", "presets.json");

    private PresetService() => Load();

    public void SaveNamed(string name, string description, Dictionary<string, string> payload)
    {
        lock (_lock)
        {
            Presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            Presets.Add(new Preset { Name = name, Description = description ?? "", Payload = new(payload ?? new()), CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            Save();
        }
    }

    public Preset Get(string name)
    {
        lock (_lock) return Presets.Find(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public void Delete(string name) { lock (_lock) { Presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)); Save(); } }

    private void Load()
    {
        try { if (File.Exists(StorePath)) Presets = JsonSerializer.Deserialize<List<Preset>>(File.ReadAllText(StorePath)) ?? new(); }
        catch { Presets = new(); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(Presets, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
