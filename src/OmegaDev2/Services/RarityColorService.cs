using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;

namespace OmegaDev2.Services;

// Session-cached rarity → hex-color map, fetched from
// /webapi/lootstudio/rarities. Every loot-filter UI element that shades by
// rarity resolves here, so the app never hardcodes rarity colors. Handles
// both the built-in rarities (R1Common..R7Omega) and custom rarities added
// by the user (e.g. RCustom_LootStudioRed).
public static class RarityColorService
{
    // Session cache: leaf/short-name (case-insensitive) -> resolved color.
    // Also indexed by simplified short name ("Common", "Uncommon", "Cosmic")
    // so UI code can look up without knowing whether the game data uses
    // "R1Common", "Common", or "R6Cosmic".
    private static readonly SemaphoreSlim s_loadGate = new(1, 1);
    private static Dictionary<string, RarityEntry> s_byKey =
        new(StringComparer.OrdinalIgnoreCase);
    private static bool s_loaded;

    // Sensible fallbacks when the server hasn't been queried yet OR when a
    // rule references a rarity we don't have color data for. Grey ≈ "unknown"
    // so a broken lookup is visually obvious.
    private static readonly Color s_fallback = Color.FromArgb(0xFF, 0x9B, 0x9B, 0x9B);

    public sealed class RarityEntry
    {
        public string Leaf { get; init; } = "";
        public string ShortName { get; init; } = "";
        public int Tier { get; init; }
        public string ColorHex { get; init; } = "";
        public Color Color { get; init; }
    }

    /// <summary>Returns the parsed color for a rarity short/leaf name. Falls back to grey.</summary>
    public static Color GetColor(string rarityName)
    {
        if (string.IsNullOrEmpty(rarityName)) return s_fallback;
        return s_byKey.TryGetValue(rarityName, out var e) ? e.Color : s_fallback;
    }

    public static bool TryGet(string rarityName, out RarityEntry entry)
    {
        if (string.IsNullOrEmpty(rarityName)) { entry = null; return false; }
        return s_byKey.TryGetValue(rarityName, out entry);
    }

    public static IEnumerable<RarityEntry> All() => s_byKey.Values;

    /// <summary>Idempotent load from the server. Safe to call every page-navigation.</summary>
    public static async Task EnsureLoadedAsync(bool force = false)
    {
        if (s_loaded && !force) return;
        await s_loadGate.WaitAsync();
        try
        {
            if (s_loaded && !force) return;
            var s = SettingsService.Current;
            if (s == null || string.IsNullOrEmpty(s.ServerBaseUrl)) return;
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            if (!string.IsNullOrEmpty(s.BearerToken))
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", s.BearerToken);
            string body = await http.GetStringAsync($"{s.ServerBaseUrl.TrimEnd('/')}/webapi/lootstudio/rarities");
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("rarities", out var arr)) return;

            var map = new Dictionary<string, RarityEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in arr.EnumerateArray())
            {
                string leaf = e.TryGetProperty("leaf", out var l) ? l.GetString() ?? "" : "";
                int tier   = e.TryGetProperty("tier", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
                string hex = e.TryGetProperty("colorHex", out var c) ? c.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(leaf)) continue;

                Color color = ParseHex(hex, s_fallback);
                string shortName = LeafToShortName(leaf);

                var entry = new RarityEntry { Leaf = leaf, ShortName = shortName, Tier = tier, ColorHex = hex, Color = color };
                map[leaf] = entry;
                if (!string.IsNullOrEmpty(shortName)) map[shortName] = entry;
            }
            s_byKey = map;
            s_loaded = true;
        }
        catch { /* keep whatever was cached; next Ensure retries */ }
        finally { s_loadGate.Release(); }
    }

    private static Color ParseHex(string hex, Color fallback)
    {
        if (string.IsNullOrEmpty(hex)) return fallback;
        hex = hex.Trim();
        if (hex.StartsWith("#")) hex = hex.Substring(1);
        if (hex.StartsWith("0x") || hex.StartsWith("0X")) hex = hex.Substring(2);
        try
        {
            if (hex.Length == 6)
            {
                byte r = byte.Parse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber);
                byte g = byte.Parse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber);
                byte b = byte.Parse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber);
                return Color.FromArgb(0xFF, r, g, b);
            }
            if (hex.Length == 8)
            {
                byte a = byte.Parse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber);
                byte r = byte.Parse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber);
                byte g = byte.Parse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber);
                byte b = byte.Parse(hex.AsSpan(6, 2), System.Globalization.NumberStyles.HexNumber);
                return Color.FromArgb(a, r, g, b);
            }
        }
        catch { }
        return fallback;
    }

    // "R1Common" -> "Common", "R6Cosmic" -> "Cosmic", "RCustom_LootStudioRed"
    // -> "LootStudioRed" (strip R#- / RCustom_ prefixes). The rules editor
    // stores rarity chip names as short-name form.
    private static string LeafToShortName(string leaf)
    {
        if (string.IsNullOrEmpty(leaf)) return "";
        // R1..R9 prefix
        int i = 0;
        if (leaf.Length > 2 && leaf[0] == 'R' && char.IsDigit(leaf[1]))
        {
            i = 2;
            // Strip a Custom_ marker too — no data has R#Custom_ but be safe
        }
        else if (leaf.StartsWith("RCustom_", StringComparison.OrdinalIgnoreCase))
            i = "RCustom_".Length;
        else if (leaf.StartsWith("R", StringComparison.OrdinalIgnoreCase)
                 && leaf.Length > 1 && char.IsDigit(leaf[1]))
            i = 1;
        return i > 0 && i < leaf.Length ? leaf.Substring(i) : leaf;
    }

    /// <summary>Force-clears the cache — useful after a server restart added a new rarity.</summary>
    public static void Invalidate() { s_byKey.Clear(); s_loaded = false; }
}
