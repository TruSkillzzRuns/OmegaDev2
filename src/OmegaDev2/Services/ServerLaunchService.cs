using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OmegaDev2.Services;

// Native replacement for StartServer.bat/StartServer_v48.bat/StartServer_v53.bat
// and StartClient.bat/StartClient_v48.bat/StartClient_v53.bat -- those scripts
// are personal, gitignored, and hardcode one machine's paths, so the
// "Server Control" buttons only ever worked for the one person who wrote
// them. This does the exact same steps (port-patch Config.ini, launch,
// track PID / launch client with the right siteconfig) directly in-process,
// driven by paths the user picks once and that persist across app restarts.
public static class ServerLaunchService
{
    public sealed class LaunchPaths
    {
        public string? RepoDir { get; set; }
        public string? ClientPath48 { get; set; }
        public string? ClientPath52 { get; set; }
        public string? ClientPath53 { get; set; }
    }

    private static string SettingsFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmegaDev2", "serverlaunch.json");

    public static LaunchPaths LoadPaths()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
                return JsonSerializer.Deserialize<LaunchPaths>(File.ReadAllText(SettingsFilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public static void SavePaths(LaunchPaths paths)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);
            File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(paths, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public sealed class LaunchResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
    }

    // versionTag: "v48" | "v52" | "v53" -- same tags used throughout this app
    // (AccountMigrationPage's ServerVersions, PidFileRelativePathFor, etc).

    private static string BuildDirFor(string repoDir, string versionTag) => versionTag switch
    {
        "v48" => Path.Combine(repoDir, "build", "v48"),
        "v53" => Path.Combine(repoDir, "build", "v53"),
        "v52" => Path.Combine(repoDir, "src", "MHServerEmu", "bin", "x64", "Release", "net8.0"),
        _ => throw new ArgumentOutOfRangeException(nameof(versionTag)),
    };

    // (gamePort, webPort) -- matches the port-patch each StartServer script
    // applies so 1.48/1.53 can run alongside 1.52 without a port conflict.
    // 1.52 keeps the project defaults (4306/8080), so nothing to patch there.
    private static (int gamePort, int webPort)? PortsFor(string versionTag) => versionTag switch
    {
        "v48" => (4307, 8081),
        "v53" => (4308, 8082),
        "v52" => null,
        _ => throw new ArgumentOutOfRangeException(nameof(versionTag)),
    };

    private static string PidFilePathFor(string repoDir, string versionTag) =>
        Path.Combine(BuildDirFor(repoDir, versionTag), "server.pid");

    public static bool IsServerRunning(string repoDir, string versionTag)
    {
        string pidFile = PidFilePathFor(repoDir, versionTag);
        if (File.Exists(pidFile) == false) return false;

        try
        {
            string text = File.ReadAllText(pidFile).Trim();
            if (int.TryParse(text, out int pid) == false) return false;
            using Process proc = Process.GetProcessById(pid);
            return proc.HasExited == false;
        }
        catch
        {
            return false; // stale/unreadable PID file -- treat as not running
        }
    }

    public static LaunchResult StartServer(string repoDir, string versionTag)
    {
        if (string.IsNullOrWhiteSpace(repoDir) || Directory.Exists(repoDir) == false)
            return new LaunchResult { Ok = false, Message = $"MHServerEmu folder not found: {repoDir}" };

        string buildDir = BuildDirFor(repoDir, versionTag);
        string exePath = Path.Combine(buildDir, "MHServerEmu.exe");
        string configPath = Path.Combine(buildDir, "Config.ini");

        if (File.Exists(exePath) == false)
            return new LaunchResult { Ok = false, Message = $"Server exe not found at {exePath} -- build this version first." };
        if (File.Exists(configPath) == false)
            return new LaunchResult { Ok = false, Message = $"Config.ini not found at {configPath} -- build this version first." };

        // Idempotent port patch, same regex logic as the .bat scripts --
        // safe to run every launch, including after a rebuild overwrites
        // Config.ini back to the project defaults.
        var ports = PortsFor(versionTag);
        if (ports != null)
        {
            try
            {
                string text = File.ReadAllText(configPath);
                text = Regex.Replace(text, @"(?m)^Port=4306\r?$", $"Port={ports.Value.gamePort}");
                text = Regex.Replace(text, @"(?m)^Port=8080\r?$", $"Port={ports.Value.webPort}");
                File.WriteAllText(configPath, text);
            }
            catch (Exception ex)
            {
                return new LaunchResult { Ok = false, Message = $"Failed to patch Config.ini ports: {ex.Message}" };
            }
        }

        string apacheStatus = EnsureApacheRunning(repoDir);

        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = buildDir,
                UseShellExecute = true,
            });

            if (proc == null)
                return new LaunchResult { Ok = false, Message = "Process.Start returned null." };

            // Same file/format the .bat scripts write, so IsServerRunning
            // (and anyone still using the .bat scripts by hand) stays consistent.
            File.WriteAllText(PidFilePathFor(repoDir, versionTag), proc.Id.ToString());

            return new LaunchResult { Ok = true, Message = $"Started {versionTag} server (PID {proc.Id}). {apacheStatus}" };
        }
        catch (Exception ex)
        {
            return new LaunchResult { Ok = false, Message = $"Failed to start server: {ex.Message}" };
        }
    }

    // Stops the server for this version. Tries the PID file first (fast
    // path, written by StartServer above), then falls back to matching by
    // exact exe path across every running MHServerEmu.exe -- this catches
    // instances started any other way (manually, via the .bat scripts, or
    // a previous app session) that this session's PID file doesn't know
    // about, which a PID-file-only stop would silently miss (same fix
    // applied to StopServer*.bat).
    public static LaunchResult StopServer(string repoDir, string versionTag)
    {
        if (string.IsNullOrWhiteSpace(repoDir) || Directory.Exists(repoDir) == false)
            return new LaunchResult { Ok = false, Message = $"MHServerEmu folder not found: {repoDir}" };

        string exePath = Path.Combine(BuildDirFor(repoDir, versionTag), "MHServerEmu.exe");
        string pidFile = PidFilePathFor(repoDir, versionTag);
        var stoppedPids = new System.Collections.Generic.List<int>();

        if (File.Exists(pidFile))
        {
            try
            {
                if (int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid))
                {
                    using Process proc = Process.GetProcessById(pid);
                    if (proc.HasExited == false && string.Equals(proc.ProcessName, "MHServerEmu", StringComparison.OrdinalIgnoreCase))
                    {
                        proc.Kill();
                        stoppedPids.Add(pid);
                    }
                }
            }
            catch { /* stale/unreadable PID -- fall through to exe-path match */ }
            try { File.Delete(pidFile); } catch { }
        }

        foreach (Process proc in Process.GetProcessesByName("MHServerEmu"))
        {
            try
            {
                if (stoppedPids.Contains(proc.Id)) continue;
                if (string.Equals(proc.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase) == false) continue;
                proc.Kill();
                stoppedPids.Add(proc.Id);
            }
            catch { /* access denied / already exited -- skip */ }
            finally { proc.Dispose(); }
        }

        return stoppedPids.Count > 0
            ? new LaunchResult { Ok = true, Message = $"Stopped {versionTag} server (PID {string.Join(", ", stoppedPids)})." }
            : new LaunchResult { Ok = true, Message = $"No {versionTag} MHServerEmu instance is running." };
    }

    private static string SiteConfigNameFor(string versionTag) => versionTag switch
    {
        "v48" => "SiteConfig_v48.xml",
        "v53" => "SiteConfig_v53.xml",
        "v52" => "SiteConfig.xml",
        _ => throw new ArgumentOutOfRangeException(nameof(versionTag)),
    };

    public static LaunchResult LaunchClient(string clientExePath, string versionTag)
    {
        if (string.IsNullOrWhiteSpace(clientExePath))
            return new LaunchResult { Ok = false, Message = $"No client path configured for {versionTag} -- set it above first." };
        if (File.Exists(clientExePath) == false)
            return new LaunchResult { Ok = false, Message = $"Client exe not found at {clientExePath}." };

        string siteConfig = SiteConfigNameFor(versionTag);
        string args = $"-robocopy -nosteam -nostartupmovies -nointrocinematic -siteconfigurl=localhost/{siteConfig}";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = clientExePath,
                Arguments = args,
                UseShellExecute = true,
            });
            return new LaunchResult { Ok = true, Message = $"Launched {versionTag} client." };
        }
        catch (Exception ex)
        {
            return new LaunchResult { Ok = false, Message = $"Failed to launch client: {ex.Message}" };
        }
    }

    // Apache serves SiteConfig*.xml on port 80 and proxies /AuthServer to each
    // server's WebFrontend -- shared single instance regardless of which
    // server version(s) are running. Best-effort: if Apache isn't set up
    // under the repo folder, skip without failing the server launch over it.
    // Every branch now returns a status string -- this used to be totally
    // silent (no branch fed into the returned LaunchResult message), so
    // "did Apache actually start" was invisible from the UI even though the
    // logic was already correct.
    private static string EnsureApacheRunning(string repoDir)
    {
        try
        {
            if (Process.GetProcessesByName("httpd").Length > 0)
                return "Apache already running.";

            string apacheRoot = Path.Combine(repoDir, "Apache24");
            string apacheExe = Path.Combine(apacheRoot, "bin", "httpd.exe");
            if (File.Exists(apacheExe) == false)
                return $"Apache not found at {apacheExe} -- skipped.";

            // UseShellExecute must be false to set EnvironmentVariables on
            // Windows (throws InvalidOperationException otherwise).
            var psi = new ProcessStartInfo
            {
                FileName = apacheExe,
                WorkingDirectory = Path.Combine(apacheRoot, "bin"),
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Minimized,
            };
            psi.EnvironmentVariables["APACHE_SERVER_ROOT"] = apacheRoot;
            var proc = Process.Start(psi);
            return proc != null ? "Apache started." : "Apache failed to start (Process.Start returned null).";
        }
        catch (Exception ex)
        {
            // Best-effort only -- a server without Apache still runs, it just
            // means the client can't fetch SiteConfig, which the client-launch
            // step (or a manual retry) will surface on its own.
            return $"Apache failed to start: {ex.Message}";
        }
    }
}
