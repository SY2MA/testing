using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

// Questo file non dipende da WPF né da API di Windows: viene compilato anche dal progetto di test su Linux.
//
// PERCHÉ SERVE. Quando Fortnite non è la finestra in primo piano (es. mentre si avvia o si ferma la registrazione
// dalla finestra di FN Boost) il gioco si limita da solo a ~30 FPS. Quei frame non sono "prestazioni del PC":
// mescolati al gioco vero fanno crollare 1% low e regolarità e trasformano il passaggio 165 → 30 FPS in decine di
// "stutter". Qui c'è la logica pura che decide quali frame escludere; PerfService si limita a fornire i dati
// (eventi Present con il loro istante e, ogni ~100 ms, se il gioco è in primo piano).

namespace FNBoost.Perf
{
    /// <summary>Intervallo di tempo [StartMs, EndMs] (orologio interno di FN Boost) da escludere dalle statistiche.</summary>
    public readonly record struct ExclusionInterval(double StartMs, double EndMs)
    {
        public bool Contains(double ms) => ms >= StartMs && ms <= EndMs;
    }

    /// <summary>
    /// Cronologia del primo piano costruita dai controlli periodici (GetForegroundWindow → PID == gioco).
    /// Ogni periodo "fuori fuoco" diventa un intervallo di esclusione che parte dall'ultimo controllo in cui il gioco
    /// era ancora in primo piano (meno un piccolo margine: il cambio è avvenuto tra due controlli e l'istante dei frame
    /// è stimato) e finisce <see cref="SettleMs"/> dopo il controllo che vede il gioco di nuovo in primo piano,
    /// perché il gioco impiega qualche frame per tornare agli FPS pieni.
    /// Non è thread-safe: chi la usa da più thread la protegge con un lock.
    /// </summary>
    public sealed class FocusTimeline
    {
        /// <summary>Assestamento dopo il ritorno in primo piano: questi frame non contano.</summary>
        public const double SettleMs = 500;
        /// <summary>Margine prima della perdita del fuoco (incertezza del controllo e dell'istante dei frame).</summary>
        public const double LeadMarginMs = 150;

        private readonly List<ExclusionInterval> _closed = new();
        private bool _hasPoll;
        private bool _focused;
        private double _lastPollMs;
        private double _openStartMs = double.NaN;

        /// <summary>Almeno un controllo eseguito.</summary>
        public bool HasData => _hasPoll;

        /// <summary>Esito dell'ultimo controllo (true finché non ci sono controlli: nessuna esclusione senza dati).</summary>
        public bool Focused => !_hasPoll || _focused;

        /// <summary>Istante del controllo che ha visto il gioco tornare (o essere) in primo piano; NaN se ora è fuori fuoco.</summary>
        public double FocusedSinceMs { get; private set; } = double.NaN;

        /// <summary>Intervalli chiusi registrati finora (dal più vecchio).</summary>
        public int ClosedCount => _closed.Count;

        /// <summary>Registra un controllo: nowMs crescente sull'orologio interno. Un controllo più vecchio dell'ultimo è ignorato.</summary>
        public void Add(double nowMs, bool focused)
        {
            if (!double.IsFinite(nowMs)) return;
            if (!_hasPoll)
            {
                _hasPoll = true;
                _focused = focused;
                _lastPollMs = nowMs;
                if (focused) FocusedSinceMs = nowMs;
                else _openStartMs = nowMs - LeadMarginMs;
                return;
            }
            // L'orologio è monotono: un istante all'indietro è una lettura arrivata fuori ordine, quindi superata
            // da quella più recente. Usarla creerebbe un finto cambio di fuoco.
            if (nowMs < _lastPollMs) return;
            if (focused != _focused)
            {
                if (!focused)
                {
                    // Il fuoco è andato perso tra il controllo precedente e questo: si esclude da prima.
                    _openStartMs = _lastPollMs - LeadMarginMs;
                    FocusedSinceMs = double.NaN;
                }
                else
                {
                    // Tornato in primo piano tra il controllo precedente e questo: si esclude fino a qui + assestamento.
                    _closed.Add(new ExclusionInterval(_openStartMs, nowMs + SettleMs));
                    _openStartMs = double.NaN;
                    FocusedSinceMs = nowMs;
                }
                _focused = focused;
            }
            _lastPollMs = nowMs;
        }

        /// <summary>true se all'istante ms il gioco è considerato fuori fuoco (assestamento compreso).</summary>
        public bool IsExcludedAt(double ms)
        {
            if (!_focused && _hasPoll && ms >= _openStartMs) return true;
            for (int i = _closed.Count - 1; i >= 0; i--)
            {
                if (_closed[i].Contains(ms)) return true;
                if (_closed[i].EndMs < ms) break;
            }
            return false;
        }

        /// <summary>Copia degli intervalli, in ordine; quello ancora aperto (fuori fuoco adesso) finisce a +∞.</summary>
        public ExclusionInterval[] Snapshot()
        {
            int n = _closed.Count + (!_focused && _hasPoll ? 1 : 0);
            var result = new ExclusionInterval[n];
            _closed.CopyTo(result);
            if (n > _closed.Count) result[n - 1] = new ExclusionInterval(_openStartMs, double.PositiveInfinity);
            return result;
        }

        /// <summary>Dimentica gli intervalli chiusi finiti prima di ms (memoria: una sessione dura al massimo 4 ore).</summary>
        public void TrimBefore(double ms)
        {
            int k = 0;
            while (k < _closed.Count && _closed[k].EndMs < ms) k++;
            if (k > 0) _closed.RemoveRange(0, k);
        }

        public void Reset()
        {
            _closed.Clear();
            _hasPoll = false;
            _focused = false;
            _lastPollMs = 0;
            _openStartMs = double.NaN;
            FocusedSinceMs = double.NaN;
        }
    }

    /// <summary>Frame e campioni al secondo di una registrazione, con i frame fuori fuoco già esclusi.</summary>
    public sealed class SessionFrames
    {
        /// <summary>Statistiche sui soli frame in primo piano.</summary>
        public FrameStatsResult Stats { get; set; } = new();
        /// <summary>Un campione per secondo con T, Fps, Low1Fps, MaxFrametimeMs, Stutters e Unfocused (il resto lo aggiunge il chiamante).</summary>
        public List<SecondSample> Seconds { get; set; } = new();
        /// <summary>Durata dai timestamp ETW (comprende le pause brevi tra i frame).</summary>
        public double DurationSec { get; set; }
        /// <summary>Istante ETW dell'inizio del primo frame.</summary>
        public double BaseTs { get; set; }
        /// <summary>Secondi interi e resto (ms) della durata, come usati per i campioni al secondo.</summary>
        public int FullSeconds { get; set; }
        public double RestMs { get; set; }
        public int ExcludedFrames { get; set; }
        public double UnfocusedSec { get; set; }
        public List<FrameRange> ExcludedRanges { get; set; } = new();
    }

    /// <summary>Tratti iniziale/finale "in secondo piano" riconosciuti in una sessione vecchia (senza misura del primo piano).</summary>
    public sealed class BackgroundSegments
    {
        /// <summary>Secondi iniziali a ~30 FPS con GPU quasi ferma (0 = nessuno).</summary>
        public int LeadSeconds { get; set; }
        /// <summary>Secondi finali a ~30 FPS con GPU quasi ferma (0 = nessuno).</summary>
        public int TrailSeconds { get; set; }
        /// <summary>Numero di campioni al secondo della sessione.</summary>
        public int TotalSeconds { get; set; }
        /// <summary>FPS mediani del tratto centrale (il gioco vero).</summary>
        public double CoreMedianFps { get; set; }
        public bool Any => LeadSeconds > 0 || TrailSeconds > 0;
    }

    /// <summary>Logica pura dell'esclusione dei frame presentati con il gioco fuori fuoco.</summary>
    public static class FocusFilter
    {
        /// <summary>FPS a cui Fortnite si limita in secondo piano: un secondo tra questi valori "sembra" sfondo.</summary>
        public const double BackgroundFpsMin = 22, BackgroundFpsMax = 38;
        /// <summary>GPU sotto questa soglia: in secondo piano il gioco quasi non la usa.</summary>
        public const double BackgroundGpuMax = 10;
        /// <summary>Servono almeno tanti secondi consecutivi per parlare di tratto in secondo piano.</summary>
        public const int MinBackgroundSeconds = 3;
        /// <summary>FPS mediani minimi del resto della sessione (≈ 1,5 × i ~30 FPS in secondo piano).</summary>
        public const double MinCoreFps = 45;
        /// <summary>Nelle sessioni vecchie si toglie anche questo margine attorno al tratto (assestamento, come <see cref="FocusTimeline.SettleMs"/>).</summary>
        public const double LegacyMarginMs = 500;

        /// <summary>
        /// Per ogni frame, true se va escluso: il frame [fine − ft, fine] tocca un intervallo di esclusione.
        /// endTs = istante di fine di ogni frame sull'orologio dei frame (ETW); offsetMs = orologio interno − orologio
        /// dei frame (stimato dal ritardo minimo di ricezione). Fine dei frame non decrescente, intervalli in ordine.
        /// </summary>
        public static bool[] ExcludedMask(IReadOnlyList<double> endTs, IReadOnlyList<float> ft,
            IReadOnlyList<ExclusionInterval>? intervals, double offsetMs)
        {
            int n = Math.Min(endTs.Count, ft.Count);
            var mask = new bool[n];
            if (intervals == null || intervals.Count == 0 || n == 0) return mask;
            if (!double.IsFinite(offsetMs)) offsetMs = 0;
            int j = 0;
            for (int i = 0; i < n; i++)
            {
                double end = endTs[i] + offsetMs;
                double f = FrameStats.IsValid(ft[i]) ? ft[i] : 0;
                double start = end - f;
                // Gli intervalli finiti prima dell'inizio di questo frame non servono più (gli inizi sono crescenti).
                while (j < intervals.Count && intervals[j].EndMs < start) j++;
                if (j >= intervals.Count) break;
                mask[i] = intervals[j].StartMs <= end;
            }
            return mask;
        }

        /// <summary>Intervalli di indici esclusi (compatti, per salvarli con la sessione).</summary>
        public static List<FrameRange> ToRanges(IReadOnlyList<bool>? excluded)
        {
            var list = new List<FrameRange>();
            if (excluded == null) return list;
            int i = 0;
            while (i < excluded.Count)
            {
                if (!excluded[i])
                {
                    i++;
                    continue;
                }
                int start = i;
                while (i < excluded.Count && excluded[i]) i++;
                list.Add(new FrameRange { Start = start, Count = i - start });
            }
            return list;
        }

        /// <summary>Maschera di n frame dagli intervalli salvati (null se nessun frame è escluso).</summary>
        public static bool[]? MaskFromRanges(int n, IReadOnlyList<FrameRange>? ranges)
        {
            if (ranges == null || ranges.Count == 0 || n <= 0) return null;
            var mask = new bool[n];
            bool any = false;
            foreach (var r in ranges)
            {
                if (r == null) continue;
                int from = Math.Max(0, r.Start);
                int to = (int)Math.Min(n, (long)r.Start + Math.Max(0, r.Count));
                for (int i = from; i < to; i++)
                {
                    mask[i] = true;
                    any = true;
                }
            }
            return any ? mask : null;
        }

        /// <summary>Solo i frame non esclusi (copia).</summary>
        public static float[] Keep(IReadOnlyList<float> ft, IReadOnlyList<bool>? excluded)
        {
            if (ft == null) return Array.Empty<float>();
            if (excluded == null) return ft.ToArray();
            var list = new List<float>(ft.Count);
            for (int i = 0; i < ft.Count; i++)
                if (i >= excluded.Count || !excluded[i]) list.Add(ft[i]);
            return list.ToArray();
        }

        /// <summary>Frametime di una sessione salvata senza quelli fuori fuoco (ExcludedRanges).</summary>
        public static float[] FocusedFrametimes(IReadOnlyList<float> ft, PerfSession? session) =>
            Keep(ft, MaskFromRanges(ft?.Count ?? 0, session?.ExcludedRanges));

        /// <summary>
        /// Statistiche e campioni al secondo di una registrazione. ft[i] = frametime, ts[i] = istante ETW di fine frame,
        /// excluded = frame fuori fuoco (null = nessuno). Le statistiche, gli stutter e gli FPS di ogni secondo usano solo
        /// i frame in primo piano; un secondo passato per più di metà fuori fuoco è marcato Unfocused (FPS 0, buco nei grafici).
        /// Gli stutter sono cercati sulla sequenza dei soli frame in primo piano, così il salto 165 → 30 FPS non conta.
        /// </summary>
        public static SessionFrames BuildSession(IReadOnlyList<float> ft, IReadOnlyList<double> ts, IReadOnlyList<bool>? excluded,
            double stutterFactor, double stutterMinMs)
        {
            var result = new SessionFrames();
            int n = Math.Min(ft?.Count ?? 0, ts?.Count ?? 0);
            if (ft == null || ts == null) return result;

            // Indici dei frame tenuti e loro frametime.
            var keepIdx = new List<int>(n);
            for (int i = 0; i < n; i++)
            {
                bool ex = excluded != null && i < excluded.Count && excluded[i];
                if (ex)
                {
                    result.ExcludedFrames++;
                    if (FrameStats.IsValid(ft[i])) result.UnfocusedSec += ft[i] / 1000.0;
                }
                else keepIdx.Add(i);
            }
            var kept = new float[keepIdx.Count];
            for (int k = 0; k < kept.Length; k++) kept[k] = ft[keepIdx[k]];
            result.Stats = FrameStats.Compute(kept, stutterFactor, stutterMinMs);
            if (excluded != null) result.ExcludedRanges = ToRanges(excluded.Take(n).ToList());
            if (n < 2) return result;

            double baseTs = ts[0] - (FrameStats.IsValid(ft[0]) ? ft[0] : 0);
            double span = ts[n - 1] - baseTs;
            result.BaseTs = baseTs;
            result.DurationSec = span / 1000.0;
            int full = (int)(span / 1000.0);
            double rest = span - full * 1000.0;
            int nSec = full + (rest >= 500 ? 1 : 0);
            result.FullSeconds = full;
            result.RestMs = rest;
            if (nSec <= 0) return result;

            var keptFlags = FrameStats.StutterFlags(kept, stutterFactor, stutterMinMs);
            var flags = new bool[n];
            for (int k = 0; k < keepIdx.Count; k++) flags[keepIdx[k]] = keptFlags[k];

            var buckets = new List<float>?[nSec];
            var stutters = new int[nSec];
            var inclMs = new double[nSec];
            var exclMs = new double[nSec];
            var exclCount = new int[nSec];
            for (int i = 0; i < n; i++)
            {
                // Un frame appartiene al secondo in cui termina: (k·1000, (k+1)·1000] → k.
                int sec = (int)Math.Ceiling((ts[i] - baseTs) / 1000.0) - 1;
                if (sec < 0) sec = 0;
                if (sec >= nSec) continue; // ultimo secondo parziale troppo corto
                double f = FrameStats.IsValid(ft[i]) ? ft[i] : 0;
                if (excluded != null && i < excluded.Count && excluded[i])
                {
                    exclMs[sec] += f;
                    exclCount[sec]++;
                    continue;
                }
                (buckets[sec] ??= new List<float>()).Add(ft[i]);
                inclMs[sec] += f;
                if (flags[i]) stutters[sec]++;
            }

            for (int s = 0; s < nSec; s++)
            {
                var sample = new SecondSample { T = s };
                var b = buckets[s];
                bool unfocused = exclCount[s] > 0 && (b == null || b.Count == 0 || exclMs[s] >= inclMs[s]);
                if (unfocused)
                {
                    sample.Unfocused = true;
                }
                else if (b != null && b.Count > 0)
                {
                    bool partial = s == full; // solo l'ultimo, se incluso
                    if (exclCount[s] > 0)
                        sample.Fps = inclMs[s] > 0 ? b.Count * 1000.0 / inclMs[s] : 0; // FPS dei soli frame in primo piano
                    else
                        sample.Fps = partial ? b.Count * 1000.0 / rest : b.Count;
                    sample.Low1Fps = FrameStats.LowFps(b, 0.01);
                    sample.MaxFrametimeMs = b.Max();
                    sample.Stutters = stutters[s];
                }
                result.Seconds.Add(sample);
            }
            return result;
        }

        // ================= Sessioni vecchie (senza misura del primo piano) =================

        /// <summary>
        /// Cerca all'inizio e alla fine della sessione un tratto di almeno <see cref="MinBackgroundSeconds"/> secondi
        /// consecutivi a ~30 FPS (22-38) con la GPU sotto il 10%, mentre il resto della sessione va almeno a
        /// <see cref="MinCoreFps"/> FPS: è la firma di Fortnite in secondo piano (es. registrazione avviata/fermata dalla
        /// finestra di FN Boost). Il contatore GPU arriva con circa un secondo di ritardo, quindi il tratto si allunga
        /// ai secondi vicini ancora a ~30 FPS anche se la GPU risulta già (o ancora) più alta, più al massimo un secondo
        /// "misto" di passaggio (FPS tra i 30 e il 90% del gioco vero). Senza dati GPU non si conclude nulla.
        /// Null se non c'è niente da segnalare.
        /// </summary>
        public static BackgroundSegments? DetectBackground(IReadOnlyList<SecondSample>? seconds)
        {
            if (seconds == null || seconds.Count < 2 * MinBackgroundSeconds + 30) return null;
            static bool ThirtyFps(SecondSample x) => !x.Unfocused && x.Fps >= BackgroundFpsMin && x.Fps <= BackgroundFpsMax;
            static bool Bg(SecondSample x) => ThirtyFps(x) && x.GpuPercent is { } g && g < BackgroundGpuMax;

            int n = seconds.Count;
            // Nucleo certo (30 FPS e GPU ferma) dall'inizio e dalla fine.
            int lead = 0;
            while (lead < n && Bg(seconds[lead])) lead++;
            int trail = 0;
            while (trail < n - lead && Bg(seconds[n - 1 - trail])) trail++;
            if (lead < MinBackgroundSeconds) lead = 0;
            if (trail < MinBackgroundSeconds) trail = 0;
            if (lead == 0 && trail == 0) return null;
            // Secondi vicini ancora a ~30 FPS (GPU in ritardo di un campione).
            if (lead > 0) while (lead < n - trail && ThirtyFps(seconds[lead])) lead++;
            if (trail > 0) while (trail < n - lead && ThirtyFps(seconds[n - 1 - trail])) trail++;

            var core = new List<double>();
            for (int i = lead; i < n - trail; i++)
                if (seconds[i].Fps > 0 && !seconds[i].Unfocused) core.Add(seconds[i].Fps);
            if (core.Count < 30) return null;
            core.Sort();
            double median = core.Count % 2 == 1 ? core[core.Count / 2] : (core[core.Count / 2 - 1] + core[core.Count / 2]) / 2;
            if (median < MinCoreFps) return null; // il gioco vero deve essere chiaramente più veloce del tratto a ~30 FPS

            // Un secondo di passaggio (metà a 30 FPS, metà a pieni FPS) va col tratto: i suoi frame lenti non sono del PC.
            bool Mixed(SecondSample x) => !x.Unfocused && x.Fps > BackgroundFpsMax && x.Fps < 0.9 * median;
            if (lead > 0 && lead < n - trail && Mixed(seconds[lead])) lead++;
            if (trail > 0 && trail < n - lead && Mixed(seconds[n - 1 - trail])) trail++;

            return new BackgroundSegments { LeadSeconds = lead, TrailSeconds = trail, TotalSeconds = n, CoreMedianFps = median };
        }

        private static readonly JsonSerializerOptions CloneOpts = new()
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        /// <summary>
        /// Sessione registrata prima della misura del primo piano con un tratto in secondo piano all'inizio o alla fine:
        /// restituisce una COPIA con statistiche ricalcolate sul gioco vero (tratti esclusi più <see cref="LegacyMarginMs"/>
        /// di margine), secondi del tratto marcati Unfocused e UnfocusedEstimated = true. L'istante dei frame è ricostruito
        /// sommando i frametime (le pause oltre 5 s non sono nei frametime: in quel caso è approssimato).
        /// Null se la sessione ha già la misura del primo piano, se è già stata corretta, se mancano i frametime
        /// o se non c'è nessun tratto in secondo piano.
        /// </summary>
        public static PerfSession? RepairLegacy(PerfSession? session, IReadOnlyList<float>? frametimes,
            double stutterFactor = 2.5, double stutterMinMs = 12)
        {
            if (session == null || session.FocusTracked || session.UnfocusedEstimated) return null;
            if (frametimes == null || frametimes.Count < 2) return null;
            var seg = DetectBackground(session.Seconds);
            if (seg == null) return null;

            double leadEndMs = seg.LeadSeconds > 0 ? seg.LeadSeconds * 1000.0 + LegacyMarginMs : double.NegativeInfinity;
            double trailStartMs = seg.TrailSeconds > 0 ? (seg.TotalSeconds - seg.TrailSeconds) * 1000.0 - LegacyMarginMs : double.PositiveInfinity;
            var excluded = new bool[frametimes.Count];
            var endTs = new double[frametimes.Count];
            double t = 0;
            for (int i = 0; i < frametimes.Count; i++)
            {
                double f = FrameStats.IsValid(frametimes[i]) ? frametimes[i] : 0;
                double start = t;
                t += f;
                endTs[i] = t;
                excluded[i] = start < leadEndMs || t > trailStartMs;
            }

            var built = BuildSession(frametimes, endTs, excluded, stutterFactor, stutterMinMs);
            if (!built.Stats.HasData) return null;

            var copy = JsonSerializer.Deserialize<PerfSession>(JsonSerializer.SerializeToUtf8Bytes(session, CloneOpts), CloneOpts)!;
            copy.Stats = built.Stats;
            copy.UnfocusedSec = built.UnfocusedSec;
            copy.ExcludedFrames = built.ExcludedFrames;
            copy.ExcludedRanges = built.ExcludedRanges;
            copy.UnfocusedEstimated = true;
            // I secondi del tratto diventano buchi: FPS, low e stutter non sono del gioco vero.
            // Nei due secondi di passaggio (subito dopo / subito prima del tratto) gli stutter sono il salto di FPS.
            int coreFirst = seg.LeadSeconds, coreLast = seg.TotalSeconds - seg.TrailSeconds - 1;
            for (int s = 0; s < copy.Seconds.Count; s++)
            {
                var x = copy.Seconds[s];
                if (s < coreFirst || s > coreLast)
                {
                    x.Unfocused = true;
                    x.Fps = 0;
                    x.Low1Fps = 0;
                    x.MaxFrametimeMs = 0;
                    x.Stutters = 0;
                }
                else if ((seg.LeadSeconds > 0 && s == coreFirst) || (seg.TrailSeconds > 0 && s == coreLast))
                {
                    x.Stutters = 0;
                }
            }
            return copy;
        }
    }
}
