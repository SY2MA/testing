using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FNBoost.Core
{
    /// <summary>
    /// Trova installazione e configurazione di Fortnite leggendo solo file pubblici
    /// (il manifest del Launcher Epic). Non apre né ispeziona mai il processo del gioco.
    /// </summary>
    public static class FortniteLocator
    {
        public const string ClientProcessName = "FortniteClient-Win64-Shipping";
        public const string ClientExeName = ClientProcessName + ".exe";

        public static string ConfigFile => Path.Combine(AppPaths.LocalAppData,
            "FortniteGame", "Saved", "Config", "WindowsClient", "GameUserSettings.ini");

        public static string SavedDir => Path.Combine(AppPaths.LocalAppData, "FortniteGame", "Saved");

        private static string? _cachedInstall;
        private static DateTime _cachedAt;

        public static string? InstallDir
        {
            get
            {
                if (_cachedInstall != null && DateTime.Now - _cachedAt < TimeSpan.FromSeconds(30))
                    return _cachedInstall;
                _cachedInstall = FindInstallDir();
                _cachedAt = DateTime.Now;
                return _cachedInstall;
            }
        }

        public static string? ClientExe
        {
            get
            {
                var dir = InstallDir;
                if (dir == null) return null;
                var exe = Path.Combine(dir, "FortniteGame", "Binaries", "Win64", ClientExeName);
                return File.Exists(exe) ? exe : null;
            }
        }

        private static string? FindInstallDir()
        {
            try
            {
                var manifest = Path.Combine(AppPaths.ProgramData, "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
                if (File.Exists(manifest))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                    if (doc.RootElement.TryGetProperty("InstallationList", out var list))
                    {
                        foreach (var item in list.EnumerateArray())
                        {
                            var app = item.TryGetProperty("AppName", out var a) ? a.GetString() : null;
                            var loc = item.TryGetProperty("InstallLocation", out var l) ? l.GetString() : null;
                            if (string.Equals(app, "Fortnite", StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrWhiteSpace(loc) && Directory.Exists(loc))
                                return loc;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Lettura LauncherInstalled.dat non riuscita: " + ex.Message);
            }

            var candidates = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .SelectMany(d => new[]
                {
                    Path.Combine(d.RootDirectory.FullName, "Program Files", "Epic Games", "Fortnite"),
                    Path.Combine(d.RootDirectory.FullName, "Epic Games", "Fortnite"),
                    Path.Combine(d.RootDirectory.FullName, "Games", "Epic Games", "Fortnite")
                });
            return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "FortniteGame", "Binaries", "Win64", ClientExeName)));
        }

        /// <summary>Controlla se il gioco è aperto (solo elenco dei nomi dei processi, nessun handle).</summary>
        public static bool IsRunning()
        {
            try
            {
                var procs = Process.GetProcessesByName(ClientProcessName);
                var running = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
                return running;
            }
            catch
            {
                return false;
            }
        }

        public static bool IsLauncherRunning()
        {
            try
            {
                var procs = Process.GetProcessesByName("EpicGamesLauncher");
                var running = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
                return running;
            }
            catch
            {
                return false;
            }
        }
    }
}
