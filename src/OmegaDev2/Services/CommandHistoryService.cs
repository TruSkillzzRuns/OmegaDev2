using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace OmegaDev2.Services;

// In-memory ring buffer of user actions across all tools. Small (1024) so it
// stays fully in memory. Exportable to JSON for repro / bug reports. Feed via
// CommandHistoryService.Current.Record(...) from any tool that wants to record.
public sealed class CommandHistoryService
{
    public sealed class Entry
    {
        public long TimestampMs { get; set; }
        public string Tool { get; set; } = "";      // e.g. "BossAlly", "LootFilter"
        public string Action { get; set; } = "";    // e.g. "summon", "rule/add"
        public object Data { get; set; }
    }

    private const int Capacity = 1024;
    private static readonly object _lock = new();
    private static CommandHistoryService _instance;
    public static CommandHistoryService Current => _instance ??= new CommandHistoryService();
    private readonly LinkedList<Entry> _buf = new();

    public IReadOnlyCollection<Entry> Snapshot() { lock (_lock) return new List<Entry>(_buf); }

    public void Record(string tool, string action, object data = null)
    {
        var e = new Entry { TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Tool = tool ?? "", Action = action ?? "", Data = data };
        lock (_lock) { _buf.AddFirst(e); while (_buf.Count > Capacity) _buf.RemoveLast(); }
    }

    public void Clear() { lock (_lock) _buf.Clear(); }

    public string ExportJson()
    {
        lock (_lock) return JsonSerializer.Serialize(_buf, new JsonSerializerOptions { WriteIndented = true });
    }

    public void ExportTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ExportJson());
    }
}
