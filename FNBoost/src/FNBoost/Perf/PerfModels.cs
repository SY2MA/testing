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

    /// <summary>
    /// Regione dei server Epic da usare come riferimento per il ping quando il server di gioco non
    /// risponde all'ICMP. Endpoint ufficiali Epic: ping-&lt;regione&gt;.ds.on.epicgames.com.
    /// </summary>
    public enum PingRegion { Auto, Europe, NaEast, NaCentral, NaWest, Brazil, Asia, Oceania, MiddleEast }

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

        // ---- Rete (null = non misurato) ----
        /// <summary>Ping verso il server di gioco (o la regione Epic se il server non risponde), ms.</summary>
        public double? PingMs { get; set; }
        public double? JitterMs { get; set; }
        /// <summary>Pacchetti ICMP persi verso il bersaglio del ping in questo secondo (0-100).</summary>
        public double? LossPct { get; set; }
        /// <summary>Ping verso il router (gateway predefinito): se è alto o instabile il problema è la rete di casa / Wi-Fi.</summary>
        public double? GatewayPingMs { get; set; }
        /// <summary>Pacchetti UDP al secondo ricevuti dal / inviati al server di gioco.</summary>
        public double? PacketsInPerSec { get; set; }
        public double? PacketsOutPerSec { get; set; }
        /// <summary>Banda usata dal gioco (kbit/s).</summary>
        public double? GameKbpsIn { get; set; }
        public double? GameKbpsOut { get; set; }
        /// <summary>Banda usata da tutte le altre app sulla stessa connessione (kbit/s, download + upload).</summary>
        public double? OtherAppsKbps { get; set; }
        /// <summary>Pausa più lunga tra due pacchetti ricevuti dal server in questo secondo (ms).</summary>
        public double? MaxRecvGapMs { get; set; }
        /// <summary>"Freeze" di rete iniziati in questo secondo (nessun pacchetto dal server per oltre 250 ms).</summary>
        public int NetFreezes { get; set; }
    }

    /// <summary>Statistiche di un bersaglio di ping (finestra mobile o intera sessione).</summary>
    public sealed class PingStats
    {
        /// <summary>Descrizione in italiano, es. "Server di gioco", "Regione Europa", "Router", "Internet (1.1.1.1)".</summary>
        public string Target { get; set; } = "";
        /// <summary>Host o IP pingato.</summary>
        public string Host { get; set; } = "";
        public double? LastMs { get; set; }
        public double? AvgMs { get; set; }
        public double? MinMs { get; set; }
        public double? MaxMs { get; set; }
        public double? P95Ms { get; set; }
        /// <summary>Jitter = media della differenza assoluta tra ping consecutivi (ms).</summary>
        public double? JitterMs { get; set; }
        public double LossPct { get; set; }
        public int Sent { get; set; }
        public int Received { get; set; }
    }

    /// <summary>Stato della rete dal vivo (dentro LiveSnapshot.Net).</summary>
    public sealed class NetworkSnapshot
    {
        public bool Available { get; set; }
        public string StatusText { get; set; } = "";
        /// <summary>Indirizzo IP:porta del server di gioco ricavato dal traffico UDP del gioco.</summary>
        public string? ServerEndpoint { get; set; }
        public string? RegionName { get; set; }
        /// <summary>Ping usato come "ping di gioco": server se risponde, altrimenti regione Epic.</summary>
        public PingStats? Game { get; set; }
        public PingStats? Region { get; set; }
        public PingStats? Gateway { get; set; }
        public PingStats? Internet { get; set; }
        /// <summary>"server" se il ping di gioco è misurato verso il server, "regione" se verso l'endpoint Epic.</summary>
        public string PingTargetKind { get; set; } = "";
        /// <summary>Regione Epic con il ping più basso nell'ultima scansione (per capire se il server è lontano).</summary>
        public string? BestRegionName { get; set; }
        public double? BestRegionPingMs { get; set; }
        /// <summary>
        /// true se la traccia ETW del traffico del gioco è attiva (serve l'amministratore): altrimenti pacchetti,
        /// banda e freeze qui sotto valgono 0 ma non sono misurati (i ping funzionano comunque).
        /// </summary>
        public bool TrafficAvailable { get; set; }
        public double PacketsInPerSec { get; set; }
        public double PacketsOutPerSec { get; set; }
        public double GameKbpsIn { get; set; }
        public double GameKbpsOut { get; set; }
        public double TotalKbpsIn { get; set; }
        public double TotalKbpsOut { get; set; }
        public double OtherAppsKbps { get; set; }
        public double MaxRecvGapMs { get; set; }
        /// <summary>Freeze di rete negli ultimi 60 s.</summary>
        public int RecentFreezes { get; set; }
        /// <summary>"Ethernet", "Wi-Fi", … dell'interfaccia usata per Internet.</summary>
        public string ConnectionType { get; set; } = "";
        public string AdapterName { get; set; } = "";
        public double? LinkSpeedMbps { get; set; }
        public int? WifiSignalPct { get; set; }
    }

    /// <summary>Riepilogo di rete di una sessione registrata.</summary>
    public sealed class NetworkSummary
    {
        public List<string> ServerEndpoints { get; set; } = new();
        public string? RegionName { get; set; }
        /// <summary>"server" se il ping è stato misurato verso il server di gioco, "regione" se verso l'endpoint Epic.</summary>
        public string PingTargetKind { get; set; } = "";
        public PingStats? Game { get; set; }
        public PingStats? Gateway { get; set; }
        public PingStats? Internet { get; set; }
        /// <summary>Ping verso l'endpoint Epic della regione usata (anche quando il server di gioco risponde).</summary>
        public PingStats? Region { get; set; }
        /// <summary>Regione Epic con il ping più basso (mediana di 3 ping) e il suo valore, se misurati.</summary>
        public string? BestRegionName { get; set; }
        public double? BestRegionPingMs { get; set; }
        public string ConnectionType { get; set; } = "";
        public double? LinkSpeedMbps { get; set; }
        public int? WifiSignalPct { get; set; }
        public double AvgPacketsInPerSec { get; set; }
        public double AvgPacketsOutPerSec { get; set; }
        public double AvgGameKbpsIn { get; set; }
        public double AvgGameKbpsOut { get; set; }
        public double AvgOtherAppsKbps { get; set; }
        public double MaxOtherAppsKbps { get; set; }
        public int Freezes { get; set; }
        public double LongestFreezeMs { get; set; }
    }

    /// <summary>Un processo che ha usato CPU/RAM durante la sessione (letto dai contatori PDH, senza aprire i processi).</summary>
    public sealed class ProcessUsage
    {
        public string Name { get; set; } = "";
        public double AvgCpuPct { get; set; }
        public double MaxCpuPct { get; set; }
        public double AvgRamMb { get; set; }
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
        /// <summary>Riepilogo rete (null nelle sessioni registrate prima della misura di rete).</summary>
        public NetworkSummary? Network { get; set; }
        /// <summary>Processi in background che hanno pesato di più durante la sessione (CPU media decrescente).</summary>
        public List<ProcessUsage> TopProcesses { get; set; } = new();

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
        /// <summary>Rete e ping (null se la misura di rete è disattivata).</summary>
        public NetworkSnapshot? Net { get; set; }
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
        /// <summary>Misura ping, jitter, perdita pacchetti e traffico del gioco (ETW Kernel-Network + ICMP).</summary>
        public bool NetCaptureEnabled { get; set; } = true;
        /// <summary>Regione Epic di riferimento per il ping (Auto = la più vicina).</summary>
        public PingRegion Region { get; set; } = PingRegion.Auto;
        /// <summary>Pinga anche il router per distinguere i problemi della rete di casa da quelli di Internet.</summary>
        public bool PingGateway { get; set; } = true;
        /// <summary>Registra i processi che usano più CPU/RAM durante le sessioni.</summary>
        public bool TrackProcesses { get; set; } = true;
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
        private bool _showNet = true;
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
        /// <summary>Riga "Ping · jitter · perdita" nell'overlay.</summary>
        public bool ShowNet { get => _showNet; set => Set(ref _showNet, value); }
        /// <summary>Layout su una riga sola.</summary>
        public bool Compact { get => _compact; set => Set(ref _compact, value); }
        /// <summary>Nome dispositivo del monitor (vuoto = principale).</summary>
        public string Monitor { get => _monitor; set => Set(ref _monitor, value ?? ""); }
        public bool OnlyWhenGameFocused { get => _onlyWhenGameFocused; set => Set(ref _onlyWhenGameFocused, value); }
    }
}
