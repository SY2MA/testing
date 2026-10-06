using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace FNBoost.Core
{
    public sealed class CleanupTarget : ObservableObject
    {
        private bool _selected;
        private long _bytes = -1;
        private int _files;

        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public string? Warning { get; init; }
        public Func<IEnumerable<string>> Folders { get; init; } = () => Array.Empty<string>();
        public TimeSpan MinAge { get; init; } = TimeSpan.Zero;
        public bool RequiresFortniteClosed { get; init; }
        public bool RequiresLauncherClosed { get; init; }

        public bool Selected { get => _selected; set => Set(ref _selected, value); }

        public long Bytes
        {
            get => _bytes;
            set { if (Set(ref _bytes, value)) OnPropertyChanged(nameof(SizeText)); }
        }

        public int Files
        {
            get => _files;
            set { if (Set(ref _files, value)) OnPropertyChanged(nameof(SizeText)); }
        }

        public bool HasWarning => !string.IsNullOrEmpty(Warning);
        public string SizeText => Bytes < 0 ? "—" : $"{Cleaner.FormatBytes(Bytes)} · {Files} file";
    }

    public static class Cleaner
    {
        public static List<CleanupTarget> CreateTargets()
        {
            string L(params string[] p) => Path.Combine(new[] { AppPaths.LocalAppData }.Concat(p).ToArray());
            var fnSaved = FortniteLocator.SavedDir;

            return new List<CleanupTarget>
            {
                new()
                {
                    Name = "File temporanei utente",
                    Description = "%TEMP% (file più vecchi di 24 ore).",
                    Folders = () => new[] { Path.GetTempPath() },
                    MinAge = TimeSpan.FromHours(24),
                    Selected = true
                },
                new()
                {
                    Name = "File temporanei di Windows",
                    Description = @"C:\Windows\Temp (file più vecchi di 24 ore).",
                    Folders = () => new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") },
                    MinAge = TimeSpan.FromHours(24),
                    Selected = true
                },
                new()
                {
                    Name = "Log di Fortnite",
                    Description = @"FortniteGame\Saved\Logs: log delle sessioni precedenti.",
                    Folders = () => new[] { Path.Combine(fnSaved, "Logs") },
                    RequiresFortniteClosed = true,
                    Selected = true
                },
                new()
                {
                    Name = "Crash report di Fortnite / Unreal",
                    Description = @"FortniteGame\Saved\Crashes e CrashReportClient.",
                    Folders = () => new[] { Path.Combine(fnSaved, "Crashes"), L("CrashReportClient") },
                    RequiresFortniteClosed = true,
                    Selected = true
                },
                new()
                {
                    Name = "Segnalazioni errori di Windows",
                    Description = @"ProgramData\Microsoft\Windows\WER (ReportArchive, ReportQueue).",
                    Folders = () => new[]
                    {
                        Path.Combine(AppPaths.ProgramData, "Microsoft", "Windows", "WER", "ReportArchive"),
                        Path.Combine(AppPaths.ProgramData, "Microsoft", "Windows", "WER", "ReportQueue")
                    },
                    Selected = true
                },
                new()
                {
                    Name = "Cache web dell'Epic Games Launcher",
                    Description = @"EpicGamesLauncher\Saved\webcache*: risolve launcher lento o pagine bianche.",
                    Folders = () =>
                    {
                        var saved = L("EpicGamesLauncher", "Saved");
                        return Directory.Exists(saved)
                            ? Directory.GetDirectories(saved, "webcache*")
                            : Array.Empty<string>();
                    },
                    RequiresLauncherClosed = true,
                    Selected = false
                },
                new()
                {
                    Name = "Cache shader DirectX / GPU",
                    Description = @"D3DSCache, NVIDIA DXCache/GLCache, AMD DxCache/DxcCache.",
                    Warning = "Da usare SOLO se hai stutter continui dopo un aggiornamento dei driver (lo consiglia anche il supporto Epic). Dopo la pulizia le prime partite ricompilano gli shader e scattano di più.",
                    Folders = () => new[]
                    {
                        L("D3DSCache"),
                        L("NVIDIA", "DXCache"),
                        L("NVIDIA", "GLCache"),
                        L("AMD", "DxCache"),
                        L("AMD", "DxcCache"),
                    },
                    RequiresFortniteClosed = true,
                    Selected = false
                },
            };
        }

        public static void Analyze(CleanupTarget t, CancellationToken ct)
        {
            long bytes = 0;
            int files = 0;
            foreach (var f in Enumerate(t, ct))
            {
                bytes += f.Length;
                files++;
            }
            t.Bytes = bytes;
            t.Files = files;
        }

        public static (long freed, int deleted, int skipped) Clean(CleanupTarget t, CancellationToken ct)
        {
            if (t.RequiresFortniteClosed && FortniteLocator.IsRunning())
                throw new InvalidOperationException($"'{t.Name}': chiudi Fortnite prima di pulire.");
            if (t.RequiresLauncherClosed && FortniteLocator.IsLauncherRunning())
                throw new InvalidOperationException($"'{t.Name}': chiudi l'Epic Games Launcher prima di pulire.");

            long freed = 0;
            int deleted = 0, skipped = 0;
            foreach (var f in Enumerate(t, ct).ToList())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var len = f.Length;
                    if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly;
                    f.Delete();
                    freed += len;
                    deleted++;
                }
                catch
                {
                    skipped++; // file in uso: si salta, nessun problema
                }
            }
            foreach (var root in t.Folders().Where(Directory.Exists))
                RemoveEmptyDirs(root, isRoot: true);
            Log.Info($"Pulizia '{t.Name}': {deleted} file, {FormatBytes(freed)} liberati, {skipped} saltati.");
            return (freed, deleted, skipped);
        }

        private static IEnumerable<FileInfo> Enumerate(CleanupTarget t, CancellationToken ct)
        {
            var cutoff = DateTime.Now - t.MinAge;
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
            };
            foreach (var root in t.Folders())
            {
                if (!Directory.Exists(root) || IsDangerous(root)) continue;
                IEnumerable<FileInfo> files;
                try { files = new DirectoryInfo(root).EnumerateFiles("*", opts); }
                catch { continue; }
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (t.MinAge > TimeSpan.Zero && f.LastWriteTime > cutoff) continue;
                    yield return f;
                }
            }
        }

        /// <summary>Protezione: non lavora mai sulla radice di un disco o su cartelle di sistema intere.</summary>
        private static bool IsDangerous(string path)
        {
            var full = Path.GetFullPath(path).TrimEnd('\\');
            var root = Path.GetPathRoot(full)?.TrimEnd('\\');
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return true;
            var forbidden = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                AppPaths.LocalAppData,
                AppPaths.ProgramData,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            };
            return forbidden.Any(f => string.Equals(full, f.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        }

        private static void RemoveEmptyDirs(string dir, bool isRoot)
        {
            try
            {
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    var info = new DirectoryInfo(sub);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    RemoveEmptyDirs(sub, false);
                }
                if (!isRoot && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch
            {
                // cartella in uso: ignorata
            }
        }

        public static string FormatBytes(long bytes)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return $"{v:0.#} {u[i]}";
        }
    }
}
