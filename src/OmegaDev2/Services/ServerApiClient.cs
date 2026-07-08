using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace OmegaDev2.Services;

/// <summary>
/// Lightweight HTTP wrapper around the MHServerEmu WebFrontend. All endpoints
/// return JSON with a top-level {"ok": bool, ...} envelope; the helpers below
/// deserialize into the requested strongly-typed model.
///
/// Server JSON is camelCase — deserializers set PropertyNameCaseInsensitive
/// so DTOs work regardless of casing.
/// </summary>
public sealed class ServerApiClient : IDisposable
{
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public string BaseUrl { get; set; } = "http://localhost:8080";

    public async Task<T?> GetJsonAsync<T>(string path)
    {
        using var res = await _http.GetAsync(BaseUrl.TrimEnd('/') + path);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(body, s_json);
    }

    public async Task<T?> PostJsonAsync<T>(string path, object body)
    {
        string json = JsonSerializer.Serialize(body);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var res = await _http.PostAsync(BaseUrl.TrimEnd('/') + path, content);
        res.EnsureSuccessStatusCode();
        var respBody = await res.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(respBody, s_json);
    }

    public void Dispose() => _http.Dispose();
}
