using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FNBoost.Core;

// Questo file non dipende da WPF: viene compilato anche dal progetto di test su Linux.

namespace FNBoost.Perf
{
    /// <summary>
    /// Analisi automatica delle sessioni: trasforma numeri in osservazioni leggibili con un suggerimento pratico.
    /// Le soglie sono volutamente prudenti: meglio un consiglio in meno che un allarme sbagliato.
    /// </summary>
    public static class PerfAnalyzer
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>
        /// Analizza una sessione. I frame con il gioco fuori fuoco (ExcludedRanges, secondi Unfocused) non contano.
        /// Le sessioni vecchie, registrate prima della misura del primo piano, con un tratto iniziale/finale a ~30 FPS
        /// e GPU quasi ferma vengono analizzate sul gioco vero (FocusFilter.RepairLegacy) se ci sono i frametime;
        /// senza frametime l'analisi lo dice invece di dare la colpa al PC.
        /// </summary>
        public static List<PerfInsight> Analyze(PerfSession session, IReadOnlyList<PerfSession> history, IReadOnlyList<float>? frametimes = null)
        {
            var list = new List<PerfInsight>();
            if (session == null) return list;
            var original = session;
            var repaired = FocusFilter.RepairLegacy(session, frametimes);
            if (repaired != null) session = repaired;
            var focusedFt = frametimes != null ? FocusFilter.FocusedFrametimes(frametimes, session) : null;
            var st = session.Stats;
            if ((st == null || !st.HasData) && focusedFt != null && focusedFt.Length >= 2)
                st = FrameStats.Compute(focusedFt);
            if (st == null || !st.HasData)
            {
                list.Add(new PerfInsight
                {
                    Severity = CheckStatus.Info,
                    Title = "Dati insufficienti",
                    Message = "La sessione non contiene abbastanza frame per un'analisi.",
                    Hint = "Registra almeno un minuto di gioco vero e proprio."
                });
                return list;
            }

            var seconds = session.Seconds ?? new List<SecondSample>();
            var active = seconds.Where(s => s.Fps > 0 && !s.Unfocused).ToList();

            // Sessione vecchia con tratto in secondo piano ma senza frametime per ricalcolare: statistiche falsate.
            var legacy = !session.FocusTracked && !session.UnfocusedEstimated ? FocusFilter.DetectBackground(seconds) : null;
            double avg = st.AvgFps;
            if (legacy != null)
            {
                active = seconds.Skip(legacy.LeadSeconds).Take(seconds.Count - legacy.LeadSeconds - legacy.TrailSeconds)
                    .Where(s => s.Fps > 0 && !s.Unfocused).ToList();
                if (active.Count > 0) avg = active.Average(s => s.Fps);
            }

            // Statistiche "grezze" (con il tratto in secondo piano) solo per confronto nel messaggio.
            var raw = repaired != null ? original.Stats
                : session.UnfocusedEstimated && frametimes != null && frametimes.Count >= 2 ? FrameStats.Compute(frametimes) : null;
            AddFocus(list, original, session, legacy, raw);
            if (legacy == null)
            {
                AddOverall(list, st);
                AddStutterRate(list, st);
            }
            AddShaderCompilation(list, session, st, seconds, focusedFt);

            // ---- Limite FPS: cap, VSync/refresh, sotto il refresh ----
            double cap = session.FpsCap ?? 0;
            int hz = session.RefreshHz ?? 0;
            bool capped = cap > 0 && avg >= 0.97 * cap;
            bool vsyncLike = !capped && hz > 0 && Math.Abs(avg - hz) <= 0.02 * hz &&
                             st.StdDevFrametimeMs <= 0.15 * st.AvgFrametimeMs;
            bool belowRefresh = hz > 0 && avg < 0.9 * hz && !capped;

            if (capped)
            {
                list.Add(new PerfInsight
                {
                    Severity = CheckStatus.Info,
                    Title = "FPS limitati dal cap",
                    Message = $"Media {N0(avg)} FPS con limite impostato a {N0(cap)}: il gioco gira al massimo consentito.",
                    Hint = hz > 0 && cap < hz
                        ? $"Il limite è sotto i {hz} Hz del monitor: se la media lo raggiunge sempre, puoi provare ad alzarlo."
                        : "Un limite stabile riduce calore e latenza variabile: va benissimo se gli FPS restano agganciati."
                });
            }
            else if (vsyncLike)
            {
                list.Add(new PerfInsight
                {
                    Severity = CheckStatus.Info,
                    Title = "FPS agganciati al refresh",
                    Message = $"Media {N0(avg)} FPS, praticamente uguale ai {hz} Hz del monitor e molto costante: probabilmente è attivo il VSync (o un limite equivalente).",
                    Hint = "Per la latenza più bassa in competitivo disattiva il VSync e usa un limite FPS (o NVIDIA Reflex)."
                });
            }
            else if (belowRefresh)
            {
                list.Add(new PerfInsight
                {
                    Severity = CheckStatus.Warn,
                    Title = "FPS sotto il refresh del monitor",
                    Message = $"Media {N0(avg)} FPS contro {hz} Hz del monitor ({N0(avg / hz * 100)}%).",
                    Hint = "Il monitor può mostrare più immagini di quante il PC ne produca: vedi sotto il probabile collo di bottiglia."
                });
            }
            else if (hz > 0 && avg >= hz)
            {
                list.Add(new PerfInsight
                {
                    Severity = CheckStatus.Ok,
                    Title = "FPS sopra il refresh",
                    Message = $"Media {N0(avg)} FPS con monitor a {hz} Hz.",
                    Hint = "Ottimo: più FPS del refresh riducono comunque la latenza di input."
                });
            }

            AddBottleneck(list, active, avg, hz, capped, vsyncLike, belowRefresh);
            AddMemory(list, session, active);
            AddBackgroundActivity(list, active);
            AddNetwork(list, session, st, seconds);
            AddProcesses(list, session);
            AddComparison(list, session, history);
            return list;
        }

        public static List<PerfInsight> Trend(IReadOnlyList<PerfSession> history)
        {
            var list = new List<PerfInsight>();
            var groups = (history ?? Array.Empty<PerfSession>())
                .Where(s => s?.Stats != null && s.Stats.HasData)
                .GroupBy(s => s.ProcessName ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(s => s.StartedAt).ToList())
                .OrderByDescending(g => g[0].StartedAt)
                .ToList();

            foreach (var g in groups)
            {
                var name = DisplayName(g[0].ProcessName);
                if (g.Count >= 3)
                {
                    int recentCount = Math.Min(3, g.Count - 1);
                    var recent = g.Take(recentCount).ToList();
                    var older = g.Skip(recentCount).Take(3).ToList();
                    double rAvg = recent.Average(s => s.Stats.AvgFps), oAvg = older.Average(s => s.Stats.AvgFps);
                    double rLow = recent.Average(s => s.Stats.Low1Fps), oLow = older.Average(s => s.Stats.Low1Fps);
                    double dAvg = Rel(rAvg, oAvg), dLow = Rel(rLow, oLow);
                    var msg = $"{name}: ultime {recent.Count} sessioni {N0(rAvg)} FPS medi / {N0(rLow)} 1% low, " +
                              $"contro {N0(oAvg)} / {N0(oLow)} delle {older.Count} precedenti (media {Pct(dAvg)}, 1% low {Pct(dLow)}).";

                    if (dAvg < -0.10 || dLow < -0.10)
                    {
                        list.Add(new PerfInsight
                        {
                            Severity = CheckStatus.Warn,
                            Title = "Prestazioni in calo",
                            Message = msg,
                            Hint = "Spesso dipende da un aggiornamento del gioco o dei driver, oppure da programmi in background nuovi. " +
                                   "Controlla cosa è cambiato di recente; dopo una patch le prime partite possono essere più lente (shader)."
                        });
                    }
                    else if (dAvg > 0.10 || dLow > 0.10)
                    {
                        list.Add(new PerfInsight
                        {
                            Severity = CheckStatus.Ok,
                            Title = "Prestazioni in miglioramento",
                            Message = msg,
                            Hint = "Le modifiche recenti sembrano aver aiutato (correlazione, non prova: anche mappa e modalità contano)."
                        });
                    }
                    else
                    {
                        list.Add(new PerfInsight
                        {
                            Severity = CheckStatus.Info,
                            Title = "Prestazioni stabili",
                            Message = msg,
                            Hint = "Nessuna variazione significativa (oltre ±10%) tra le sessioni recenti e le precedenti."
                        });
                    }
                }

                if (g.Count >= 2)
                {
                    var best = g.OrderByDescending(s => s.Stats.Low1Fps).First();
                    list.Add(new PerfInsight
                    {
                        Severity = CheckStatus.Info,
                        Title = "Sessione migliore",
                        Message = $"{name}: {best.Title} con {N0(best.Stats.Low1Fps)} FPS di 1% low ({N0(best.Stats.AvgFps)} medi), su {g.Count} sessioni.",
                        Hint = best.ActiveTweaks != null && best.ActiveTweaks.Count > 0
                            ? $"In quella sessione erano attivi {best.ActiveTweaks.Count} tweak{(string.IsNullOrEmpty(best.RenderMode) ? "" : $" con rendering {best.RenderMode}")}: usala come riferimento."
                            : "Usala come riferimento per i confronti futuri."
                    });
                }
            }

            if (!groups.Any(g => g.Count >= 3))
            {
                list.Insert(0, new PerfInsight
                {
                    Severity = CheckStatus.Info,
                    Title = "Andamento non ancora disponibile",
                    Message = "Servono almeno 3 sessioni dello stesso gioco per vedere come cambiano le prestazioni nel tempo.",
                    Hint = "Lascia attiva la registrazione automatica: le sessioni si salvano da sole."
                });
            }
            return list;
        }

        public static string Compare(PerfSession current, PerfSession baseline)
        {
            if (current?.Stats == null || baseline?.Stats == null) return "";
            var c = current.Stats;
            var b = baseline.Stats;
            string stutter;
            if (b.StuttersPerMin <= 0.0001)
                stutter = c.StuttersPerMin <= 0.0001 ? "stutter =" : $"stutter da 0 a {N1(c.StuttersPerMin)}/min";
            else
                stutter = "stutter " + Pct(Rel(c.StuttersPerMin, b.StuttersPerMin));
            return $"Media {Pct(Rel(c.AvgFps, b.AvgFps))} · 1% low {Pct(Rel(c.Low1Fps, b.Low1Fps))} · {stutter} rispetto a {baseline.Title}";
        }

        // ---- sezioni dell'analisi ----

        /// <summary>Quanto tempo con il gioco fuori fuoco è stato escluso (misurato o stimato), o perché i numeri sono falsati.</summary>
        private static void AddFocus(List<PerfInsight> list, PerfSession original, PerfSession session, BackgroundSegments? legacy, FrameStatsResult? raw)
        {
            if (legacy != null)
            {
                list.Add(new PerfInsight
                {
                    Severity = CheckStatus.Info,
                    Title = "Probabile gioco in secondo piano",
                    Message = $"{SegmentText(legacy)} la sessione è a ~30 FPS con la GPU sotto il 10%, mentre il resto va a circa {N0(legacy.CoreMedianFps)} FPS. " +
                              "È la firma di Fortnite in secondo piano (per esempio mentre avvii o fermi la registrazione dalla finestra di FN Boost): " +
                              $"1% low ({N0(original.Stats?.Low1Fps ?? 0)} FPS), stutter e regolarità della sessione sono falsati da quel tratto e non indicano un problema del PC.",
                    Hint = "I frametime di questa sessione non sono disponibili, quindi non è stato possibile ricalcolare le statistiche sul gioco vero. " +
                           "Le sessioni nuove escludono da sole il tempo fuori fuoco."
                });
                return;
            }
            if (session.UnfocusedEstimated)
            {
                // Tratti già marcati nei campioni al secondo (secondi Unfocused all'inizio e alla fine).
                var secs = session.Seconds ?? new List<SecondSample>();
                int lead = 0, trail = 0;
                while (lead < secs.Count && secs[lead].Unfocused) lead++;
                while (trail < secs.Count - lead && secs[secs.Count - 1 - trail].Unfocused) trail++;
                var seg = new BackgroundSegments { LeadSeconds = lead, TrailSeconds = trail, TotalSeconds = secs.Count };
                list.Add(new PerfInsight
                {
                    Severity = CheckStatus.Info,
                    Title = "Gioco in secondo piano escluso",
                    Message = $"{(seg.Any ? SegmentText(seg) : "In una parte")} la sessione era a ~30 FPS con la GPU sotto il 10%: probabile gioco in secondo piano " +
                              "(per esempio mentre avviavi o fermavi la registrazione da FN Boost). " +
                              $"Escluso dalle statistiche ({N0(session.UnfocusedSec)} s, {session.ExcludedFrames} frame): sul gioco vero media {N0(session.Stats.AvgFps)} FPS e 1% low {N0(session.Stats.Low1Fps)} FPS" +
                              (raw is { HasData: true } ? $", contro {N0(raw.AvgFps)} e {N0(raw.Low1Fps)} contando anche quel tratto." : "."),
                    Hint = "È una stima su una sessione registrata prima che FN Boost controllasse il primo piano; le sessioni nuove lo misurano direttamente."
                });
                return;
            }
            if (session.FocusTracked && session.UnfocusedSec >= 1)
            {
                double inFocus = Math.Max(0, session.DurationSec - session.UnfocusedSec);
                list.Add(new PerfInsight
                {
                    Severity = CheckStatus.Info,
                    Title = "Tempo fuori fuoco escluso",
                    Message = $"{N0(session.UnfocusedSec)} s ({session.ExcludedFrames} frame) con il gioco non in primo piano non sono stati contati: " +
                              "in secondo piano Fortnite si limita da solo a ~30 FPS, quindi non sono prestazioni del PC.",
                    Hint = $"Le statistiche si riferiscono a {DurationText(inFocus)} di gioco in primo piano (esclusi anche ~0,5 s di assestamento a ogni ritorno nel gioco)."
                });
            }
        }

        /// <summary>"I primi 7 s e gli ultimi 8 s del" / "I primi 7 s della" / "Gli ultimi 8 s della".</summary>
        private static string SegmentText(BackgroundSegments seg)
        {
            if (seg.LeadSeconds > 0 && seg.TrailSeconds > 0) return $"Nei primi {seg.LeadSeconds} s e negli ultimi {seg.TrailSeconds} s";
            if (seg.LeadSeconds > 0) return $"Nei primi {seg.LeadSeconds} s";
            return $"Negli ultimi {seg.TrailSeconds} s";
        }

        private static void AddOverall(List<PerfInsight> list, FrameStatsResult st)
        {
            double ratio = st.AvgFps > 0 ? st.Low1Fps / st.AvgFps : 0;
            var msg = $"Media {N0(st.AvgFps)} FPS · 1% low {N0(st.Low1Fps)} FPS · 0,1% low {N0(st.Low01Fps)} FPS · " +
                      $"regolarità {N0(st.ConsistencyScore)}/100 (1% low = {N0(ratio * 100)}% della media).";
            if (ratio >= 0.75)
                list.Add(new PerfInsight
                {
                    Severity = CheckStatus.Ok,
                    Title = "Gioco fluido",
                    Message = msg,
                    Hint = "I frame più lenti restano vicini alla media: l'esperienza è stabile."
                });
            else if (ratio >= 0.6)
                list.Add(new PerfInsight
                {
                    Severity = CheckStatus.Info,
                    Title = "Fluidità buona",
                    Message = msg,
                    Hint = "Qualche calo occasionale: se lo noti in gioco, guarda i suggerimenti qui sotto."
                });
            else if (ratio >= 0.45)
                list.Add(new PerfInsight
                {
                    Severity = CheckStatus.Warn,
                    Title = "Fluidità altalenante",
                    Message = msg,
                    Hint = "I frame più lenti sono parecchio sotto la media e si percepiscono come scatti. " +
                           "Un limite FPS poco sopra l'1% low rende spesso il gioco più regolare."
                });
            else
                list.Add(new PerfInsight
                {
                    Severity = CheckStatus.Bad,
                    Title = "Frame molto irregolari",
                    Message = msg,
                    Hint = "Gli FPS medi alti non bastano: i cali sono forti. Controlla stutter e collo di bottiglia qui sotto " +
                           "e prova un limite FPS più basso."
                });
        }

        private static void AddStutterRate(List<PerfInsight> list, FrameStatsResult st)
        {
            var rate = st.StuttersPerMin;
            var msg = $"{st.Stutters} stutter in {DurationText(st.DurationSec)} ({N1(rate)} al minuto); frame più lungo {N1(st.MaxFrametimeMs)} ms.";
            if (rate <= 2)
                list.Add(new PerfInsight
                {
                    Severity = CheckStatus.Ok,
                    Title = "Pochissimi stutter",
                    Message = msg,
                    Hint = "Nessun intervento necessario."
                });
            else
                list.Add(new PerfInsight
                {
                    Severity = rate <= 10 ? CheckStatus.Warn : CheckStatus.Bad,
                    Title = rate <= 10 ? "Qualche stutter" : "Molti stutter",
                    Message = msg,
                    Hint = "Nelle prime partite dopo un aggiornamento è normale (compilazione shader). Altrimenti: chiudi le app in background " +
                           "(browser, launcher, overlay), lascia il file di paging gestito da Windows, attiva XMP/EXPO nel BIOS e, " +
                           "se persiste, svuota la cache shader del driver."
                });
        }

        private static void AddShaderCompilation(List<PerfInsight> list, PerfSession session, FrameStatsResult st,
            List<SecondSample> seconds, IReadOnlyList<float>? frametimes)
        {
            if (st.Stutters < 3 || session.DurationSec < 120) return;
            int early;
            int total;
            if (seconds.Count > 0 && seconds.Sum(s => s.Stutters) > 0)
            {
                early = seconds.Where(s => s.T < 60).Sum(s => s.Stutters);
                total = seconds.Sum(s => s.Stutters);
            }
            else if (frametimes != null && frametimes.Count > 0)
            {
                var flags = FrameStats.StutterFlags(frametimes, 2.5, 12);
                double t = 0;
                early = 0;
                total = 0;
                for (int i = 0; i < frametimes.Count; i++)
                {
                    if (FrameStats.IsValid(frametimes[i])) t += frametimes[i];
                    if (!flags[i]) continue;
                    total++;
                    if (t < 60000) early++;
                }
            }
            else return;

            if (total >= 3 && early >= total * 0.5)
                list.Add(new PerfInsight
                {
                    Severity = CheckStatus.Info,
                    Title = "Stutter concentrati all'inizio",
                    Message = $"{early} stutter su {total} nel primo minuto della sessione.",
                    Hint = "È tipico della compilazione degli shader (dopo aggiornamenti del gioco o dei driver): " +
                           "di solito sparisce dopo qualche partita. Non cancellare la cache shader senza motivo."
                });
        }

        private static void AddBottleneck(List<PerfInsight> list, List<SecondSample> active, double avg, int hz,
            bool capped, bool vsyncLike, bool belowRefresh)
        {
            var gpuSeconds = active.Where(s => s.GpuPercent.HasValue).ToList();
            if (gpuSeconds.Count < 5) return;
            double gpu = gpuSeconds.Average(s => s.GpuPercent!.Value);
            double cpu = gpuSeconds.Average(s => s.CpuPercent);

            if (gpu >= 95)
            {
                list.Add(new PerfInsight
                {
                    Severity = belowRefresh ? CheckStatus.Warn : CheckStatus.Info,
                    Title = "Limite GPU",
                    Message = $"La GPU lavora in media al {N0(gpu)}%: è lei a decidere quanti FPS ottieni.",
                    Hint = "Per più FPS: modalità di rendering Prestazioni, risoluzione 3D più bassa, ombre ed effetti ridotti, " +
                           "upscaling TSR o DLSS/FSR in modalità prestazioni."
                });
            }
            else if (gpu <= 75 && !capped && !vsyncLike && belowRefresh)
            {
                list.Add(new PerfInsight
                {
                    Severity = CheckStatus.Warn,
                    Title = "Probabile limite CPU",
                    Message = $"La GPU è solo al {N0(gpu)}% (CPU {N0(cpu)}% totale) ma gli FPS ({N0(avg)}) restano sotto i {hz} Hz del monitor: " +
                              "probabilmente il limite è la CPU (o la memoria).",
                    Hint = "Prova la modalità Prestazioni, attiva XMP/EXPO nel BIOS, lascia attiva la Modalità gioco di Windows, " +
                           "chiudi le app in background e usa un piano energetico ad alte prestazioni. " +
                           "Nota: un solo core saturo basta a limitare gli FPS anche con la CPU totale bassa."
                });
            }
        }

        private static void AddMemory(List<PerfInsight> list, PerfSession session, List<SecondSample> active)
        {
            var vram = active.Where(s => s.VramUsedGb.HasValue).Select(s => s.VramUsedGb!.Value).ToList();
            if (vram.Count > 0 && session.VramTotalGb is > 0)
            {
                double used = vram.Average();
                double total = session.VramTotalGb.Value;
                if (used >= 0.92 * total)
                    list.Add(new PerfInsight
                    {
                        Severity = CheckStatus.Warn,
                        Title = "Memoria video quasi piena",
                        Message = $"VRAM usata in media {N1(used)} GB su {N1(total)} GB ({N0(used / total * 100)}%).",
                        Hint = "Abbassa la qualità delle texture (o la risoluzione 3D): quando la VRAM si riempie compaiono scatti e texture lente a caricare."
                    });
            }

            if (active.Count > 0)
            {
                double ram = active.Average(s => s.RamPercent);
                if (ram >= 90)
                    list.Add(new PerfInsight
                    {
                        Severity = CheckStatus.Warn,
                        Title = "RAM quasi piena",
                        Message = $"Memoria di sistema usata in media al {N0(ram)}%.",
                        Hint = "Chiudi browser e app pesanti prima di giocare: con la RAM piena Windows usa il disco e compaiono scatti."
                    });
            }
        }

        private static void AddBackgroundActivity(List<PerfInsight> list, List<SecondSample> active)
        {
            var withStutter = active.Where(s => s.Stutters > 0).ToList();
            var without = active.Where(s => s.Stutters == 0).ToList();
            if (withStutter.Count < 3 || without.Count < 3) return;
            double cpuS = withStutter.Average(s => s.CpuPercent);
            double cpuN = without.Average(s => s.CpuPercent);
            if (cpuS - cpuN >= 15)
                list.Add(new PerfInsight
                {
                    Severity = CheckStatus.Warn,
                    Title = "Attività in background durante gli stutter",
                    Message = $"Nei secondi con stutter la CPU è in media al {N0(cpuS)}%, contro {N0(cpuN)}% nel resto della sessione.",
                    Hint = "Qualcosa sta usando la CPU a ondate: aggiornamenti, antivirus, launcher, browser o registrazione video. " +
                           "Controlla Gestione attività durante il gioco."
                });
        }

        private static void AddComparison(List<PerfInsight> list, PerfSession session, IReadOnlyList<PerfSession> history)
        {
            if (history == null || session.Stats == null || !session.Stats.HasData) return;
            var prev = history
                .Where(h => h != null && h.Id != session.Id && h.Stats != null && h.Stats.HasData &&
                            string.Equals(h.ProcessName, session.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                            h.StartedAt < session.StartedAt)
                .OrderByDescending(h => h.StartedAt)
                .FirstOrDefault();
            if (prev == null) return;

            double dAvg = Rel(session.Stats.AvgFps, prev.Stats.AvgFps);
            double dLow = Rel(session.Stats.Low1Fps, prev.Stats.Low1Fps);

            var changes = new List<string>();
            var cur = session.ActiveTweaks ?? new List<string>();
            var old = prev.ActiveTweaks ?? new List<string>();
            var added = cur.Except(old, StringComparer.OrdinalIgnoreCase).ToList();
            var removed = old.Except(cur, StringComparer.OrdinalIgnoreCase).ToList();
            if (added.Count > 0) changes.Add("tweak aggiunti: " + string.Join(", ", added));
            if (removed.Count > 0) changes.Add("tweak rimossi: " + string.Join(", ", removed));
            if (!string.IsNullOrEmpty(session.RenderMode) && !string.IsNullOrEmpty(prev.RenderMode) &&
                !string.Equals(session.RenderMode, prev.RenderMode, StringComparison.OrdinalIgnoreCase))
                changes.Add($"rendering {prev.RenderMode} → {session.RenderMode}");
            if (session.FpsCap != prev.FpsCap && (session.FpsCap.HasValue || prev.FpsCap.HasValue))
                changes.Add($"limite FPS {CapText(prev.FpsCap)} → {CapText(session.FpsCap)}");

            var msg = Compare(session, prev) + ".";
            if (changes.Count > 0) msg += " Differenze: " + string.Join("; ", changes) + ".";
            var netDelta = NetworkDelta(session, prev);
            if (netDelta.Length > 0) msg += " " + netDelta;

            CheckStatus sev;
            string title;
            if (dAvg >= 0.03 && dLow >= 0.03) { sev = CheckStatus.Ok; title = "Meglio della sessione precedente"; }
            else if (dAvg <= -0.05 || dLow <= -0.05) { sev = CheckStatus.Warn; title = "Peggio della sessione precedente"; }
            else { sev = CheckStatus.Info; title = "Simile alla sessione precedente"; }

            var hint = changes.Count > 0
                ? "Le differenze di configurazione potrebbero aver contribuito, ma è una correlazione, non una prova: " +
                  "patch del gioco, driver, mappa e modalità cambiano molto i risultati. Ripeti il confronto in condizioni simili."
                : "Nessuna modifica di configurazione rilevata tra le due sessioni: la differenza dipende probabilmente da mappa, modalità o aggiornamenti.";

            list.Add(new PerfInsight { Severity = sev, Title = title, Message = msg, Hint = hint });
        }

        // ---- rete ----

        /// <summary>Ping, jitter, perdita, rete di casa, Wi-Fi, banda delle altre app e freeze. Niente se la sessione non ha dati di rete.</summary>
        private static void AddNetwork(List<PerfInsight> list, PerfSession session, FrameStatsResult st, List<SecondSample> seconds)
        {
            var net = session.Network;
            if (net == null) return;
            bool server = string.Equals(net.PingTargetKind, "server", StringComparison.OrdinalIgnoreCase);
            bool wifi = string.Equals(net.ConnectionType, "Wi-Fi", StringComparison.OrdinalIgnoreCase);
            var game = net.Game;
            var gw = net.Gateway;
            string target = server ? "il server di gioco" : "la regione Epic" + (string.IsNullOrEmpty(net.RegionName) ? "" : " " + net.RegionName);
            bool gwSlow = gw != null && gw.Received >= 10 && gw.AvgMs is > 10;
            bool gwJitter = gw != null && gw.Received >= 10 && gw.JitterMs is > 5;
            bool gwLoss = gw != null && gw.Sent >= 10 && gw.Sent - gw.Received >= 2 && gw.LossPct > 0;
            bool homeProblem = gwSlow || gwJitter || gwLoss;

            // ---- ping ----
            if (game != null && game.Received >= 10 && game.AvgMs is { } ping)
            {
                var msg = $"Ping medio {N0(ping)} ms verso {target} (minimo {N0(game.MinMs ?? ping)}, 95° percentile {N0(game.P95Ms ?? ping)}), " +
                          $"jitter {N1(game.JitterMs ?? 0)} ms, perdita {N1(game.LossPct)}%.";
                if (!server)
                    msg += " Il server della partita non risponde al ping (o non è stato rilevato): il valore è quello della regione Epic, indicativo del ping reale.";
                double? best = net.BestRegionPingMs;
                bool farServer = best.HasValue && best.Value + 30 < ping;

                if (ping > 80)
                {
                    string hint;
                    if (farServer && server)
                        hint = $"La regione Epic più vicina ({net.BestRegionName}) risponde in {N0(best!.Value)} ms: il server della partita è lontano. " +
                               "In Fortnite controlla Impostazioni → Gioco → Regione matchmaking e scegli quella più vicina (con \"Auto\" a volte finisci altrove).";
                    else if (farServer)
                        hint = $"La regione {net.RegionName} è lontana: {net.BestRegionName} risponde in {N0(best!.Value)} ms. " +
                               "Scegli la regione più vicina sia in Fortnite (Regione matchmaking) sia nelle impostazioni di FN Boost.";
                    else if (homeProblem)
                        hint = "Anche il router risponde lento o in modo irregolare: una parte del ritardo nasce nella rete di casa (vedi sotto).";
                    else if (net.Internet?.AvgMs is { } inet && inet > 60)
                        hint = $"Anche verso Internet (1.1.1.1) il ping è alto ({N0(inet)} ms): dipende dalla linea o dal provider, non dal PC.";
                    else
                        hint = "La distanza dal data center conta più di ogni ottimizzazione: usa la regione di matchmaking più vicina. " +
                               "Il cavo Ethernet toglie qualche ms e soprattutto instabilità.";
                    list.Add(new PerfInsight
                    {
                        Severity = ping > 120 ? CheckStatus.Bad : CheckStatus.Warn,
                        Title = "Ping alto",
                        Message = msg,
                        Hint = hint
                    });
                }
                else
                {
                    list.Add(new PerfInsight
                    {
                        Severity = CheckStatus.Ok,
                        Title = "Ping buono",
                        Message = msg,
                        Hint = server
                            ? "Sotto gli 80 ms la latenza non è un limite per il competitivo."
                            : "Il ping della regione Epic è buono; quello del server reale può essere qualche ms diverso."
                    });
                }

                // ---- jitter ----
                if (game.JitterMs is { } jit && jit > 10)
                    list.Add(new PerfInsight
                    {
                        Severity = jit > 25 ? CheckStatus.Bad : CheckStatus.Warn,
                        Title = "Ping instabile (jitter)",
                        Message = $"Il ping varia in media di {N1(jit)} ms da un secondo all'altro verso {target}.",
                        Hint = gwJitter || gwSlow
                            ? "L'instabilità c'è già verso il router: la causa è nella rete di casa (Wi-Fi, router, altri dispositivi che scaricano)."
                            : "Il router è stabile: la variabilità nasce fuori casa (linea, provider o percorso verso il server). " +
                              "Chiudi i download in background; se persiste a tutte le ore, segnalalo al provider."
                    });

                // ---- perdita ----
                if (game.LossPct > 1)
                {
                    string hint;
                    if (gwLoss) hint = "Si perdono pacchetti già verso il router: il problema è il Wi-Fi o il collegamento al router.";
                    else if (net.Internet is { Sent: >= 10 } i && i.LossPct > 1)
                        hint = "Si perdono pacchetti anche verso Internet (1.1.1.1): è la linea o il provider. Riavvia il modem/router e, se continua, contatta il provider.";
                    else
                        hint = "Il resto della rete non perde pacchetti: può essere il percorso verso il server oppure il server che limita le risposte al ping " +
                               "(in quel caso in gioco non si nota nulla). Se senti lag, confronta con altre sessioni.";
                    list.Add(new PerfInsight
                    {
                        Severity = game.LossPct > 3 ? CheckStatus.Bad : CheckStatus.Warn,
                        Title = "Perdita di pacchetti",
                        Message = $"{N1(game.LossPct)}% dei ping verso {target} senza risposta ({game.Sent - game.Received} su {game.Sent}).",
                        Hint = hint
                    });
                }
            }

            // ---- rete di casa ----
            if (homeProblem && gw != null)
            {
                list.Add(new PerfInsight
                {
                    Severity = CheckStatus.Warn,
                    Title = "Rete di casa instabile",
                    Message = $"Ping verso il router: media {N1(gw.AvgMs ?? 0)} ms, jitter {N1(gw.JitterMs ?? 0)} ms, perdita {N1(gw.LossPct)}%. " +
                              "In una rete di casa sana è di 1-2 ms, costante e senza perdite.",
                    Hint = wifi
                        ? "Sei in Wi-Fi: il cavo Ethernet è la soluzione migliore. In alternativa usa la banda 5 GHz, avvicina il PC al router " +
                          "(o togli ostacoli) e controlla chi altro usa la rete in quel momento (streaming, download, videochiamate)."
                        : "Controlla cavo e porta del router, riavvia il router e verifica che altri dispositivi non stiano saturando la rete " +
                          "(streaming, download, backup)."
                });
            }

            // ---- Wi-Fi ----
            if (wifi && net.WifiSignalPct is { } sig && sig < 60)
                list.Add(new PerfInsight
                {
                    Severity = CheckStatus.Warn,
                    Title = "Segnale Wi-Fi debole",
                    Message = $"Segnale Wi-Fi medio {sig}% durante la sessione.",
                    Hint = "Con un segnale debole aumentano jitter e pacchetti persi. Usa il cavo se puoi, oppure avvicina il router, " +
                           "passa alla banda 5 GHz o valuta un ripetitore/sistema mesh."
                });

            // ---- banda delle altre app ----
            if (net.AvgOtherAppsKbps > 2000 || net.MaxOtherAppsKbps > 10000)
                list.Add(new PerfInsight
                {
                    Severity = CheckStatus.Warn,
                    Title = "Altre app usano la connessione",
                    Message = $"Durante la sessione le altre app di questo PC hanno usato in media {Mbps(net.AvgOtherAppsKbps)} Mbit/s " +
                              $"(picco {Mbps(net.MaxOtherAppsKbps)} Mbit/s)" +
                              (net.AvgGameKbpsIn + net.AvgGameKbpsOut > 0 ? $"; il gioco ne usa circa {Mbps(net.AvgGameKbpsIn + net.AvgGameKbpsOut)} Mbit/s." : "."),
                    Hint = "Probabili download in background: Windows Update / Ottimizzazione recapito, Steam o altri launcher, OneDrive o altri cloud, " +
                           "video nel browser. Mettili in pausa mentre giochi. Nota: qui si vede solo il traffico di questo PC, non degli altri dispositivi di casa."
                });

            // ---- freeze di rete e confronto con gli stutter ----
            var corr = NetStats.Correlate(seconds);
            if (net.Freezes > 0)
            {
                double minutes = Math.Max(1, session.DurationSec / 60.0);
                double perMin = net.Freezes / minutes;
                var sev = perMin >= 1 || net.LongestFreezeMs >= 2000 ? CheckStatus.Bad
                    : net.Freezes >= 3 || net.LongestFreezeMs >= 1000 ? CheckStatus.Warn
                    : CheckStatus.Info;
                var msg = $"{net.Freezes} pause nella ricezione dei dati dal server (la più lunga {N0(net.LongestFreezeMs)} ms): " +
                          "in gioco si sentono come lag o rubber-banding (giocatori che si teletrasportano, colpi non registrati).";
                if (corr.StutterSeconds == 0 || corr.FreezeSeconds >= corr.StutterSeconds)
                    msg += $" In questa sessione i freeze di rete ({corr.FreezeSeconds} s) pesano più degli scatti degli FPS ({corr.StutterSeconds} s).";
                else if (corr.StutterSeconds >= 2 * corr.FreezeSeconds)
                    msg += $" Gli scatti degli FPS ({corr.StutterSeconds} s) sono più frequenti dei freeze di rete ({corr.FreezeSeconds} s): la priorità è il PC.";
                else
                    msg += $" Scatti degli FPS ({corr.StutterSeconds} s) e freeze di rete ({corr.FreezeSeconds} s) hanno un peso simile.";
                if (corr.FreezeSeconds >= 3 && corr.FreezeWithStutter * 2 >= corr.FreezeSeconds)
                    msg += " Molti freeze coincidono con uno scatto dei frame: potrebbe essere un blocco dell'intero PC (driver, DPC) più che della rete.";
                list.Add(new PerfInsight
                {
                    Severity = sev,
                    Title = "Freeze di rete (lag)",
                    Message = msg,
                    Hint = homeProblem
                        ? "Il router stesso risponde in modo irregolare: inizia dalla rete di casa (cavo, Wi-Fi a 5 GHz, altri dispositivi)."
                        : "Chiudi i download in background e prova il cavo. Se router e Internet restano stabili ma i freeze continuano, " +
                          "la causa è il percorso verso il server o il server stesso."
                });
            }
            else if (net.AvgPacketsInPerSec > 0 && st.Stutters >= 10)
            {
                list.Add(new PerfInsight
                {
                    Severity = CheckStatus.Info,
                    Title = "Nessun freeze di rete",
                    Message = $"Il server ha inviato dati senza interruzioni, mentre gli FPS hanno avuto {st.Stutters} stutter.",
                    Hint = "Gli scatti che senti vengono dal PC, non dalla connessione: guarda i suggerimenti sugli stutter."
                });
            }

            // ---- pacchetti dal server ----
            var inPkts = seconds.Where(s => s.PacketsInPerSec is >= 1).Select(s => s.PacketsInPerSec!.Value).ToList();
            if (inPkts.Count >= 60)
            {
                double med = NetStats.Median(inPkts);
                if (med < 20)
                    list.Add(new PerfInsight
                    {
                        Severity = CheckStatus.Info,
                        Title = "Pochi aggiornamenti dal server",
                        Message = $"Mediana di {N0(med)} pacchetti al secondo ricevuti dal server (in {inPkts.Count} secondi con traffico).",
                        Hint = "Durante una partita di solito il server ne invia di più: valori bassi sono normali in menu, attese o modalità tranquille, " +
                               "ma se coincidono con lag possono indicare una connessione congestionata."
                    });
            }
        }

        private static void AddProcesses(List<PerfInsight> list, PerfSession session)
        {
            var heavy = (session.TopProcesses ?? new List<ProcessUsage>())
                .Where(p => p != null && p.AvgCpuPct > 5)
                .OrderByDescending(p => p.AvgCpuPct)
                .Take(3)
                .ToList();
            if (heavy.Count == 0) return;
            var names = string.Join(", ", heavy.Select(p =>
                $"{p.Name} ({N0(p.AvgCpuPct)}% CPU in media, picco {N0(p.MaxCpuPct)}%{(p.AvgRamMb >= 300 ? $", {N1(p.AvgRamMb / 1024.0)} GB di RAM" : "")})"));
            list.Add(new PerfInsight
            {
                Severity = CheckStatus.Warn,
                Title = "Programmi in background pesanti",
                Message = "Durante la sessione: " + names + ".",
                Hint = "Se non ti servono mentre giochi chiudili (o rimandane il lavoro): tolgono CPU al gioco e possono causare scatti. " +
                       "Le percentuali sono sul totale della CPU."
            });
        }

        /// <summary>"ping 35 → 48 ms, jitter 2 → 5 ms, perdita 0 → 1,2%" se entrambe le sessioni hanno la rete.</summary>
        private static string NetworkDelta(PerfSession cur, PerfSession prev)
        {
            var c = cur.Network?.Game;
            var p = prev.Network?.Game;
            if (c?.AvgMs == null || p?.AvgMs == null) return "";
            var parts = new List<string> { $"ping {N0(p.AvgMs.Value)} → {N0(c.AvgMs.Value)} ms" };
            if (c.JitterMs.HasValue && p.JitterMs.HasValue) parts.Add($"jitter {N1(p.JitterMs.Value)} → {N1(c.JitterMs.Value)} ms");
            parts.Add($"perdita {N1(p.LossPct)} → {N1(c.LossPct)}%");
            var text = "Rete: " + string.Join(", ", parts);
            if (!string.Equals(cur.Network!.PingTargetKind, prev.Network!.PingTargetKind, StringComparison.OrdinalIgnoreCase))
                text += " (misurati verso bersagli diversi: server in una, regione Epic nell'altra)";
            return text + ".";
        }

        private static string Mbps(double kbps) => N1(kbps / 1000.0);

        // ---- formattazione ----

        private static double Rel(double cur, double baseline) => baseline > 0 ? (cur - baseline) / baseline : 0;

        /// <summary>Percentuale con segno tipografico: "+12%", "−35%", "±0%".</summary>
        private static string Pct(double rel)
        {
            var p = Math.Round(rel * 100);
            if (p == 0) return "±0%";
            return (p > 0 ? "+" : "−") + Math.Abs(p).ToString("0", Inv) + "%";
        }

        private static string N0(double v) => double.IsFinite(v) ? Math.Round(v).ToString("0", Inv) : "–";
        private static string N1(double v) => double.IsFinite(v) ? v.ToString("0.#", Inv).Replace('.', ',') : "–";

        private static string CapText(double? cap) => cap is > 0 ? N0(cap.Value) : "illimitato";

        private static string DurationText(double sec) =>
            sec >= 3600 ? TimeSpan.FromSeconds(sec).ToString(@"h\:mm\:ss", Inv) : TimeSpan.FromSeconds(sec).ToString(@"m\:ss", Inv);

        private static string DisplayName(string? process) =>
            string.IsNullOrWhiteSpace(process) ? "Gioco" :
            process.StartsWith("FortniteClient", StringComparison.OrdinalIgnoreCase) ? "Fortnite" : process;
    }
}
