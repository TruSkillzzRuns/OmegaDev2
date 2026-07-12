using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OmegaDev2.Services;

/// <summary>
/// HTTP wrapper for the fork's WebFrontend. Method surface mirrors OmegaDev's
/// ServerApiClient so pages ported over compile without touching.
/// </summary>
public sealed class ServerApiClient : IDisposable
{
    private static readonly JsonSerializerOptions s_caseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private string _baseUrl;

    public ServerApiClient() : this(AppState.ServerUrl) { }

    public ServerApiClient(string baseUrl, string? bearerToken = null, TimeSpan? timeout = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
        if (!string.IsNullOrWhiteSpace(bearerToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set { _baseUrl = (value ?? "").TrimEnd('/'); }
    }

    private Uri BuildUri(string path)
        => new Uri($"{_baseUrl}/{path.TrimStart('/')}");

    // ---- Generic helpers (kept for pages that use them directly) ----
    public async Task<T?> GetJsonAsync<T>(string path, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(BuildUri(path), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(body, s_caseInsensitive);
    }
    public async Task<T?> PostJsonAsync<T>(string path, object body, CancellationToken ct = default)
    {
        string json = JsonSerializer.Serialize(body);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(BuildUri(path), content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var respBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(respBody, s_caseInsensitive);
    }

    // ---- Regions ----
    public async Task<RegionListResponse?> GetRegionListAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(BuildUri("webapi/regions/list"), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<RegionListResponse>(stream, s_caseInsensitive, ct).ConfigureAwait(false);
    }

    public async Task<(int Status, string Body)> PostRegionRemixWarpAsync(
        string playerName, string? playerDbId, string regionProtoRef,
        int level, string? difficultyTierRef, string[]? affixes, string? itemRarityRef,
        int endlessLevel, bool allowUnsafe, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new {
            playerName, playerDbId, regionProtoRef,
            level, difficultyTierRef, affixes, itemRarityRef, endlessLevel, allowUnsafe
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(BuildUri("webapi/regionremix/warp"), content, ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    // ---- Player warp (used by Region Forge) ----
    public async Task<(int Status, string Body)> PostPlayerWarpAsync(string playerName, string? playerDbId, string regionProtoRef, bool allowUnsafe, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { playerName, playerDbId, regionProtoRef, allowUnsafe });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(BuildUri("webapi/playeradmin/warp"), content, ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    // ---- Mod save (used by Region Forge) ----
    public async Task<(int Status, string Body)> PostModSaveAsync(string jsonBody, CancellationToken ct = default)
    {
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(BuildUri("webapi/mods/save"), content, ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    // ---- Prototype catalog ----
    public async Task<(int Status, string Body)> GetProtoCatalogAsync(string className, string? pathFilter = null, CancellationToken ct = default)
    {
        string url = $"webapi/protocatalog?class={Uri.EscapeDataString(className)}";
        if (!string.IsNullOrEmpty(pathFilter)) url += $"&pathFilter={Uri.EscapeDataString(pathFilter)}";
        using var resp = await _http.GetAsync(BuildUri(url), ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    // ---- Ported OmegaDev methods ----

    public async Task<(int Status, string Body)> GetBossSpawnersAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(BuildUri("webapi/spawners/bosses"), ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public async Task<(int Status, string Body)> GetCellDiagAsync(string namesCsv, CancellationToken ct = default)
    {
        string url = $"webapi/celldiag?names={Uri.EscapeDataString(namesCsv ?? "")}";
        using var resp = await _http.GetAsync(BuildUri(url), ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public async Task<(int Status, string Body)> GetCellExitsAsync(string cellRef, CancellationToken ct = default)
    {
        string url = $"webapi/cells/exits?ref={Uri.EscapeDataString(cellRef ?? "")}";
        using var resp = await _http.GetAsync(BuildUri(url), ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public async Task<(int Status, string Body)> GetCellFamiliesAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(BuildUri("webapi/cells/families"), ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public async Task<(int Status, string Body)> GetCellMatesAsync(string cellRef, CancellationToken ct = default)
    {
        string url = $"webapi/cells/mates?cellRef={Uri.EscapeDataString(cellRef ?? "")}";
        using var resp = await _http.GetAsync(BuildUri(url), ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public async Task<(int Status, string Body)> GetCellsListAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(BuildUri("webapi/cells/list"), ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public async Task<(int Status, string Body)> GetEnemiesByRegionAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(BuildUri("webapi/enemies/byregion"), ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public async Task<EnemyCatalogResponse?> GetEnemyCatalogAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(BuildUri("webapi/enemies/catalog"), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<EnemyCatalogResponse>(stream, s_caseInsensitive, ct).ConfigureAwait(false);
    }

    public async Task<byte[]?> GetPortraitPngAsync(string assetPath, int w = 128, int h = 128, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(assetPath)) return null;
        string url = $"webapi/portrait?path={Uri.EscapeDataString(assetPath)}&w={w}&h={h}";
        using var resp = await _http.GetAsync(BuildUri(url), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public async Task<byte[]?> GetTexturePngAsync(string assetName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(assetName)) return null;
        string url = $"webapi/texbyname?name={Uri.EscapeDataString(assetName)}";
        using var resp = await _http.GetAsync(BuildUri(url), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public async Task<(int Status, string Body)> GetRegionBuildsListAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(BuildUri("webapi/regionbuilder/list"), ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public async Task<(int Status, string Body)> GetRegionTemplatesAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(BuildUri("webapi/regions/templates"), ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public async Task<(int Status, string Body)> GetTerminalsIndexAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(BuildUri("webapi/terminals/index"), ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public async Task<(int Status, string Body)> PostRegionBuilderWarpAsync(
        string playerName, string regionBuildPath, bool trace = false, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { playerName, regionBuildPath, allowUnsafe = true, trace });
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        string url = trace ? "webapi/regionbuilder/warp?trace=1" : "webapi/regionbuilder/warp";
        using var resp = await _http.PostAsync(BuildUri(url), content, ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public async Task<(int Status, string Body)> PostRegionTemplateSynthesizeAsync(string regionRef, long seed, CancellationToken ct = default)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { regionRef = regionRef ?? "", seed });
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(BuildUri("webapi/regions/template/synthesize"), content, ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public async Task<(int Status, string Body)> PostSpawnHubPortalsAsync(string playerName = "*", CancellationToken ct = default)
    {
        var payload = new { playerName = playerName ?? "*", playerDbId = "0" };
        string json = System.Text.Json.JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(BuildUri("webapi/regionbuilder/spawnhubportals"), content, ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public async Task<(int Status, string Body)> PostTerminalSnapshotAsync(
        string sourceTerminalRef, int seed, string family, string regionName,
        string bossSpawnerRef, int enemyFillCount, string enemyFillAgentRef,
        bool addEntryWaypoint, string[] enemyFillPool = null,
        CancellationToken ct = default)
    {
        var payload = new
        {
            sourceTerminalRef = sourceTerminalRef ?? "",
            seed,
            family = family ?? "",
            regionName = regionName ?? "",
            bossSpawnerRef = bossSpawnerRef ?? "",
            enemyFillCount,
            enemyFillAgentRef = enemyFillAgentRef ?? "",
            addEntryWaypoint,
            enemyFillPool = enemyFillPool ?? Array.Empty<string>(),
        };
        string json = System.Text.Json.JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(BuildUri("webapi/terminals/snapshot"), content, ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public async Task<(int Status, string Body)> PostTerminalSynthesizeAsync(
        string sourceTerminalRef, string family, int bossCount, string regionName,
        string bossSpawnerRef = "", int enemyFillCount = 0, string enemyFillAgentRef = "",
        bool addEntryWaypoint = true, string entryWaypointRef = "", string entryReturnRegionRef = "",
        string length = "",
        CancellationToken ct = default)
    {
        var payload = new
        {
            sourceTerminalRef = sourceTerminalRef ?? "",
            family = family ?? "",
            bossCount,
            regionName = regionName ?? "",
            bossSpawnerRef = bossSpawnerRef ?? "",
            enemyFillCount,
            enemyFillAgentRef = enemyFillAgentRef ?? "",
            addEntryWaypoint,
            entryWaypointRef = entryWaypointRef ?? "",
            entryReturnRegionRef = entryReturnRegionRef ?? "",
            // TR11 — "short" / "medium" / "long" / empty. Server treats
            // empty + "medium" identically (chain as authored).
            length = length ?? "",
        };
        string json = System.Text.Json.JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(BuildUri("webapi/terminals/synthesize"), content, ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }


    // ---- Gear Picker ----
    public async Task<ItemCatalogResponse?> GetItemCatalogAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(BuildUri("webapi/items/catalog"), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<ItemCatalogResponse>(stream, s_caseInsensitive, ct).ConfigureAwait(false);
    }

    public async Task<(int Status, string Body)> PostItemGiveAsync(ItemGiveRequest request, CancellationToken ct = default)
    {
        string json = JsonSerializer.Serialize(request, s_camelCase);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(BuildUri("webapi/items/give"), content, ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    private static readonly JsonSerializerOptions s_camelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // ---- Phantom Heroes ----
    public Task<PhantomHeroesResponse?> GetPhantomHeroesAsync(CancellationToken ct = default)
        => GetJsonAsync<PhantomHeroesResponse>("webapi/phantoms/catalog", ct);

    public Task<PhantomCostumesResponse?> GetPhantomCostumesAsync(string heroProtoRef, CancellationToken ct = default)
        => GetJsonAsync<PhantomCostumesResponse>($"webapi/phantoms/catalog?hero={Uri.EscapeDataString(heroProtoRef)}", ct);

    public Task<PhantomStatusResponse?> GetPhantomStatusAsync(string player, CancellationToken ct = default)
        => GetJsonAsync<PhantomStatusResponse>($"webapi/phantoms/status?player={Uri.EscapeDataString(player ?? "*")}", ct);

    public Task<PhantomSquadsResponse?> GetPhantomSquadsAsync(string player, CancellationToken ct = default)
        => GetJsonAsync<PhantomSquadsResponse>($"webapi/phantoms/squads?player={Uri.EscapeDataString(player ?? "*")}", ct);

    public Task<PhantomSpawnResponse?> PostPhantomSpawnAsync(object body, CancellationToken ct = default)
        => PostJsonAsync<PhantomSpawnResponse>("webapi/phantoms/spawn", body, ct);

    public Task<PhantomOpResponse?> PostPhantomClearAsync(string playerName, CancellationToken ct = default)
        => PostJsonAsync<PhantomOpResponse>("webapi/phantoms/clear", new { playerName }, ct);

    public Task<PhantomOpResponse?> PostPhantomCostumeAsync(object body, CancellationToken ct = default)
        => PostJsonAsync<PhantomOpResponse>("webapi/phantoms/costume", body, ct);

    public Task<PhantomOpResponse?> PostPhantomGearAsync(string playerName, string? phantomQuery, CancellationToken ct = default)
        => PostJsonAsync<PhantomOpResponse>("webapi/phantoms/gear", new { playerName, phantomQuery }, ct);

    public Task<PhantomOpResponse?> PostPhantomSquadOpAsync(string playerName, string op, string name, CancellationToken ct = default)
        => PostJsonAsync<PhantomOpResponse>("webapi/phantoms/squads", new { playerName, op, name }, ct);

    public Task<PhantomOpResponse?> PostPhantomSquadSaveListAsync(string playerName, string name, object members, CancellationToken ct = default)
        => PostJsonAsync<PhantomOpResponse>("webapi/phantoms/squads", new { playerName, op = "savelist", name, members }, ct);

    // ---- Arena: Enemy Phantoms + Wave Director ----
    public Task<PhantomSpawnResponse?> PostEnemyPhantomSpawnAsync(object body, CancellationToken ct = default)
        => PostJsonAsync<PhantomSpawnResponse>("webapi/arena/enemyphantoms/spawn", body, ct);

    public Task<PhantomOpResponse?> PostEnemyPhantomClearAsync(string playerName, CancellationToken ct = default)
        => PostJsonAsync<PhantomOpResponse>("webapi/arena/enemyphantoms/clear", new { playerName }, ct);

    public Task<EnemyPhantomStatusResponse?> GetEnemyPhantomStatusAsync(string player, CancellationToken ct = default)
        => GetJsonAsync<EnemyPhantomStatusResponse>($"webapi/arena/enemyphantoms/status?player={Uri.EscapeDataString(player ?? "*")}", ct);

    public Task<PhantomOpResponse?> PostWavesStartAsync(object body, CancellationToken ct = default)
        => PostJsonAsync<PhantomOpResponse>("webapi/arena/waves/start", body, ct);

    public Task<PhantomOpResponse?> PostWavesStopAsync(string playerName, bool cleanup, CancellationToken ct = default)
        => PostJsonAsync<PhantomOpResponse>("webapi/arena/waves/stop", new { playerName, cleanup }, ct);

    public Task<WavesStatusResponse?> GetWavesStatusAsync(string player, CancellationToken ct = default)
        => GetJsonAsync<WavesStatusResponse>($"webapi/arena/waves/status?player={Uri.EscapeDataString(player ?? "*")}", ct);

    // ---- Stash Manager ----
    public Task<InventoryResponse?> GetInventoryAsync(string player, CancellationToken ct = default)
        => GetJsonAsync<InventoryResponse>($"webapi/inventory?player={Uri.EscapeDataString(player ?? "*")}", ct);

    public Task<PhantomOpResponse?> PostInventoryDeleteAsync(string playerName, string entityId, CancellationToken ct = default)
        => PostJsonAsync<PhantomOpResponse>("webapi/inventory/delete", new { playerName, entityId }, ct);

    // ---- DPS Meter ----
    public Task<DpsResponse?> GetDpsAsync(string player, CancellationToken ct = default)
        => GetJsonAsync<DpsResponse>($"webapi/dps?player={Uri.EscapeDataString(player ?? "*")}", ct);

    public Task<PhantomOpResponse?> PostDpsResetAsync(CancellationToken ct = default)
        => PostJsonAsync<PhantomOpResponse>("webapi/dps/reset", new { }, ct);

    // ---- Command Console ----
    public Task<ConsoleExecResponse?> PostConsoleExecAsync(string command, string? playerName, CancellationToken ct = default)
        => PostJsonAsync<ConsoleExecResponse>("webapi/console/exec", new { command, playerName }, ct);

    // ---- Live Log Viewer ----
    public Task<LogsTailResponse?> GetLogsTailAsync(int lines, CancellationToken ct = default)
        => GetJsonAsync<LogsTailResponse>($"webapi/logs/tail?lines={lines}", ct);

    public void Dispose() => _http.Dispose();
}

// ---- Phantom Heroes DTOs (match /webapi/phantoms/* on the 1.52 fork) ----

public sealed class PhantomHeroesResponse
{
    public int TotalHeroes { get; set; }
    public System.Collections.Generic.List<PhantomHeroEntry> Heroes { get; set; } = new();
}

public sealed class PhantomHeroEntry
{
    public string ProtoRef { get; set; } = "";
    public string Name { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? PortraitPath { get; set; }
    public System.Collections.Generic.List<string>? PortraitCandidates { get; set; }
}

public sealed class PhantomCostumesResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public int TotalCostumes { get; set; }
    public System.Collections.Generic.List<PhantomCostumeEntry> Costumes { get; set; } = new();
}

public sealed class PhantomCostumeEntry
{
    public string ProtoRef { get; set; } = "";
    public string Name { get; set; } = "";
    public string? DisplayName { get; set; }
}

public sealed class PhantomStatusResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Player { get; set; }
    public int Count { get; set; }
    public System.Collections.Generic.List<PhantomInfoEntry> Phantoms { get; set; } = new();
}

public sealed class PhantomInfoEntry
{
    public string AvatarId { get; set; } = "";
    public string HeroProtoRef { get; set; } = "";
    public string HeroName { get; set; } = "";
    public string Username { get; set; } = "";
    public int Level { get; set; }
    public bool LockLevel { get; set; }
    public string? CostumeRef { get; set; }
    public bool InWorld { get; set; }
}

public sealed class PhantomSquadsResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public System.Collections.Generic.List<PhantomSquadEntry> Squads { get; set; } = new();
}

public sealed class PhantomSquadEntry
{
    public string Name { get; set; } = "";
    public System.Collections.Generic.List<string> Heroes { get; set; } = new();
    public System.Collections.Generic.List<int> Levels { get; set; } = new();
    public System.Collections.Generic.List<PhantomSquadMemberEntry>? Members { get; set; }
}

public sealed class PhantomSquadMemberEntry
{
    public string AvatarRef { get; set; } = "";
    public string HeroName { get; set; } = "";
    public int Level { get; set; }
    public bool LockLevel { get; set; }
    public string? CostumeRef { get; set; }
}

public sealed class EnemyPhantomStatusResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public int Count { get; set; }
    public System.Collections.Generic.List<EnemyPhantomEntry> Enemies { get; set; } = new();
}

public sealed class EnemyPhantomEntry
{
    public string HeroName { get; set; } = "";
    public int Level { get; set; }
    public int HealthPct { get; set; }
    public bool Dead { get; set; }
}

public sealed class WavesStatusResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public WaveStatusEntry? Status { get; set; }
}

public sealed class WaveStatusEntry
{
    public bool Active { get; set; }
    public string State { get; set; } = "";
    public int Wave { get; set; }
    public int TotalWaves { get; set; }
    public int Alive { get; set; }
    public int Kills { get; set; }
    public int SpawnedTotal { get; set; }
    public long RunSeconds { get; set; }
    public long IntermissionRemainingMs { get; set; }
}

public sealed class InventoryResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Player { get; set; }
    public int TotalItems { get; set; }
    public System.Collections.Generic.List<InventoryContainer> Containers { get; set; } = new();
}

public sealed class InventoryContainer
{
    public string Name { get; set; } = "";
    public string? Category { get; set; }
    public int Capacity { get; set; }
    public int Count { get; set; }
    public System.Collections.Generic.List<InventoryItemEntry> Items { get; set; } = new();
}

public sealed class InventoryItemEntry
{
    public string EntityId { get; set; } = "";
    public string ProtoRef { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Rarity { get; set; }
    public int RarityTier { get; set; }
    public int Stack { get; set; }
    public int Level { get; set; }
    public int Slot { get; set; }
    public string? IconPath { get; set; }
}

public sealed class DpsResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Player { get; set; }
    public long SecondsSinceReset { get; set; }
    public System.Collections.Generic.List<DpsCombatant> Combatants { get; set; } = new();
}

public sealed class DpsCombatant
{
    public string Name { get; set; } = "";
    public bool IsPhantom { get; set; }
    public long Total { get; set; }
    public double Dps10 { get; set; }
    public double Dps60 { get; set; }
    public double DpsOverall { get; set; }
    public long SecondsSinceLastHit { get; set; }
}

public sealed class ConsoleExecResponse
{
    public bool Ok { get; set; }
    public string? Command { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
}

public sealed class LogsTailResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? File { get; set; }
    public int Count { get; set; }
    public System.Collections.Generic.List<string> Lines { get; set; } = new();
}

public sealed class PhantomSpawnResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public int Spawned { get; set; }
    public int Failed { get; set; }
    public string? FirstError { get; set; }
}

public sealed class PhantomOpResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
    public int Removed { get; set; }
}

public sealed class RegionListResponse
{
    public int TotalRegions { get; set; }
    public List<RegionListEntry> Regions { get; set; } = new();
}

public sealed class RegionListEntry
{
    public string ProtoRef { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsSafe { get; set; }
}

public sealed class EnemyCatalogResponse
{
    public System.Collections.Generic.List<EnemyCatalogEntry> Entries { get; set; } = new();
}

public sealed class EnemyCatalogEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    // Server pins the JSON property name to "ref" (JS-friendly short name).
    [System.Text.Json.Serialization.JsonPropertyName("ref")]
    public string ProtoRef { get; set; } = "";
    public string PortraitPath { get; set; } = "";
    public string Faction { get; set; } = "";
    public string Rank { get; set; } = "";
    public string HealthCurveRef { get; set; } = "";
    public double HealthCurveTier { get; set; }
    public string Region { get; set; } = "";
}

// ---- Gear Picker DTOs (match /webapi/items/* on the 1.52 fork) ----

public sealed class ItemCatalogResponse
{
    public int TotalItems { get; set; }
    public System.Collections.Generic.List<string> Categories { get; set; } = new();
    public System.Collections.Generic.List<ItemRarityEntry> Rarities { get; set; } = new();
    public System.Collections.Generic.List<ItemCatalogEntry> Items { get; set; } = new();
}

public sealed class ItemRarityEntry
{
    public string ProtoRef { get; set; } = "";
    public string Name { get; set; } = "";
    public int Tier { get; set; }
}

public sealed class ItemCatalogEntry
{
    public string ProtoRef { get; set; } = "";
    public string Name { get; set; } = "";
    public string? DisplayName { get; set; }
    public string Path { get; set; } = "";
    public string Category { get; set; } = "";
    public string? Slot { get; set; }
    public string? Avatar { get; set; }
    public string? IconPath { get; set; }
    public bool IsUnique { get; set; }
}

public sealed class ItemGiveRequest
{
    public string? PlayerName { get; set; }
    public string? PlayerDbId { get; set; }
    public System.Collections.Generic.List<ItemGiveBatchEntry> Items { get; set; } = new();
}

public sealed class ItemGiveBatchEntry
{
    public string ItemProtoRef { get; set; } = "";
    public int Count { get; set; } = 1;
    public int Level { get; set; }
    public string? RarityProtoRef { get; set; }
}

public sealed class ItemGiveResponse
{
    public string Message { get; set; } = "";
    public int GivenCount { get; set; }
    public System.Collections.Generic.List<ItemGiveResultEntry>? Results { get; set; }
}

public sealed class ItemGiveResultEntry
{
    public string ItemProtoRef { get; set; } = "";
    public int StatusCode { get; set; }
    public int GivenCount { get; set; }
    public string Message { get; set; } = "";
}
