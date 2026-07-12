using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace OmegaDev2.Services;

// Persistent user bookmark store. Saves to %LocalAppData%/OmegaDev/bookmarks.json.
// Kind is free-form ("proto", "entity", "region", "power", ...) so any tool can
// hand something back to be re-opened later. Thread-safe on the save/load path.
public sealed class BookmarksService
{
    public sealed class Entry
    {
        public string Kind { get; set; } = "";
        public string Value { get; set; } = "";     // ref / id / json blob
        public string Label { get; set; } = "";
        public List<string> Tags { get; set; } = new();
        public long AddedAtMs { get; set; }
    }

    private static readonly object _lock = new();
    private static BookmarksService _instance;
    public static BookmarksService Current => _instance ??= new BookmarksService();

    public List<Entry> Entries { get; private set; } = new();
    private static string StorePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmegaDev", "bookmarks.json");

    private BookmarksService() => Load();

    public void Add(string kind, string value, string label, IEnumerable<string> tags = null)
    {
        lock (_lock)
        {
            Entries.Add(new Entry
            {
                Kind = kind ?? "", Value = value ?? "", Label = label ?? value ?? "",
                Tags = tags is null ? new() : new List<string>(tags),
                AddedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            Save();
        }
    }

    public void Remove(Entry e) { lock (_lock) { Entries.Remove(e); Save(); } }
    public void Clear()         { lock (_lock) { Entries.Clear(); Save(); } }

    private void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            Entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(StorePath)) ?? new();
        }
        catch { Entries = new(); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(Entries, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }
}
