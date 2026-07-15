using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmegaDev2.Services;

/// <summary>
/// Encode / decode Wave Director Share Codes — a compact, portable
/// representation of a full wave plan (waves + run settings). The payload
/// is proto-ref IDs and flags only (no images, sounds, or copyrighted
/// client data), so a code on its own is useless without your own 1.52
/// install + phantom-heroes fork. gzipped JSON, base64url, "OD2W1-" prefix
/// so it's recognizable. Same shape as SquadCode.cs.
/// </summary>
public static class WaveCode
{
    public const string Prefix = "OD2W1-";

    public sealed class Payload
    {
        [JsonPropertyName("v")]  public int Version { get; set; } = 1;
        [JsonPropertyName("n")]  public string? Name { get; set; }
        [JsonPropertyName("im")] public int IntermissionMs { get; set; } = 5000;
        [JsonPropertyName("ar")] public string? ArenaRegionRef { get; set; }
        [JsonPropertyName("ca")] public bool ClearArena { get; set; }
        [JsonPropertyName("lp")] public bool Loop { get; set; }
        [JsonPropertyName("cs")] public float CountScalePerWave { get; set; }
        [JsonPropertyName("lb")] public int LevelBumpPerWave { get; set; }
        [JsonPropertyName("rm")] public string RewardMode { get; set; } = "None";
        [JsonPropertyName("rl")] public string? RewardLootTableRef { get; set; }
        [JsonPropertyName("w")]  public List<Wave> Waves { get; set; } = new();
    }

    public sealed class Wave
    {
        [JsonPropertyName("io")] public int? IntermissionMsOverride { get; set; }
        [JsonPropertyName("e")]  public List<Entry> Entries { get; set; } = new();
    }

    public sealed class Entry
    {
        [JsonPropertyName("a")]  public string? AgentRef { get; set; }
        [JsonPropertyName("an")] public string? AgentName { get; set; }
        [JsonPropertyName("ep")] public bool EnemyPhantom { get; set; }
        [JsonPropertyName("h")]  public string? HeroRef { get; set; }
        [JsonPropertyName("hn")] public string? HeroName { get; set; }
        [JsonPropertyName("c")]  public int Count { get; set; }
        [JsonPropertyName("l")]  public int Level { get; set; }
    }

    public static string Encode(Payload payload)
    {
        var opts = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, opts);

        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(json, 0, json.Length);
        return Prefix + ToBase64Url(ms.ToArray());
    }

    /// <summary>Returns null (with reason) if the code is malformed.</summary>
    public static Payload? TryDecode(string code, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(code)) { error = "Code is empty."; return null; }

        code = code.Trim();
        if (code.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            code = code[Prefix.Length..];

        byte[] bytes;
        try { bytes = FromBase64Url(code); }
        catch { error = "Code isn't valid base64. Copy the whole thing and try again."; return null; }

        byte[] json;
        try
        {
            using var ms = new MemoryStream(bytes);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            gz.CopyTo(outMs);
            json = outMs.ToArray();
        }
        catch { error = "Code payload is corrupt (bad gzip)."; return null; }

        Payload? payload;
        try { payload = JsonSerializer.Deserialize<Payload>(json); }
        catch (Exception ex) { error = "Code payload is corrupt: " + ex.Message; return null; }

        if (payload is null) { error = "Code payload is empty."; return null; }
        if (payload.Version != 1) { error = $"Unsupported code version v{payload.Version} (this build reads v1)."; return null; }
        payload.Waves ??= new List<Wave>();
        return payload;
    }

    // Base64url: '+' → '-', '/' → '_', strip padding — safe for links,
    // Discord messages, etc.
    private static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        string b64 = s.Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4) { case 2: b64 += "=="; break; case 3: b64 += "="; break; }
        return Convert.FromBase64String(b64);
    }
}
