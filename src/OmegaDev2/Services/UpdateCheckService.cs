using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace OmegaDev2.Services;

/// <summary>
/// Hits the GitHub Releases API for the configured repo, parses the latest
/// release's tag, and compares it against the running app's AssemblyVersion.
/// Same pattern OmegaAssetStudio uses, adapted for a ZIP-based install
/// (no Inno Setup toolchain required for a first release — the release
/// asset is a plain ZIP of the app's publish output).
/// </summary>
public static class UpdateCheckService
{
    // === GitHub repo this app checks for releases against ===
    // Format: "owner/repo". Tag names are expected to be "v1.2.3" or "1.2.3"
    // — both work; the leading 'v' is stripped before version comparison.
    public const string OwnerSlashRepo = "TruSkillzzRuns/OmegaDev2";

    public sealed class UpdateInfo
    {
        public bool Configured { get; init; }
        public bool UpdateAvailable { get; init; }
        public bool CheckSucceeded { get; init; }
        public string CurrentVersion { get; init; } = string.Empty;
        public string LatestVersion { get; init; } = string.Empty;
        public string ReleaseUrl { get; init; } = string.Empty;
        public string ReleaseName { get; init; } = string.Empty;
        public string ReleaseNotes { get; init; } = string.Empty;
        public string DownloadUrl { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
    }

    public static string CurrentAssemblyVersion()
    {
        Version? v = Assembly.GetEntryAssembly()?.GetName().Version
                  ?? Assembly.GetExecutingAssembly().GetName().Version;
        if (v is null) return "0.0.0";
        // Always emit Major.Minor.Build so the display and the comparison
        // match the tag format we ship ("0.2.0", not "0.2").
        int build = v.Build < 0 ? 0 : v.Build;
        if (v.Revision > 0) return $"{v.Major}.{v.Minor}.{build}.{v.Revision}";
        return $"{v.Major}.{v.Minor}.{build}";
    }

    /// <summary>Calls GitHub's /releases/latest endpoint and parses the response.</summary>
    public static async Task<UpdateInfo> CheckAsync()
    {
        string current = CurrentAssemblyVersion();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub requires a User-Agent header or the request 403's.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"OmegaDev2/{current}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        string url = $"https://api.github.com/repos/{OwnerSlashRepo}/releases/latest";

        try
        {
            using var resp = await http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // 404 = no releases published yet (fresh repo). Treat as
                // "up to date" so the UI doesn't shout at the user.
                if ((int)resp.StatusCode == 404)
                {
                    return new UpdateInfo
                    {
                        Configured = true,
                        CheckSucceeded = true,
                        CurrentVersion = current,
                        LatestVersion = current,
                        Message = $"You're on v{current}. No published releases yet.",
                    };
                }
                return new UpdateInfo
                {
                    Configured = true,
                    CheckSucceeded = false,
                    CurrentVersion = current,
                    Message = $"GitHub returned {(int)resp.StatusCode} {resp.ReasonPhrase}. Check network / repo name.",
                };
            }

            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            GhRelease? release = JsonSerializer.Deserialize<GhRelease>(body);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return new UpdateInfo
                {
                    Configured = true,
                    CheckSucceeded = false,
                    CurrentVersion = current,
                    Message = "GitHub response did not contain a tag_name.",
                };
            }

            string latestTagRaw = release.TagName.Trim();
            string latestTagNumeric = latestTagRaw.StartsWith('v') || latestTagRaw.StartsWith('V')
                ? latestTagRaw[1..]
                : latestTagRaw;

            // Prefer the first .zip asset — that's the app payload. Fall
            // back to the release page URL if no ZIP is attached.
            string downloadUrl = string.Empty;
            if (release.Assets is { Length: > 0 })
            {
                foreach (var a in release.Assets)
                {
                    if (string.IsNullOrEmpty(a.BrowserDownloadUrl)) continue;
                    string n = (a.Name ?? "").ToLowerInvariant();
                    if (n.EndsWith(".zip")) { downloadUrl = a.BrowserDownloadUrl; break; }
                }
            }

            bool newer = IsNewer(latestTagNumeric, current);
            return new UpdateInfo
            {
                Configured = true,
                CheckSucceeded = true,
                CurrentVersion = current,
                LatestVersion = latestTagNumeric,
                ReleaseUrl = release.HtmlUrl ?? string.Empty,
                ReleaseName = release.Name ?? latestTagRaw,
                ReleaseNotes = release.Body ?? string.Empty,
                DownloadUrl = downloadUrl,
                UpdateAvailable = newer,
                Message = newer
                    ? $"Update available: v{latestTagNumeric} (you have v{current})."
                    : $"You're on the latest version (v{current}).",
            };
        }
        catch (Exception ex)
        {
            return new UpdateInfo
            {
                Configured = true,
                CheckSucceeded = false,
                CurrentVersion = current,
                Message = $"Update check failed: {ex.GetType().Name}: {ex.Message}",
            };
        }
    }

    /// <summary>
    /// Downloads the ZIP at <paramref name="downloadUrl"/>, extracts it, and
    /// hands off to a PowerShell wrapper that waits for our PID to exit,
    /// then copies the new files over the install directory and relaunches
    /// the app. Caller should exit the process immediately after this
    /// returns success — the wrapper is waiting on our PID.
    /// </summary>
    public static async Task<bool> DownloadAndInstallAsync(
        string downloadUrl,
        string version,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrEmpty(downloadUrl))
            throw new ArgumentException("Download URL is empty.", nameof(downloadUrl));

        string exePath = Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location ?? string.Empty;
        string installDir = string.IsNullOrEmpty(exePath) ? string.Empty : Path.GetDirectoryName(exePath) ?? string.Empty;
        if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
            throw new InvalidOperationException("Cannot determine the app's install directory.");

        // Stable filenames in TEMP so retries don't pile up infinitely.
        string safeVer = new(version.Where(c => char.IsLetterOrDigit(c) || c == '.' || c == '-').ToArray());
        string zipPath = Path.Combine(Path.GetTempPath(), $"OmegaDev2_v{safeVer}.zip");
        string stagingDir = Path.Combine(Path.GetTempPath(), $"OmegaDev2_v{safeVer}_staging");

        // 1) Download
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"OmegaDev2/{CurrentAssemblyVersion()}");
            using var resp = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;

            using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            using var src = await resp.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            byte[] buf = new byte[81920];
            long copied = 0;
            int reportEvery = 0;
            int n;
            while ((n = await src.ReadAsync(buf, cancel).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n), cancel).ConfigureAwait(false);
                copied += n;
                if (++reportEvery >= 4) { reportEvery = 0; progress?.Report(new DownloadProgress(copied, total)); }
            }
            progress?.Report(new DownloadProgress(copied, total));
        }

        // 2) Extract to staging
        try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); } catch { }
        Directory.CreateDirectory(stagingDir);
        ZipFile.ExtractToDirectory(zipPath, stagingDir, overwriteFiles: true);

        // Some releases wrap the payload in a top-level folder (e.g.
        // OmegaDev2_v0.1.1/…). Detect that and use the folder as the source.
        string sourceDir = stagingDir;
        var entries = Directory.EnumerateFileSystemEntries(stagingDir).ToList();
        if (entries.Count == 1 && Directory.Exists(entries[0]))
            sourceDir = entries[0];

        // Sanity check — must contain OmegaDev2.exe.
        if (!File.Exists(Path.Combine(sourceDir, "OmegaDev2.exe")))
            throw new InvalidOperationException("Release payload does not contain OmegaDev2.exe.");

        // 3) Race-proof handoff. PowerShell script:
        //    * waits for our PID to exit
        //    * robocopies staging → install dir (/MIR would delete unmatched
        //      files including user's Config.ini — /E preserves those)
        //    * relaunches OmegaDev2.exe
        //
        // Retry/wait was previously /R:2 /W:1 (2 attempts, 1s apart — up to
        // ~2s total) with ALL robocopy output suppressed (/NFL /NDL /NJH
        // /NJS | Out-Null) and the exit code never checked. If our own
        // process's DLL was still momentarily locked (AV scan, slow
        // teardown, WebView2 lingering) that window silently wasn't long
        // enough, robocopy would skip the locked file without a trace, and
        // we'd relaunch a half-updated install — new pages/resources next to
        // a stale assembly. That's the leading theory for a real user's
        // "update ran, then CTD on clicking heroes, fixed by manually
        // rebuilding" report. Widened the retry window and made failures
        // observable instead of silent.
        int ourPid = Environment.ProcessId;
        string esc(string s) => s.Replace("'", "''");
        string logPath = Path.Combine(Path.GetTempPath(), $"OmegaDev2_update_v{safeVer}.log");
        string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmegaDev2");
        string markerPath = Path.Combine(appDataDir, "update_failed.json");
        string script =
            $"try {{ Wait-Process -Id {ourPid} -ErrorAction Stop -Timeout 30 }} catch {{ }}; " +
            $"Start-Sleep -Milliseconds 500; " +
            // /E copies subdirs including empty; /IS overwrites same-size files
            // (needed when the timestamp trick doesn't detect a real change);
            // /R:6 /W:2 gives a locked file up to ~12s of retries instead of
            // ~2s; /LOG replaces the old fully-suppressed output so a failed
            // copy leaves a real trail instead of nothing.
            $"robocopy '{esc(sourceDir)}' '{esc(installDir)}' /E /IS /R:6 /W:2 /LOG:'{esc(logPath)}' | Out-Null; " +
            $"$code = $LASTEXITCODE; " +
            // Robocopy exit codes 0-7 are informational success (bitmask of
            // "files copied"/"extra files"/etc.); 8+ means at least one file
            // genuinely failed to copy. Leave a marker the app checks on its
            // next launch instead of pretending the update fully succeeded.
            $"if ($code -ge 8) {{ " +
            $"  New-Item -ItemType Directory -Force -Path '{esc(appDataDir)}' | Out-Null; " +
            $"  $marker = @{{ version = '{esc(version)}'; exitCode = $code; logPath = '{esc(logPath)}'; timestamp = (Get-Date).ToString('o') }} | ConvertTo-Json; " +
            $"  Set-Content -Path '{esc(markerPath)}' -Value $marker; " +
            $"}} else {{ Remove-Item -Path '{esc(markerPath)}' -ErrorAction SilentlyContinue }}; " +
            $"Start-Process -FilePath '{esc(Path.Combine(installDir, "OmegaDev2.exe"))}'";

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -WindowStyle Hidden -Command \"{script}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Marker left by a self-update whose robocopy step reported a genuine failure (exit code >= 8).</summary>
    public sealed class UpdateFailureInfo
    {
        [JsonPropertyName("version")]   public string Version { get; set; } = string.Empty;
        [JsonPropertyName("exitCode")]  public int ExitCode { get; set; }
        [JsonPropertyName("logPath")]   public string LogPath { get; set; } = string.Empty;
        [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = string.Empty;
    }

    private static string MarkerPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmegaDev2", "update_failed.json");

    /// <summary>Checks for a marker left by an incomplete update. Call once at startup.</summary>
    public static UpdateFailureInfo? CheckForFailedUpdate()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return null;
            string json = File.ReadAllText(MarkerPath);
            return JsonSerializer.Deserialize<UpdateFailureInfo>(json);
        }
        catch { return null; }
    }

    /// <summary>Clears the failed-update marker once the user has been shown it.</summary>
    public static void ClearFailedUpdateMarker()
    {
        try { if (File.Exists(MarkerPath)) File.Delete(MarkerPath); } catch { }
    }

    public readonly record struct DownloadProgress(long BytesReceived, long? TotalBytes)
    {
        public int PercentOrZero => TotalBytes is { } t && t > 0
            ? (int)Math.Clamp(BytesReceived * 100 / t, 0, 100)
            : 0;
    }

    private static bool IsNewer(string remote, string local)
    {
        if (Version.TryParse(NormalizeForVersion(remote), out var rv)
            && Version.TryParse(NormalizeForVersion(local), out var lv))
            return rv > lv;
        return string.Compare(remote, local, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private static string NormalizeForVersion(string s)
    {
        int dash = s.IndexOf('-');
        if (dash > 0) s = s[..dash];
        return s.Contains('.') ? s : s + ".0";
    }

    private sealed class GhRelease
    {
        [JsonPropertyName("tag_name")]    public string? TagName { get; set; }
        [JsonPropertyName("name")]        public string? Name { get; set; }
        [JsonPropertyName("html_url")]    public string? HtmlUrl { get; set; }
        [JsonPropertyName("body")]        public string? Body { get; set; }
        [JsonPropertyName("prerelease")]  public bool Prerelease { get; set; }
        [JsonPropertyName("draft")]       public bool Draft { get; set; }
        [JsonPropertyName("assets")]      public GhAsset[]? Assets { get; set; }
    }

    private sealed class GhAsset
    {
        [JsonPropertyName("name")]                 public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("size")]                 public long Size { get; set; }
    }
}
