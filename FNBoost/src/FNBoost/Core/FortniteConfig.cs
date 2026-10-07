using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace FNBoost.Core
{
    public enum RenderMode { Unknown, DirectX12, Performance }
    public enum QualityPreset { Keep, Competitive, Balanced }

    /// <summary>
    /// Lettura/scrittura delle impostazioni grafiche di Fortnite in GameUserSettings.ini.
    /// È il file che il gioco stesso salva quando cambi le opzioni nel menu: modificarlo
    /// a gioco chiuso è l'equivalente di cambiare le impostazioni in partita (Epic stessa
    /// indica di modificarlo o eliminarlo nelle sue guide di supporto).
    /// </summary>
    public sealed class FortniteSettings
    {
        public const string Fgs = "/Script/FortniteGame.FortGameUserSettings";
        public const string Rhi = "D3DRHIPreference";
        public const string Perf = "PerformanceMode";
        public const string Sg = "ScalabilityGroups";
        public const string Rt = "RayTracing";

        public RenderMode RenderMode { get; set; }
        public string RawRhi { get; set; } = "";
        /// <summary>Valori grezzi di [D3DRHIPreference]: PreferredRHI (es. "dx11", "dx12") e PreferredFeatureLevel (es. "es31", "sm6").</summary>
        public string PreferredRhi { get; set; } = "";
        public string PreferredFeatureLevel { get; set; } = "";
        public double FrameRateLimit { get; set; }
        public bool VSync { get; set; }
        public int Reflex { get; set; }
        public bool MotionBlur { get; set; }
        public bool ShowFps { get; set; }
        public bool RayTracing { get; set; }
        public bool Nanite { get; set; }
        public bool ShowGrass { get; set; }
        public int MeshQuality { get; set; }
        public int WindowMode { get; set; }
        public int ResolutionX { get; set; }
        public int ResolutionY { get; set; }
        public QualityPreset Preset { get; set; } = QualityPreset.Keep;
        public Dictionary<string, string> Scalability { get; } = new();

        private static bool Bool(string? v, bool def) =>
            v == null ? def : v.Equals("True", StringComparison.OrdinalIgnoreCase) || v == "1";

        private static int Int(string? v, int def) =>
            int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : def;

        private static string B(bool v) => v ? "True" : "False";

        public static FortniteSettings Read(string path)
        {
            var ini = IniDocument.Load(path);
            var s = new FortniteSettings();
            var rhi = ini.Get(Rhi, "PreferredRHI") ?? "";
            var level = ini.Get(Rhi, "PreferredFeatureLevel") ?? "";
            s.RawRhi = $"{rhi}/{level}";
            s.PreferredRhi = rhi;
            s.PreferredFeatureLevel = level;
            s.RenderMode = level.Equals("es31", StringComparison.OrdinalIgnoreCase) ? RenderMode.Performance
                         : rhi.Equals("dx12", StringComparison.OrdinalIgnoreCase) ? RenderMode.DirectX12
                         : RenderMode.Unknown;
            s.FrameRateLimit = double.TryParse(ini.Get(Fgs, "FrameRateLimit"), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0;
            s.VSync = Bool(ini.Get(Fgs, "bUseVSync"), false);
            s.Reflex = Int(ini.Get(Fgs, "LatencyTweak2"), 0);
            s.MotionBlur = Bool(ini.Get(Fgs, "bMotionBlur"), false);
            s.ShowFps = Bool(ini.Get(Fgs, "bShowFPS"), false);
            s.RayTracing = Bool(ini.Get(Fgs, "bRayTracing"), false);
            s.Nanite = Bool(ini.Get(Fgs, "bUseNanite"), false);
            s.ShowGrass = Bool(ini.Get(Fgs, "bShowGrass"), true);
            s.MeshQuality = Int(ini.Get(Perf, "MeshQuality"), 0);
            s.WindowMode = Int(ini.Get(Fgs, "FullscreenMode"), 0);
            s.ResolutionX = Int(ini.Get(Fgs, "ResolutionSizeX"), 0);
            s.ResolutionY = Int(ini.Get(Fgs, "ResolutionSizeY"), 0);
            foreach (var key in ScalabilityKeys)
            {
                var v = ini.Get(Sg, key);
                if (v != null) s.Scalability[key] = v;
            }
            return s;
        }

        public static readonly string[] ScalabilityKeys =
        {
            "sg.ResolutionQuality", "sg.ViewDistanceQuality", "sg.AntiAliasingQuality", "sg.ShadowQuality",
            "sg.GlobalIlluminationQuality", "sg.ReflectionQuality", "sg.PostProcessQuality", "sg.TextureQuality",
            "sg.EffectsQuality", "sg.FoliageQuality", "sg.ShadingQuality", "sg.LandscapeQuality"
        };

        /// <summary>Tutto basso tranne la distanza visiva (visibilità dei nemici) e risoluzione 3D al 100%.</summary>
        private static readonly Dictionary<string, string> CompetitiveSg = new()
        {
            ["sg.ResolutionQuality"] = "100",
            ["sg.ViewDistanceQuality"] = "2",
            ["sg.AntiAliasingQuality"] = "0",
            ["sg.ShadowQuality"] = "0",
            ["sg.GlobalIlluminationQuality"] = "0",
            ["sg.ReflectionQuality"] = "0",
            ["sg.PostProcessQuality"] = "0",
            ["sg.TextureQuality"] = "1",
            ["sg.EffectsQuality"] = "0",
            ["sg.FoliageQuality"] = "0",
            ["sg.ShadingQuality"] = "0",
            ["sg.LandscapeQuality"] = "0",
        };

        private static readonly Dictionary<string, string> BalancedSg = new()
        {
            ["sg.ResolutionQuality"] = "100",
            ["sg.ViewDistanceQuality"] = "3",
            ["sg.AntiAliasingQuality"] = "2",
            ["sg.ShadowQuality"] = "1",
            ["sg.GlobalIlluminationQuality"] = "1",
            ["sg.ReflectionQuality"] = "1",
            ["sg.PostProcessQuality"] = "1",
            ["sg.TextureQuality"] = "2",
            ["sg.EffectsQuality"] = "1",
            ["sg.FoliageQuality"] = "1",
            ["sg.ShadingQuality"] = "2",
            ["sg.LandscapeQuality"] = "2",
        };

        public void Write(string path)
        {
            var ini = IniDocument.Load(path);
            switch (RenderMode)
            {
                case RenderMode.Performance:
                    // Gli stessi valori che scrive il gioco: la modalità Prestazioni gira su Direct3D 11 con feature
                    // level ES3_1 (nel log: "RHI D3D11 with Feature Level ES3_1 is supported and will be used").
                    ini.Set(Rhi, "PreferredRHI", "dx11");
                    ini.Set(Rhi, "PreferredFeatureLevel", "es31");
                    break;
                case RenderMode.DirectX12:
                    ini.Set(Rhi, "PreferredRHI", "dx12");
                    ini.Set(Rhi, "PreferredFeatureLevel", "sm6");
                    break;
            }
            ini.Set(Fgs, "FrameRateLimit", FrameRateLimit.ToString("0.000000", CultureInfo.InvariantCulture));
            ini.Set(Fgs, "bUseVSync", B(VSync));
            ini.Set(Fgs, "LatencyTweak2", Math.Clamp(Reflex, 0, 2).ToString(CultureInfo.InvariantCulture));
            ini.Set(Fgs, "bMotionBlur", B(MotionBlur));
            ini.Set(Fgs, "bShowFPS", B(ShowFps));
            ini.Set(Fgs, "bRayTracing", B(RayTracing));
            ini.Set(Rt, "r.RayTracing.EnableInGame", B(RayTracing));
            ini.Set(Fgs, "bUseNanite", B(Nanite));
            ini.Set(Fgs, "bShowGrass", B(ShowGrass));
            ini.Set(Perf, "MeshQuality", Math.Clamp(MeshQuality, 0, 2).ToString(CultureInfo.InvariantCulture));
            var wm = Math.Clamp(WindowMode, 0, 2).ToString(CultureInfo.InvariantCulture);
            ini.Set(Fgs, "FullscreenMode", wm);
            ini.Set(Fgs, "PreferredFullscreenMode", wm);
            ini.Set(Fgs, "LastConfirmedFullscreenMode", wm);

            var sg = Preset switch
            {
                QualityPreset.Competitive => CompetitiveSg,
                QualityPreset.Balanced => BalancedSg,
                _ => null
            };
            if (sg != null)
                foreach (var kv in sg) ini.Set(Sg, kv.Key, kv.Value);

            ini.Save(path);
        }

        public string ScalabilitySummary =>
            Scalability.Count == 0 ? "(nessun valore)" :
            string.Join("  ", Scalability.Select(kv => $"{kv.Key.Replace("sg.", "").Replace("Quality", "")}={kv.Value}"));
    }

    public static class FortniteConfigService
    {
        public static bool ConfigExists => File.Exists(FortniteLocator.ConfigFile);

        public static string Backup(string reason)
        {
            AppPaths.Ensure();
            var dest = Path.Combine(AppPaths.IniBackupDir, $"GameUserSettings_{DateTime.Now:yyyyMMdd_HHmmss}_{reason}.ini");
            File.Copy(FortniteLocator.ConfigFile, dest, true);
            Log.Info("Backup config Fortnite: " + Path.GetFileName(dest));
            return dest;
        }

        public static IReadOnlyList<FileInfo> ListBackups()
        {
            if (!Directory.Exists(AppPaths.IniBackupDir)) return Array.Empty<FileInfo>();
            return new DirectoryInfo(AppPaths.IniBackupDir).GetFiles("GameUserSettings_*.ini")
                .OrderByDescending(f => f.LastWriteTime).ToList();
        }

        public static void RestoreBackup(FileInfo backup)
        {
            if (FortniteLocator.IsRunning())
                throw new InvalidOperationException("Chiudi Fortnite prima di ripristinare la configurazione.");
            Backup("prima-del-ripristino");
            File.Copy(backup.FullName, FortniteLocator.ConfigFile, true);
            Log.Info("Config Fortnite ripristinata da " + backup.Name);
        }

        public static void Save(FortniteSettings settings)
        {
            if (FortniteLocator.IsRunning())
                throw new InvalidOperationException("Chiudi Fortnite: il gioco riscrive il file all'uscita e annullerebbe le modifiche.");
            if (!ConfigExists)
                throw new FileNotFoundException("GameUserSettings.ini non trovato. Avvia Fortnite almeno una volta.");
            var attrs = File.GetAttributes(FortniteLocator.ConfigFile);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(FortniteLocator.ConfigFile, attrs & ~FileAttributes.ReadOnly);
            Backup("auto");
            settings.Write(FortniteLocator.ConfigFile);
            Log.Info("Impostazioni Fortnite salvate.");
        }
    }
}
