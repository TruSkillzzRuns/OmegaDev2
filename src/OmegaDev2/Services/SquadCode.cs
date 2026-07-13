using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmegaDev2.Services;

/// <summary>
/// Encode / decode Squad Sharing Codes — a compact, portable
/// representation of a squad lineup. The payload is proto-ref IDs and
/// flags only (no images, sounds, or copyrighted client data), so a code
/// on its own is useless without your own 1.52 install + phantom-heroes
/// fork. gzipped JSON, base64url, "OD2S1-" prefix so it's recognizable.
/// </summary>
public static class SquadCode
{
    public const string Prefix = "OD2S1-";

    public sealed class Payload
    {
        [JsonPropertyName("v")]  public int Version { get; set; } = 1;
        [JsonPropertyName("n")]  public string? Name { get; set; }
        [JsonPropertyName("m")]  public List<Member> Members { get; set; } = new();
    }

    public sealed class Member
    {
        [JsonPropertyName("h")]   public string HeroRef { get; set; } = string.Empty;
        [JsonPropertyName("l")]   public int Level { get; set; }
        [JsonPropertyName("lk")]  public bool LockLevel { get; set; }
        [JsonPropertyName("c")]   public string? CostumeRef { get; set; }
        [JsonPropertyName("i")]   public bool Invincible { get; set; }
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
        payload.Members ??= new List<Member>();
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
