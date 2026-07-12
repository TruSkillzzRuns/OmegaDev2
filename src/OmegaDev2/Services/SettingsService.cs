using System.Collections.Generic;

namespace OmegaDev2.Services;

// Thin shim so pages ported verbatim from OmegaDev keep working.
public sealed class SettingsService
{
    public static SettingsService Current { get; } = new();

    public string ServerBaseUrl => AppState.ServerUrl;
    public string? BearerToken => null;

    // Optional client-install paths OmegaDev's Region Builder scans for .upk assets.
    // Empty on this fork — no client-side sip/upk work here.
    public string? ClientInstallPath { get; set; }
    public List<string> ClientInstallPathsExtra { get; } = new();

    // Server repo path — Region Builder saves regions under Desktop\Mods\Custom Regions\<name>\...
    // If OmegaDev refers to a repo path elsewhere, return empty so file ops fall back cleanly.
    public string ServerRepoPath { get; set; } = "";
}
