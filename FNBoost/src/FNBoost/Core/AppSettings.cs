using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FNBoost.Crosshair;
using FNBoost.Perf;

namespace FNBoost.Core
{
    public sealed class HotkeySetting
    {
        public bool Ctrl { get; set; } = true;
        public bool Alt { get; set; } = true;
        public bool Shift { get; set; }
        /// <summary>Nome del tasto (System.Windows.Input.Key), es. "X".</summary>
        public string Key { get; set; } = "X";
        /// <summary>false = scorciatoia disattivata.</summary>
        public bool Enabled { get; set; } = true;

        public override string ToString() => !Enabled ? "(disattivata)" :
            $"{(Ctrl ? "Ctrl+" : "")}{(Alt ? "Alt+" : "")}{(Shift ? "Shift+" : "")}{Key}";
    }

    public sealed class AppSettings
    {
        public CrosshairSettings Crosshair { get; set; } = new();
        public List<CrosshairPreset> CrosshairPresets { get; set; } = new();

        public HotkeySetting HotkeyCrosshair { get; set; } = new() { Key = "X" };
        public HotkeySetting HotkeyPanel { get; set; } = new() { Key = "Z" };
        /// <summary>Mostra/nasconde l'overlay contatore FPS.</summary>
        public HotkeySetting HotkeyOverlay { get; set; } = new() { Key = "F" };
        /// <summary>Passa al preset di mirino successivo.</summary>
        public HotkeySetting HotkeyNextPreset { get; set; } = new() { Key = "C" };
        /// <summary>Avvia/ferma la registrazione di una sessione di prestazioni.</summary>
        public HotkeySetting HotkeyRecord { get; set; } = new() { Key = "R" };

        /// <summary>Modulo Prestazioni (contatore FPS, overlay, sessioni).</summary>
        public PerfSettings Perf { get; set; } = new();

        public double FloatingLeft { get; set; } = double.NaN;
        public double FloatingTop { get; set; } = double.NaN;
        public double FloatingOpacity { get; set; } = 0.92;
        public bool FloatingTopmost { get; set; } = true;
        public bool StartFloating { get; set; }

        public bool CloseToTray { get; set; } = true;
        public bool AutoRestorePoint { get; set; } = true;
        public bool TrayHintShown { get; set; }

        private static readonly JsonSerializerOptions Opts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Opts);
                    if (s != null)
                    {
                        if (s.CrosshairPresets.Count == 0) s.CrosshairPresets = CrosshairPreset.BuiltIn();
                        s.Crosshair.Enabled = false; // all'avvio il mirino parte sempre spento
                        return s;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Lettura impostazioni", ex);
            }
            return new AppSettings { CrosshairPresets = CrosshairPreset.BuiltIn() };
        }

        public void Save()
        {
            try
            {
                AppPaths.Ensure();
                var tmp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
                File.Move(tmp, AppPaths.SettingsFile, true);
            }
            catch (Exception ex)
            {
                Log.Error("Salvataggio impostazioni", ex);
            }
        }
    }
}
