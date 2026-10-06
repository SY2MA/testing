using System;
using System.Collections.Generic;
using FNBoost.Core;
using FNBoost.Perf;

// Modelli del report diagnostico. Questo file non dipende da WPF né da API di Windows:
// viene compilato anche dal progetto di test su Linux.

namespace FNBoost.Report
{
    /// <summary>Tutto ciò che finisce nel report (report.json ha esattamente questa forma).</summary>
    public sealed class ReportData
    {
        public const string CurrentSchema = "fnboost-report/1";

        public string SchemaVersion { get; set; } = CurrentSchema;
        public DateTime GeneratedAt { get; set; }
        public string AppVersion { get; set; } = "";
        /// <summary>Limitazioni di questo report (es. "modulo Prestazioni non attivo", "nessuna sessione").</summary>
        public List<string> Notes { get; set; } = new();
        public ReportSystem? System { get; set; }
        public List<ReportCheck> Checks { get; set; } = new();
        public List<ReportTweak> Tweaks { get; set; } = new();
        public ReportFortnite Fortnite { get; set; } = new();
        /// <summary>Sessione analizzata (con i campioni al secondo, la rete e i processi). Null = nessuna sessione.</summary>
        public PerfSession? Session { get; set; }
        /// <summary>Distribuzione dei frametime della sessione (se i frametime erano disponibili).</summary>
        public FrametimeHistogram? Histogram { get; set; }
        /// <summary>Stato della rete dal vivo, incluso solo quando la sessione non ha dati di rete.</summary>
        public NetworkSnapshot? LiveNetwork { get; set; }
        public List<PerfInsight> Insights { get; set; } = new();
        public List<PerfInsight> Trend { get; set; } = new();
        /// <summary>Ultime 10 sessioni (la più recente prima).</summary>
        public List<SessionSummary> PreviousSessions { get; set; } = new();
        public LogFindings? Log { get; set; }
        /// <summary>"Cosa non va / cosa migliorare", in ordine di priorità.</summary>
        public List<Recommendation> Recommendations { get; set; } = new();
    }

    public sealed class ReportSystem
    {
        public string Os { get; set; } = "";
        public string Cpu { get; set; } = "";
        public int Cores { get; set; }
        public int Threads { get; set; }
        public double RamTotalGb { get; set; }
        public string RamType { get; set; } = "";
        public int RamConfiguredMts { get; set; }
        public int RamRatedMts { get; set; }
        public int RamModules { get; set; }
        public List<ReportGpu> Gpus { get; set; } = new();
        public List<ReportDisplay> Displays { get; set; } = new();
        public string Board { get; set; } = "";
        public string BiosVersion { get; set; } = "";
        public DateTime? BiosDate { get; set; }
        public bool? SecureBoot { get; set; }
        public bool? Tpm { get; set; }
        /// <summary>0 = VBS disattivo, 1 = abilitato ma non in esecuzione, 2 = in esecuzione.</summary>
        public int? VbsStatus { get; set; }
        public bool? HvciRunning { get; set; }
        /// <summary>Configurazione del file di paging, es. "gestito da Windows".</summary>
        public string PageFile { get; set; } = "";
        public int? PageFileAllocatedMb { get; set; }
        public double CommitUsedGb { get; set; }
        public double CommitLimitGb { get; set; }
        public string PowerPlan { get; set; } = "";
    }

    public sealed class ReportGpu
    {
        public string Name { get; set; } = "";
        public string Vendor { get; set; } = "";
        public string DriverVersion { get; set; } = "";
        public DateTime? DriverDate { get; set; }
    }

    public sealed class ReportDisplay
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int CurrentHz { get; set; }
        public int MaxHz { get; set; }
        public bool Primary { get; set; }
    }

    /// <summary>Un controllo della diagnostica di sistema (come nella pagina Diagnostica).</summary>
    public sealed class ReportCheck
    {
        public string Title { get; set; } = "";
        public CheckStatus Status { get; set; }
        public string Message { get; set; } = "";
        public string Hint { get; set; } = "";
    }

    public sealed class ReportTweak
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Category { get; set; } = "";
        /// <summary>"Applied", "NotApplied", "Partial", "NotApplicable", "Unknown".</summary>
        public string State { get; set; } = "";
        public bool Recommended { get; set; }
        public string Impact { get; set; } = "";
    }

    public sealed class ReportFortnite
    {
        public bool Installed { get; set; }
        public string? DiskType { get; set; }
        public double? FreeGb { get; set; }
        public bool ConfigFound { get; set; }
        public FortniteSettingsSummary? Settings { get; set; }
    }

    /// <summary>Solo le impostazioni rilevanti per le prestazioni lette da GameUserSettings.ini (nessun'altra chiave).</summary>
    public sealed class FortniteSettingsSummary
    {
        /// <summary>"Prestazioni", "DirectX 12" o il valore grezzo (es. "dx11/sm5").</summary>
        public string RenderMode { get; set; } = "";
        /// <summary>0 = illimitato.</summary>
        public double FpsCap { get; set; }
        public bool VSync { get; set; }
        /// <summary>0 = off, 1 = on, 2 = on + boost.</summary>
        public int Reflex { get; set; }
        /// <summary>0 = schermo intero, 1 = finestra a schermo intero, 2 = finestra.</summary>
        public int WindowMode { get; set; }
        public int ResolutionX { get; set; }
        public int ResolutionY { get; set; }
        public Dictionary<string, string> Scalability { get; set; } = new();
        public bool RayTracing { get; set; }
        public bool Nanite { get; set; }
        public bool MotionBlur { get; set; }
    }

    /// <summary>Una riga della tabella "sessioni precedenti".</summary>
    public sealed class SessionSummary
    {
        public string Id { get; set; } = "";
        public DateTime StartedAt { get; set; }
        public string Label { get; set; } = "";
        public double DurationSec { get; set; }
        public double AvgFps { get; set; }
        public double Low1Fps { get; set; }
        public double Low01Fps { get; set; }
        public double StuttersPerMin { get; set; }
        public double? PingAvgMs { get; set; }
        /// <summary>È la sessione analizzata nel report.</summary>
        public bool Selected { get; set; }
    }

    /// <summary>Istogramma dei frametime: Counts[i] frame tra Edges[i] e Edges[i+1] ms; l'ultimo raccoglie anche quelli oltre.</summary>
    public sealed class FrametimeHistogram
    {
        public List<double> EdgesMs { get; set; } = new();
        public List<int> Counts { get; set; } = new();
        public int Total { get; set; }
        public double MedianMs { get; set; }
        public double P99Ms { get; set; }
    }

    /// <summary>Un punto della sezione "Cosa non va / Cosa migliorare".</summary>
    public sealed class Recommendation
    {
        public CheckStatus Severity { get; set; }
        public string Title { get; set; } = "";
        /// <summary>Problema: cosa è stato rilevato.</summary>
        public string Problem { get; set; } = "";
        /// <summary>Perché conta.</summary>
        public string WhyItMatters { get; set; } = "";
        /// <summary>Cosa fare.</summary>
        public string WhatToDo { get; set; } = "";
        /// <summary>"Sistema", "Sessione", "Andamento", "Rete", "Log di Fortnite".</summary>
        public string Source { get; set; } = "";
        /// <summary>Impatto stimato sul gioco: 1 basso, 2 medio, 3 alto.</summary>
        public int Impact { get; set; } = 1;
    }
}
