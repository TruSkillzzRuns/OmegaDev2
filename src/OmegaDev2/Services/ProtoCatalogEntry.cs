using System.Collections.Generic;
using System.Text.Json;

namespace OmegaDev2.Services;

// Client model for /webapi/protocatalog responses. Drives every Forge / Builder
// dropdown that lets the user pick a prototype.
public sealed class ProtoCatalogEntry
{
    public string Ref { get; set; } = "";       // 0x-hex
    public string ProtoPath { get; set; } = ""; // full path
    public string Leaf { get; set; } = "";      // last segment, no .prototype
    // TR48-D: server-resolved IconPath asset name. Empty for CellPrototype,
    // populated for WorldEntityPrototype (and subclasses Agent/Spawner/
    // Transition). Region Builder catalog rail uses this as the path to
    // /webapi/portrait so each non-cell card has a real portrait thumbnail.
    public string IconPath { get; set; } = "";
    // TR48-O: server-resolved mesh name via ClassMeshLookup. Empty for
    // cells; populated for entities whose UnrealClass maps to a skeletal
    // or static mesh in classMeshIndex.json. JS uses /webapi/meshbyname
    // to fetch the .mhmp v6 bytes and renders the real mesh in viewport.
    public string MeshName { get; set; } = "";

    // Display line shown in AutoSuggestBox dropdown: leaf + parent dir hint.
    public string DisplayLine => string.IsNullOrEmpty(ParentDir)
        ? Leaf
        : $"{Leaf}   ·   {ParentDir}";

    public string ParentDir
    {
        get
        {
            if (string.IsNullOrEmpty(ProtoPath)) return "";
            int slash = ProtoPath.LastIndexOf('/');
            return slash > 0 ? ProtoPath.Substring(0, slash) : "";
        }
    }

    public static List<ProtoCatalogEntry> Parse(string json)
    {
        var list = new List<ProtoCatalogEntry>(2048);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("entries", out var arr)) return list;
            foreach (var e in arr.EnumerateArray())
                list.Add(new ProtoCatalogEntry
                {
                    Ref = e.TryGetProperty("ref", out var r) ? (r.GetString() ?? "") : "",
                    ProtoPath = e.TryGetProperty("protoPath", out var p) ? (p.GetString() ?? "") : "",
                    Leaf = e.TryGetProperty("leaf", out var lf) ? (lf.GetString() ?? "") : "",
                    IconPath = e.TryGetProperty("iconPath", out var ip) && ip.ValueKind == JsonValueKind.String ? (ip.GetString() ?? "") : "",
                    MeshName = e.TryGetProperty("meshName", out var mn) && mn.ValueKind == JsonValueKind.String ? (mn.GetString() ?? "") : "",
                });
        }
        catch { }
        return list;
    }
}
