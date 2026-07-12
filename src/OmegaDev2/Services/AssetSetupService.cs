using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace OmegaDev2.Services;

// One-click asset setup: given the user's server folder and game-client
// folder, configure everything the icon/portrait/name pipeline needs —
// Config.ini [ClientAssets] + LoadLocaleFiles, locale file copy, and the
// one-time texture index. All discovery is by generic directory/file names
// (CookedPCConsole, TextureFileCacheManifest.bin, *.locale); nothing
// game-specific is hardcoded here.
public sealed class AssetSetupService
{
    // ---------------- persisted paths ----------------

    public sealed class SetupPaths
    {
        public string? ServerDir { get; set; }
        public string? ClientDir { get; set; }
        public string? RepoDir { get; set; }
    }

    private static string SettingsFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmegaDev2", "setup.json");

    public static SetupPaths LoadPaths()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
                return JsonSerializer.Deserialize<SetupPaths>(File.ReadAllText(SettingsFilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public static void SavePaths(SetupPaths paths)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);
            File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(paths, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ---------------- discovery / validation ----------------

    public sealed class SetupStatus
    {
        public bool ServerOk;            // MHServerEmu.exe + Config.ini present
        public string? ConfigIniPath;
        public bool CookedOk;            // CookedPCConsole directory located
        public string? CookedPath;
        public bool ManifestOk;          // TextureFileCacheManifest.bin inside it
        public bool ToolsOk;             // UpkExtract.exe + TfcExtract.exe next to server
        public string? UpkExtractPath;
        public bool LocaleSourceOk;      // client locale files located
        public string? LocaleSourcePath;
        public bool LocaleInstalled;     // server Data\Game\Loco already populated
        public bool TexIndexOk;          // Cache\texIndex.json already built
        public bool ConfigApplied;       // [ClientAssets] path + LoadLocaleFiles already set
        public bool RepoOk;              // server repo with buildable tool projects
        public string? RepoToolsDir;
        public string? ServerDirResolved; // auto-corrected server folder (exe found below the selected one)
    }

    public static SetupStatus Inspect(string? serverDir, string? clientDir, string? repoDir = null)
    {
        var s = new SetupStatus();

        if (string.IsNullOrWhiteSpace(serverDir) == false && Directory.Exists(serverDir))
        {
            // Forgiving selection: users pick the repo root or some parent —
            // walk down and find the folder that actually holds the server.
            serverDir = FindServerDir(serverDir!.Trim()) ?? serverDir!.Trim();
            s.ServerDirResolved = serverDir;

            string exe = Path.Combine(serverDir, "MHServerEmu.exe");
            string ini = Path.Combine(serverDir, "Config.ini");
            s.ServerOk = File.Exists(exe) && File.Exists(ini);
            if (s.ServerOk)
            {
                s.ConfigIniPath = ini;
                s.UpkExtractPath = Path.Combine(serverDir, "Tools", "UpkExtract", "UpkExtract.exe");
                s.ToolsOk = File.Exists(s.UpkExtractPath) &&
                            File.Exists(Path.Combine(serverDir, "Tools", "TfcExtract", "TfcExtract.exe"));
                s.TexIndexOk = File.Exists(Path.Combine(serverDir, "Cache", "texIndex.json"));
                s.LocaleInstalled = Directory.Exists(Path.Combine(serverDir, "Data", "Game", "Loco")) &&
                                    Directory.EnumerateFileSystemEntries(Path.Combine(serverDir, "Data", "Game", "Loco")).Any();
            }
        }

        if (string.IsNullOrWhiteSpace(clientDir) == false && Directory.Exists(clientDir))
        {
            s.CookedPath = FindDirectory(clientDir, "CookedPCConsole", maxDepth: 4);
            s.CookedOk = s.CookedPath != null;
            s.ManifestOk = s.CookedPath != null && File.Exists(Path.Combine(s.CookedPath, "TextureFileCacheManifest.bin"));

            s.LocaleSourcePath = FindLocaleDirectory(clientDir);
            s.LocaleSourceOk = s.LocaleSourcePath != null;
        }

        if (string.IsNullOrWhiteSpace(repoDir) == false && Directory.Exists(repoDir))
        {
            // Accept the repo root or the tools folder itself.
            string toolsDir = Directory.Exists(Path.Combine(repoDir, "tools")) ? Path.Combine(repoDir, "tools") : repoDir;
            bool hasUpk = File.Exists(Path.Combine(toolsDir, "UpkExtract", "UpkExtract.csproj"));
            bool hasTfc = File.Exists(Path.Combine(toolsDir, "TfcExtract", "TfcExtract.csproj"));
            if (hasUpk && hasTfc)
            {
                s.RepoOk = true;
                s.RepoToolsDir = toolsDir;
            }
        }

        if (s.ConfigIniPath != null && s.CookedPath != null)
        {
            try
            {
                string ini = File.ReadAllText(s.ConfigIniPath);
                bool pathSet = ini.IndexOf($"CookedPCConsolePath={s.CookedPath}", StringComparison.OrdinalIgnoreCase) >= 0;
                bool localeOn = System.Text.RegularExpressions.Regex.IsMatch(ini, @"(?im)^\s*LoadLocaleFiles\s*=\s*true\s*$");
                s.ConfigApplied = pathSet && localeOn;
            }
            catch { }
        }

        return s;
    }

    // Locate the folder MHServerEmu actually runs from, starting at whatever
    // the user selected. Direct hit wins; otherwise walk down (the built exe
    // sits ~5 levels below a repo root: src\MHServerEmu\bin\x64\Release\...).
    // Multiple candidates (Debug + Release builds): newest exe wins.
    private static string? FindServerDir(string root)
    {
        static bool IsServerDir(string dir)
            => File.Exists(Path.Combine(dir, "MHServerEmu.exe")) && File.Exists(Path.Combine(dir, "Config.ini"));

        if (IsServerDir(root)) return root;

        var candidates = new List<string>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            try
            {
                foreach (string sub in Directory.EnumerateDirectories(dir))
                {
                    string name = Path.GetFileName(sub);
                    if (name.StartsWith('.') || name.Equals("obj", StringComparison.OrdinalIgnoreCase)) continue;
                    if (IsServerDir(sub)) { candidates.Add(sub); continue; }
                    if (depth + 1 < 7) queue.Enqueue((sub, depth + 1));
                }
            }
            catch { /* access denied etc. — skip */ }
        }

        return candidates
            .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "MHServerEmu.exe")))
            .FirstOrDefault();
    }

    // Breadth-first search for a directory by name, depth-limited so a
    // whole-drive selection doesn't hang the UI.
    private static string? FindDirectory(string root, string name, int maxDepth)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            try
            {
                foreach (string sub in Directory.EnumerateDirectories(dir))
                {
                    if (string.Equals(Path.GetFileName(sub), name, StringComparison.OrdinalIgnoreCase))
                        return sub;
                    if (depth + 1 < maxDepth)
                        queue.Enqueue((sub, depth + 1));
                }
            }
            catch { /* access denied etc. — skip */ }
        }
        return null;
    }

    // The client keeps localized strings in a "Loco" folder holding *.locale
    // files (plus per-language subfolders).
    private static string? FindLocaleDirectory(string clientDir)
    {
        string? loco = FindDirectory(clientDir, "Loco", maxDepth: 4);
        if (loco == null) return null;
        try
        {
            return Directory.EnumerateFiles(loco, "*.locale", SearchOption.TopDirectoryOnly).Any() ? loco : null;
        }
        catch { return null; }
    }

    // ---------------- apply steps ----------------

    /// <summary>Set [ClientAssets] CookedPCConsolePath and [GameData] LoadLocaleFiles=true, preserving the rest of the file.</summary>
    public static string ApplyConfig(string configIniPath, string cookedPath)
    {
        var lines = File.ReadAllLines(configIniPath).ToList();

        SetIniValue(lines, "ClientAssets", "CookedPCConsolePath", cookedPath);
        SetIniValue(lines, "GameData", "LoadLocaleFiles", "true");

        File.WriteAllLines(configIniPath, lines);
        return "Config.ini updated (client path + locale loading).";
    }

    private static void SetIniValue(List<string> lines, string section, string key, string value)
    {
        int sectionStart = -1, sectionEnd = lines.Count;
        for (int i = 0; i < lines.Count; i++)
        {
            string t = lines[i].Trim();
            if (sectionStart < 0)
            {
                if (t.Equals($"[{section}]", StringComparison.OrdinalIgnoreCase)) sectionStart = i;
            }
            else if (t.StartsWith('[')) { sectionEnd = i; break; }
        }

        if (sectionStart < 0)
        {
            // Section missing (older server build) — append it.
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
            return;
        }

        for (int i = sectionStart + 1; i < sectionEnd; i++)
        {
            string t = lines[i].TrimStart();
            if (t.StartsWith(';') || t.StartsWith('#')) continue;
            if (t.StartsWith(key, StringComparison.OrdinalIgnoreCase) &&
                t[key.Length..].TrimStart().StartsWith('='))
            {
                lines[i] = $"{key}={value}";
                return;
            }
        }

        lines.Insert(sectionEnd, $"{key}={value}");
    }

    /// <summary>Copy the client's locale files into the server's Data\Game\Loco.</summary>
    public static string CopyLocaleFiles(string localeSourceDir, string serverDir)
    {
        string dest = Path.Combine(serverDir, "Data", "Game", "Loco");
        Directory.CreateDirectory(dest);

        int copied = 0;
        foreach (string src in Directory.EnumerateFiles(localeSourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(localeSourceDir, src);
            string target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(src).Length) continue;
            File.Copy(src, target, overwrite: true);
            copied++;
        }
        return $"Locale files: {copied} copied to Data\\Game\\Loco.";
    }

    /// <summary>
    /// Build the extraction tools from the server repo (dotnet build) and
    /// install the outputs under &lt;server&gt;\Tools\. Requires the .NET SDK,
    /// which anyone who built the server already has.
    /// </summary>
    public static async Task<bool> BuildToolsAsync(string repoToolsDir, string serverDir, Action<string> onOutput)
    {
        foreach (string tool in new[] { "UpkExtract", "TfcExtract" })
        {
            string csproj = Path.Combine(repoToolsDir, tool, $"{tool}.csproj");
            onOutput($"building {tool}…");

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("build");
            psi.ArgumentList.Add(csproj);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("Release");
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("quiet");
            psi.ArgumentList.Add("--nologo");

            Process? proc;
            try { proc = Process.Start(psi); }
            catch (Exception ex)
            {
                onOutput($"could not run 'dotnet' — is the .NET 8 SDK installed? ({ex.Message})");
                return false;
            }
            if (proc == null) { onOutput("failed to start dotnet"); return false; }

            proc.OutputDataReceived += (_, e) => { if (string.IsNullOrEmpty(e.Data) == false) onOutput("  " + e.Data.Trim()); };
            proc.ErrorDataReceived += (_, e) => { if (string.IsNullOrEmpty(e.Data) == false) onOutput("  " + e.Data.Trim()); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0) { onOutput($"{tool}: build FAILED (exit {proc.ExitCode})"); return false; }

            // Locate the build output (bin\Release\<tfm>\ holding the exe)
            // and install it under <server>\Tools\<tool>\.
            string binDir = Path.Combine(repoToolsDir, tool, "bin", "Release");
            string? outputDir = Directory.Exists(binDir)
                ? Directory.EnumerateFiles(binDir, $"{tool}.exe", SearchOption.AllDirectories)
                    .Select(Path.GetDirectoryName)
                    .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d!, $"{tool}.exe")))
                    .FirstOrDefault()
                : null;
            if (outputDir == null) { onOutput($"{tool}: build output not found under {binDir}"); return false; }

            string dest = Path.Combine(serverDir, "Tools", tool);
            Directory.CreateDirectory(dest);
            int copied = 0;
            foreach (string src in Directory.EnumerateFiles(outputDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(outputDir, src);
                string target = Path.Combine(dest, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(src, target, overwrite: true);
                copied++;
            }

            // Native runtime DLLs also need to sit flat next to the exe for
            // P/Invoke resolution when the tool is launched directly.
            string nativeDir = Path.Combine(dest, "runtimes", "win-x64", "native");
            if (Directory.Exists(nativeDir))
                foreach (string dll in Directory.EnumerateFiles(nativeDir, "*.dll"))
                    File.Copy(dll, Path.Combine(dest, Path.GetFileName(dll)), overwrite: true);

            onOutput($"{tool}: installed {copied} file(s) to Tools\\{tool}\\.");
        }

        return true;
    }

    /// <summary>
    /// Run the one-time texture index (UpkExtract indexmeshes). Streams
    /// output lines to <paramref name="onOutput"/>; can take a few minutes
    /// on a full client.
    /// </summary>
    public static async Task<bool> BuildTextureIndexAsync(string upkExtractExe, string cookedPath, string serverDir, Action<string> onOutput)
    {
        string cacheDir = Path.Combine(serverDir, "Cache");
        Directory.CreateDirectory(cacheDir);

        var psi = new ProcessStartInfo
        {
            FileName = upkExtractExe,
            WorkingDirectory = serverDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("indexmeshes");
        psi.ArgumentList.Add(cookedPath);
        psi.ArgumentList.Add(cacheDir);

        using var proc = Process.Start(psi);
        if (proc == null) { onOutput("failed to start UpkExtract"); return false; }

        proc.OutputDataReceived += (_, e) => { if (string.IsNullOrEmpty(e.Data) == false) onOutput(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (string.IsNullOrEmpty(e.Data) == false) onOutput(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await proc.WaitForExitAsync();
        return proc.ExitCode == 0 && File.Exists(Path.Combine(cacheDir, "texIndex.json"));
    }
}
