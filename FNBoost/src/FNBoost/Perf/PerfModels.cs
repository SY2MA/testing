using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using FNBoost.Core;

// CONTRATTO CONDIVISO del modulo Prestazioni.
// Questo file non dipende da WPF: viene compilato anche dal progetto di test su Linux.

namespace FNBoost.Perf
{
    /// <summary>Quale processo misurare.</summary>
    public enum PerfTarget
    {
        /// <summary>Solo FortniteClient-Win64-Shipping.exe.</summary>
        Fortnite,
        /// <summary>Qualsiasi applicazione DirectX in primo piano.</summary>
        Foreground
    }

    public enum CaptureStatus { Stopped, WaitingForGame, Capturing, Error }

    public enum OverlayCorner { TopLeft, TopRight, BottomLeft, BottomRight }

    /// <summary>Risultato statistico calcolato da una serie di frametime (ms).</summary>
    public sealed class FrameStatsResult
    {
        public int Frames { get; set; }
        public double DurationSec { get; set; }
        /// <summary>FPS medi = frame / tempo totale (non la media degli FPS istantanei).</summary>
        public double AvgFps { get; set; }
        /// <summary>1% low: FPS calcolati dalla media dell'1% dei frametime più lunghi.</summary>
        public double Low1Fps { get; set; }
        /// <summary>0,1% low: FPS calcolati dalla media dello 0,1% dei frametime più lunghi.</summary>
        public double Low01Fps { get; set; }
        /// <summary>P1: FPS al 99° percentile dei frametime (1000 / P99 frametime).</summary>
        public double P1Fps { get; set; }
        /// <summary>FPS minimi = 1000 / frametime massimo.</summary>
        public double MinFps { get; set; }
        /// <summary>FPS massimi = 1000 / frametime minimo.</summary>
        public double MaxFps { get; set; }
        public double AvgFrametimeMs { get; set; }
        public double MedianFrametimeMs { get; set; }
        public double P99FrametimeMs { get; set; }
        public double MaxFrametimeMs { get; set; }
        public double StdDevFrametimeMs { get; set; }
        /// <summary>Frame che durano molto più dei vicini (vedi FrameStats.IsStutter).</summary>
        public int Stutters { get; set; }
        public double StuttersPerMin { get; set; }
        /// <summary>0-100: quanto i frametime sono regolari (100 = perfettamente costanti).</summary>
        public double ConsistencyScore { get; set; }

        [JsonIgnore] public bool HasData => Frames >= 2;
    }

    /// <summary>Un campione al secondo durante una sessione registrata.</summary>
    public sealed class SecondSample
    {
        /// <summary>Secondi dall'inizio della sessione.</summary>
        public double T { get; set; }
        public double Fps { get; set; }
        public double Low1Fps { get; set; }
        public double MaxFrametimeMs { get; set; }
        public int Stutters { get; set; }
        public double CpuPercent { get; set; }
        public double? GpuPercent { get; set; }
        public double RamPercent { get; set; }
        public double? VramUsedGb { get; set; }
    }

    /// <summary>Una sessione di gioco registrata (salvata in %LOCALAPPDATA%\FNBoost\sessions).</summary>
    public sealed class PerfSession
    {
        public string Id { get; set; } = "";
        public DateTime StartedAt { get; set; }
        public double DurationSec { get; set; }
        public string ProcessName { get; set; } = "";
        /// <summary>Etichetta scelta dall'utente (es. "dopo tweak", "DX12").</summary>
        public string Label { get; set; } = "";
        public string Notes { get; set; } = "";
        public FrameStatsResult Stats { get; set; } = new();
        public List<SecondSample> Seconds { get; set; } = new();
        /// <summary>Id dei tweak FN Boost attivi all'inizio della sessione.</summary>
        public List<string> ActiveTweaks { get; set; } = new();
        /// <summary>Modalità di rendering letta da GameUserSettings.ini (se disponibile).</summary>
        public string? RenderMode { get; set; }
        public double? FpsCap { get; set; }
        public int? RefreshHz { get; set; }
        public double? VramTotalGb { get; set; }

        [JsonIgnore] public string Title => string.IsNullOrWhiteSpace(Label) ? $"{StartedAt:dd/MM HH:mm}" : $"{StartedAt:dd/MM HH:mm} · {Label}";
        [JsonIgnore] public string DurationText => TimeSpan.FromSeconds(DurationSec).ToString(DurationSec >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    }

    /// <summary>Una osservazione dell'analisi automatica.</summary>
    public sealed class PerfInsight
    {
        public CheckStatus Severity { get; set; }
        public string Title { get; set; } = "";
        public string Message { get; set; } = "";
        public string Hint { get; set; } = "";
    }

    /// <summary>Stato "dal vivo" pubblicato ~4 volte al secondo dal PerfService.</summary>
    public sealed class LiveSnapshot
    {
        public bool HasData { get; set; }
        public string? ProcessName { get; set; }
        public int? ProcessId { get; set; }
        /// <summary>FPS dell'ultimo secondo.</summary>
        public double CurrentFps { get; set; }
        public double LastFrametimeMs { get; set; }
        /// <summary>Statistiche sulla finestra mobile (PerfSettings.WindowSeconds).</summary>
        public FrameStatsResult Window { get; set; } = new();
        /// <summary>Ultimi frametime (ms) per il grafico, dal più vecchio al più recente.</summary>
        public float[] RecentFrametimes { get; set; } = Array.Empty<float>();
        /// <summary>FPS al secondo degli ultimi ~120 s (per il grafico).</summary>
        public double[] FpsHistory { get; set; } = Array.Empty<double>();
        public double CpuPercent { get; set; }
        public double? GpuPercent { get; set; }
        public double RamPercent { get; set; }
        public double? VramUsedGb { get; set; }
        public double? VramTotalGb { get; set; }
        public bool IsRecording { get; set; }
        public double RecordingSeconds { get; set; }
        public CaptureStatus Status { get; set; }
        public string StatusText { get; set; } = "";
    }

    /// <summary>Impostazioni del modulo Prestazioni (dentro AppSettings.Perf).</summary>
    public sealed class PerfSettings
    {
        /// <summary>Misura gli FPS in background tramite ETW (come PresentMon / Xbox Game Bar).</summary>
        public bool CaptureEnabled { get; set; } = true;
        public PerfTarget Target { get; set; } = PerfTarget.Fortnite;
        /// <summary>Durata della finestra mobile per le statistiche dal vivo.</summary>
        public int WindowSeconds { get; set; } = 30;
        /// <summary>Registra automaticamente una sessione quando Fortnite renderizza.</summary>
        public bool AutoRecord { get; set; } = true;
        /// <summary>Sessioni più corte di così non vengono salvate.</summary>
        public int MinSessionSeconds { get; set; } = 60;
        /// <summary>Un frame è "stutter" se dura più di StutterFactor × la mediana locale…</summary>
        public double StutterFactor { get; set; } = 2.5;
        /// <summary>…e più di questo valore assoluto (ms), per ignorare variazioni impercettibili ad altissimi FPS.</summary>
        public double StutterMinMs { get; set; } = 12;
        /// <summary>Numero massimo di sessioni conservate (le più vecchie vengono eliminate).</summary>
        public int MaxSessions { get; set; } = 100;
        public PerfOverlaySettings Overlay { get; set; } = new();
    }

    /// <summary>Impostazioni dell'overlay contatore FPS (finestra trasparente click-through).</summary>
    public sealed class PerfOverlaySettings : ObservableObject
    {
        private bool _enabled;
        private OverlayCorner _corner = OverlayCorner.TopLeft;
        private int _offsetX = 12;
        private int _offsetY = 12;
        private double _fontSize = 14;
        private double _backgroundOpacity = 0.55;
        private string _textColor = "#FFFFFF";
        private string _accentColor = "#00E5FF";
        private bool _showFps = true;
        private bool _showAvg = true;
        private bool _show1Low = true;
        private bool _show01Low = true;
        private bool _showMinMax = true;
        private bool _showFrametime = true;
        private bool _showCpuGpu = true;
        private bool _showGraph = true;
        private bool _compact;
        private string _monitor = "";
        private bool _onlyWhenGameFocused = true;

        /// <summary>Non salvato: all'avvio l'overlay parte sempre spento.</summary>
        [JsonIgnore] public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
        public OverlayCorner Corner { get => _corner; set => Set(ref _corner, value); }
        public int OffsetX { get => _offsetX; set => Set(ref _offsetX, Math.Clamp(value, 0, 2000)); }
        public int OffsetY { get => _offsetY; set => Set(ref _offsetY, Math.Clamp(value, 0, 2000)); }
        public double FontSize { get => _fontSize; set => Set(ref _fontSize, Math.Clamp(value, 9, 32)); }
        public double BackgroundOpacity { get => _backgroundOpacity; set => Set(ref _backgroundOpacity, Math.Clamp(value, 0, 1)); }
        public string TextColor { get => _textColor; set => Set(ref _textColor, value ?? "#FFFFFF"); }
        public string AccentColor { get => _accentColor; set => Set(ref _accentColor, value ?? "#00E5FF"); }
        public bool ShowFps { get => _showFps; set => Set(ref _showFps, value); }
        public bool ShowAvg { get => _showAvg; set => Set(ref _showAvg, value); }
        public bool Show1Low { get => _show1Low; set => Set(ref _show1Low, value); }
        public bool Show01Low { get => _show01Low; set => Set(ref _show01Low, value); }
        public bool ShowMinMax { get => _showMinMax; set => Set(ref _showMinMax, value); }
        public bool ShowFrametime { get => _showFrametime; set => Set(ref _showFrametime, value); }
        public bool ShowCpuGpu { get => _showCpuGpu; set => Set(ref _showCpuGpu, value); }
        public bool ShowGraph { get => _showGraph; set => Set(ref _showGraph, value); }
        /// <summary>Layout su una riga sola.</summary>
        public bool Compact { get => _compact; set => Set(ref _compact, value); }
        /// <summary>Nome dispositivo del monitor (vuoto = principale).</summary>
        public string Monitor { get => _monitor; set => Set(ref _monitor, value ?? ""); }
        public bool OnlyWhenGameFocused { get => _onlyWhenGameFocused; set => Set(ref _onlyWhenGameFocused, value); }
    }
}
