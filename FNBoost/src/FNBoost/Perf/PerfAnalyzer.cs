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

        public static List<PerfInsight> Analyze(PerfSession session, IReadOnlyList<PerfSession> history, IReadOnlyList<float>? frametimes = null)
        {
            var list = new List<PerfInsight>();
            if (session == null) return list;
            var st = session.Stats;
            if ((st == null || !st.HasData) && frametimes != null && frametimes.Count >= 2)
                st = FrameStats.Compute(frametimes);
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
            var active = seconds.Where(s => s.Fps > 0).ToList();

            AddOverall(list, st);
            AddStutterRate(list, st);
            AddShaderCompilation(list, session, st, seconds, frametimes);

            // ---- Limite FPS: cap, VSync/refresh, sotto il refresh ----
            double avg = st.AvgFps;
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
