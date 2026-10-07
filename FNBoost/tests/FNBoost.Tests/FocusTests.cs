using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FNBoost.Core;
using FNBoost.Perf;
using FNBoost.Report;

namespace FNBoost.Tests
{
    /// <summary>
    /// Test dell'esclusione dei frame con il gioco fuori fuoco, del log limitato alla sessione, del giudizio
    /// "il PC va bene" e dei valori della modalità Performance. Riproducono il primo report reale di un utente:
    /// 7 s a 30 FPS (FN Boost in primo piano), 147 s a 165 FPS di gioco con due hitch (35,7 e 47 ms), 8 s a 30 FPS.
    /// </summary>
    internal static class FocusTests
    {
        private const string Fn = "FortniteClient-Win64-Shipping";

        public static void RunAll()
        {
            Console.WriteLine("FocusFilter");
            T.Run("cronologia del primo piano: margine, assestamento, intervallo aperto, pulizia", TimelineBasics);
            T.Run("controllo del primo piano arrivato in ritardo: nessun finto cambio di fuoco", TimelineStalePoll);
            T.Run("maschera: frame dentro, fuori e a cavallo del cambio di fuoco", MaskBasics);
            T.Run("intervalli di frame esclusi: andata e ritorno", RangesRoundtrip);
            T.Run("report reale (frame puliti): statistiche = solo gioco, regolarità alta", EvidenceClean);
            T.Run("report reale (frametime irregolari come quelli veri): 1% low ~105, ≤ 2 stutter", EvidenceRealistic);
            T.Run("sessione vecchia: tratto in secondo piano riconosciuto e corretto", LegacyRepair);
            T.Run("analisi: niente \"Frame molto irregolari\" per il gioco in secondo piano", AnalyzerHonesty);

            Console.WriteLine("Log limitato alla sessione");
            T.Run("periodo della sessione in UTC (orari del log di Unreal)", SessionWindowConversion);
            T.Run("conteggi sessione/intero log, RHI e versione dell'avvio giusto", LogWindowCounts);
            T.Run("solo i segnali della sessione diventano consigli", LogWindowRecommendations);
            T.Run("confine tra file: il log corrente tagliato non eredita versione e RHI del backup", LogFileBoundary);

            Console.WriteLine("Report e modalità Performance");
            T.Run("sessione buona: verdetto chiaro e solo miglioramenti facoltativi", GoodSessionReport);
            T.Run("sessione da migliorare: niente verdetto positivo", BadSessionVerdict);
            T.Run("sessione vecchia senza frametime: nessun giudizio sugli FPS falsati", LegacyVerdictWithoutFrametimes);
            T.Run("sessione di un altro gioco: verdetto e testi senza Fortnite", OtherGameTexts);
            T.Run("modalità Performance = dx11 + es31, DirectX 12 = dx12 + sm6", PerformanceModeIni);
        }

        // ================= Dati sintetici del report reale =================

        private sealed class Evidence
        {
            public float[] Ft = Array.Empty<float>();
            /// <summary>Fine di ogni frame sull'orologio ETW (ms).</summary>
            public double[] Ts = Array.Empty<double>();
            /// <summary>Istanti (orologio ETW) in cui il gioco torna in primo piano e lo perde.</summary>
            public double FocusIn, FocusOut;
            /// <summary>Indici dei frame di gioco vero (iniziati dopo FocusIn e finiti prima di FocusOut).</summary>
            public int GameFirst, GameLast;
        }

        private const double EtwBase = 1000;

        /// <param name="realistic">true = frametime con la coda dei frame lenti del log reale (1% low ~105 FPS).</param>
        private static Evidence MakeEvidence(bool realistic)
        {
            var rnd = new Random(1234);
            var ft = new List<float>();
            var ts = new List<double>();
            double t = 0;
            void Add(float f)
            {
                ft.Add(f);
                t += f;
                ts.Add(EtwBase + t);
            }

            var e = new Evidence();
            while (t < 7000) Add(1000f / 30f);
            e.FocusIn = EtwBase + t;
            e.GameFirst = ft.Count;
            double gameEnd = t + 147000;
            bool h1 = false, h2 = false;
            while (t < gameEnd)
            {
                float f = 1000f / 165f;
                if (realistic)
                {
                    double u = rnd.NextDouble();
                    // ~98,4% di frame regolari con un po' di rumore, ~1,6% di frame lenti (7,5-10 ms) come nel log reale.
                    f = u < 0.016 ? (float)(7.5 + rnd.NextDouble() * 2.5) : (float)Math.Clamp(6.06 + (rnd.NextDouble() - 0.5) * 0.9, 4.5, 7.5);
                }
                if (!h1 && t >= 75000) { f = 35.7f; h1 = true; }
                else if (!h2 && t >= 136100) { f = 47f; h2 = true; }
                Add(f);
            }
            e.GameLast = ft.Count - 1;
            e.FocusOut = EtwBase + t;
            while (t < gameEnd + 8000) Add(1000f / 30f);
            e.Ft = ft.ToArray();
            e.Ts = ts.ToArray();
            return e;
        }

        /// <summary>Controlli del primo piano ogni 100 ms sull'orologio interno (= ETW + realOffset).</summary>
        private static ExclusionInterval[] Polls(Evidence e, double realOffset)
        {
            var tl = new FocusTimeline();
            double end = e.Ts[^1] + realOffset + 500;
            for (double clock = 0; clock <= end; clock += 100)
            {
                double etw = clock - realOffset;
                tl.Add(clock, etw >= e.FocusIn && etw < e.FocusOut);
            }
            return tl.Snapshot();
        }

        private static (SessionFrames Frames, bool[] Mask) Build(Evidence e)
        {
            const double realOffset = 20;
            var intervals = Polls(e, realOffset);
            // Offset stimato dal ritardo minimo di ricezione: qualche ms più del vero.
            var mask = FocusFilter.ExcludedMask(e.Ts, e.Ft, intervals, realOffset + 3);
            return (FocusFilter.BuildSession(e.Ft, e.Ts, mask, 2.5, 12), mask);
        }

        private static FrameStatsResult GroundTruth(Evidence e) =>
            FrameStats.Compute(e.Ft.Skip(e.GameFirst).Take(e.GameLast - e.GameFirst + 1).ToArray());

        // ================= FocusFilter =================

        private static void TimelineBasics()
        {
            var tl = new FocusTimeline();
            T.True(tl.Focused && !tl.HasData, "senza controlli: nessuna esclusione");
            T.Equal(0, tl.Snapshot().Length, "nessun intervallo");
            tl.Add(0, true);
            tl.Add(100, true);
            tl.Add(200, false); // perso tra 100 e 200
            T.True(!tl.Focused, "fuori fuoco");
            var open = tl.Snapshot();
            T.Equal(1, open.Length, "intervallo aperto");
            T.Near(100 - FocusTimeline.LeadMarginMs, open[0].StartMs, 1e-9, "parte dall'ultimo controllo in primo piano meno il margine");
            T.True(double.IsPositiveInfinity(open[0].EndMs), "aperto fino a +∞");
            T.True(tl.IsExcludedAt(5000), "dentro l'intervallo aperto");
            tl.Add(300, false);
            tl.Add(400, true); // tornato tra 300 e 400
            var closed = tl.Snapshot();
            T.Equal(1, closed.Length, "intervallo chiuso");
            T.Near(400 + FocusTimeline.SettleMs, closed[0].EndMs, 1e-9, "fine = ritorno + assestamento");
            T.Near(400, tl.FocusedSinceMs, 1e-9, "in primo piano da 400");
            T.True(tl.IsExcludedAt(850) && !tl.IsExcludedAt(950) && !tl.IsExcludedAt(-100), "assestamento compreso, poi non più");
            tl.Add(350, false); // istante all'indietro: lettura arrivata fuori ordine, ignorata
            T.True(tl.Focused && tl.ClosedCount == 1, "lettura fuori ordine ignorata");
            tl.Add(500, false);
            T.True(!tl.Focused, "perso di nuovo");
            tl.Add(2000, true);
            T.Equal(2, tl.ClosedCount, "due intervalli chiusi");
            tl.TrimBefore(1000);
            T.Equal(1, tl.ClosedCount, "pulizia degli intervalli finiti");
            tl.Reset();
            T.True(!tl.HasData && tl.Snapshot().Length == 0, "reset");
        }

        private static void TimelineStalePoll()
        {
            // Scenario della review: il callback A legge "fuori fuoco" a 1000 ms ma prende il lock dopo B, che ha letto
            // "in primo piano" a 1100 ms. La lettura di A è superata e non deve aprire un intervallo.
            var tl = new FocusTimeline();
            for (double t = 0; t <= 900; t += 100) tl.Add(t, true);
            tl.Add(1100, true); // B
            tl.Add(1000, false); // A, in ritardo
            tl.Add(1200, true);
            T.True(tl.Focused && tl.Snapshot().Length == 0, "nessun intervallo escluso");
            T.True(!tl.IsExcludedAt(1100) && !tl.IsExcludedAt(1500), "il gioco in primo piano resta contato");

            // Al contrario: una lettura "in primo piano" in ritardo non chiude l'intervallo aperto.
            var tl2 = new FocusTimeline();
            tl2.Add(0, true);
            tl2.Add(100, false);
            tl2.Add(300, false);
            tl2.Add(200, true); // in ritardo
            T.True(!tl2.Focused && tl2.ClosedCount == 0, "resta fuori fuoco");
            T.True(tl2.IsExcludedAt(400), "i frame in secondo piano restano esclusi");
        }

        private static void MaskBasics()
        {
            // Frame da 10 ms che finiscono a 10, 20, … 100 (orologio ETW); orologio interno = ETW + 5.
            var ts = Enumerable.Range(1, 10).Select(i => i * 10.0).ToArray();
            var ft = Enumerable.Repeat(10f, 10).ToArray();
            var intervals = new[] { new ExclusionInterval(37, 52), new ExclusionInterval(95, double.PositiveInfinity) };
            var m = FocusFilter.ExcludedMask(ts, ft, intervals, 5);
            // Frame [25,35]→no, [35,45]→sì (a cavallo), [45,55]→sì, [55,65]→no, …, [85,95]→sì (tocca 95), [95,105]→sì.
            T.Equal("0,0,0,1,1,0,0,0,1,1", string.Join(",", m.Select(b => b ? 1 : 0)), "maschera");
            T.True(FocusFilter.ExcludedMask(ts, ft, null, 0).All(b => !b), "senza intervalli nessuna esclusione");
            T.True(FocusFilter.ExcludedMask(ts, ft, Array.Empty<ExclusionInterval>(), double.NaN).All(b => !b), "offset NaN tollerato");
            T.Equal(6, FocusFilter.Keep(ft, m).Length, "solo i frame tenuti");
        }

        private static void RangesRoundtrip()
        {
            var mask = new[] { true, true, false, false, true, false, true, true, true };
            var r = FocusFilter.ToRanges(mask);
            T.Equal("0+2,4+1,6+3", string.Join(",", r.Select(x => $"{x.Start}+{x.Count}")), "intervalli");
            var back = FocusFilter.MaskFromRanges(mask.Length, r)!;
            T.True(back.SequenceEqual(mask), "andata e ritorno");
            T.True(FocusFilter.MaskFromRanges(5, null) == null && FocusFilter.MaskFromRanges(5, new List<FrameRange>()) == null, "nessun intervallo → null");
            var clipped = FocusFilter.MaskFromRanges(3, new List<FrameRange> { new() { Start = 2, Count = 10 }, new() { Start = -4, Count = 5 } })!;
            T.Equal("1,0,1", string.Join(",", clipped.Select(b => b ? 1 : 0)), "intervalli fuori misura tagliati");
            var s = new PerfSession { ExcludedRanges = r };
            T.Equal(3, FocusFilter.FocusedFrametimes(Enumerable.Repeat(1f, 9).ToArray(), s).Length, "frametime in primo piano");
        }

        private static void EvidenceClean()
        {
            var e = MakeEvidence(realistic: false);
            var whole = FrameStats.Compute(e.Ft);
            T.True(whole.Low1Fps < 35 && whole.ConsistencyScore < 10, $"senza esclusione i numeri sono quelli sbagliati del report (1% low {whole.Low1Fps:0}, regolarità {whole.ConsistencyScore:0})");
            T.True(whole.Stutters >= 1, "e il passaggio 165 → 30 FPS sembra uno stutter");

            var (frames, mask) = Build(e);
            var st = frames.Stats;
            var gt = GroundTruth(e);
            T.Near(165, st.AvgFps, 1.0, "media ~165");
            T.Near(gt.AvgFps, st.AvgFps, 0.3, "media = solo gioco");
            T.True(st.Low1Fps >= 150, $"1% low alto ({st.Low1Fps:0})");
            T.Near(gt.Low1Fps, st.Low1Fps, gt.Low1Fps * 0.02, "1% low = solo gioco");
            T.Equal(2, st.Stutters, "solo i due hitch veri");
            T.Near(47, st.MaxFrametimeMs, 0.01, "frame più lungo 47 ms");
            T.True(st.ConsistencyScore >= 90, $"regolarità alta ({st.ConsistencyScore:0})");
            T.True(frames.UnfocusedSec >= 15 && frames.UnfocusedSec <= 16.5, $"~15 s fuori fuoco esclusi ({frames.UnfocusedSec:0.00})");
            T.True(mask.Take(e.GameFirst).All(x => x) && mask.Skip(e.GameLast + 1).All(x => x), "tutti i frame a 30 FPS esclusi");
            int lostGame = mask.Skip(e.GameFirst).Take(e.GameLast - e.GameFirst + 1).Count(x => x);
            T.True(lostGame > 0 && lostGame < 165, $"del gioco vero si perde meno di 1 s (assestamento e margine): {lostGame} frame");
            T.Equal(frames.ExcludedFrames, mask.Count(x => x), "frame esclusi contati");
            T.Equal(2, frames.ExcludedRanges.Count, "due tratti esclusi");

            // Campioni al secondo: tratti fuori fuoco marcati (FPS 0 → buco nei grafici), niente stutter fasulli.
            var secs = frames.Seconds;
            T.True(secs.Count >= 161 && secs.Count <= 163, $"~162 secondi ({secs.Count})");
            T.True(secs.Take(7).All(x => x.Unfocused && x.Fps == 0 && x.Stutters == 0), "secondi 0-6 fuori fuoco");
            T.True(secs.Skip(155).All(x => x.Unfocused), "ultimi secondi fuori fuoco");
            T.True(secs.Skip(9).Take(140).All(x => !x.Unfocused && Math.Abs(x.Fps - 165) <= 8), "gioco a ~165 FPS");
            T.Equal(2, secs.Sum(x => x.Stutters), "stutter al secondo = 2");
            T.True(secs.Where(x => !x.Unfocused).All(x => x.Fps > 100), "nessun secondo \"misto\" con FPS bassi");

            // Senza maschera (misura del primo piano non disponibile) tutto resta come prima.
            var plain = FocusFilter.BuildSession(e.Ft, e.Ts, null, 2.5, 12);
            T.Near(whole.AvgFps, plain.Stats.AvgFps, 1e-9, "senza maschera = statistiche di tutti i frame");
            T.True(plain.Seconds.All(x => !x.Unfocused) && plain.ExcludedFrames == 0, "nessun secondo marcato");
            T.Equal(30, (int)Math.Round(plain.Seconds[0].Fps), "secondo 0 a 30 FPS");
        }

        private static void EvidenceRealistic()
        {
            var e = MakeEvidence(realistic: true);
            var (frames, _) = Build(e);
            var st = frames.Stats;
            var gt = GroundTruth(e);
            T.True(st.AvgFps >= 160 && st.AvgFps <= 167, $"media ~165 ({st.AvgFps:0.0})");
            T.True(st.Low1Fps >= 95 && st.Low1Fps <= 120, $"1% low ~105 ({st.Low1Fps:0.0})");
            T.Near(gt.Low1Fps, st.Low1Fps, gt.Low1Fps * 0.02, "1% low = solo gioco");
            T.Near(gt.Low01Fps, st.Low01Fps, gt.Low01Fps * 0.05, "0,1% low = solo gioco");
            T.True(st.Stutters <= 2, $"al massimo 2 stutter ({st.Stutters})");
            T.True(st.StuttersPerMin <= 1, "meno di uno stutter al minuto");
            T.True(st.Low1Fps / st.AvgFps >= 0.6, "1% low ≥ 60% della media");
            T.True(st.ConsistencyScore > 40, $"regolarità non più a 0 ({st.ConsistencyScore:0})");
            var whole = FrameStats.Compute(e.Ft);
            T.True(whole.Low1Fps < 35, "con il tratto in secondo piano 1% low ~30 (il bug del report)");
        }

        /// <summary>Sessione "vecchia" (senza misura del primo piano) costruita come faceva PerfService prima.</summary>
        private static PerfSession LegacySession(Evidence e, out float[] ft)
        {
            var plain = FocusFilter.BuildSession(e.Ft, e.Ts, null, 2.5, 12);
            foreach (var x in plain.Seconds)
            {
                bool bg = x.Fps <= 38;
                x.GpuPercent = bg ? 4.5 : 26;
                x.CpuPercent = bg ? 10 : 18;
                x.RamPercent = 55;
            }
            // Come nel report reale il contatore GPU è in ritardo di un secondo ai bordi del tratto.
            plain.Seconds[6].GpuPercent = 15.3;
            plain.Seconds[154].GpuPercent = 14.8;
            ft = e.Ft;
            return new PerfSession
            {
                Id = "20261007-121400-abcd",
                StartedAt = new DateTime(2026, 10, 7, 12, 14, 0),
                ProcessName = Fn,
                DurationSec = plain.DurationSec,
                Stats = plain.Stats,
                Seconds = plain.Seconds,
                FpsCap = 165,
                RefreshHz = 165,
                RenderMode = "Performance"
            };
        }

        private static void LegacyRepair()
        {
            var e = MakeEvidence(realistic: true);
            var old = LegacySession(e, out var ft);
            var seg = FocusFilter.DetectBackground(old.Seconds);
            T.True(seg != null, "tratto riconosciuto");
            T.Equal(7, seg!.LeadSeconds, "7 s iniziali");
            T.Equal(8, seg.TrailSeconds, "8 s finali");
            T.True(seg.CoreMedianFps >= 160, "il resto va a ~165 FPS");

            var fixedS = FocusFilter.RepairLegacy(old, ft);
            T.True(fixedS != null, "sessione corretta");
            if (fixedS == null) return;
            T.True(!ReferenceEquals(fixedS, old) && old.UnfocusedSec == 0 && !old.Seconds[0].Unfocused, "l'originale non viene toccato");
            T.True(fixedS.UnfocusedEstimated && !fixedS.FocusTracked, "stima dichiarata");
            var gt = GroundTruth(e);
            T.True(fixedS.Stats.AvgFps >= 160, $"media sul gioco vero ({fixedS.Stats.AvgFps:0})");
            T.Near(gt.Low1Fps, fixedS.Stats.Low1Fps, gt.Low1Fps * 0.03, "1% low sul gioco vero");
            T.True(fixedS.Stats.Stutters <= 2, "≤ 2 stutter");
            T.True(fixedS.UnfocusedSec >= 15 && fixedS.UnfocusedSec <= 18, $"~15-17 s esclusi ({fixedS.UnfocusedSec:0.0})");
            T.True(fixedS.Seconds.Take(7).All(x => x.Unfocused) && fixedS.Seconds.Skip(fixedS.Seconds.Count - 8).All(x => x.Unfocused), "secondi del tratto marcati");
            T.True(FocusFilter.RepairLegacy(fixedS, ft) == null, "non si corregge due volte");
            T.True(FocusFilter.RepairLegacy(old, null) == null, "senza frametime niente correzione");
            T.True(FocusFilter.RepairLegacy(new PerfSession { FocusTracked = true, Seconds = old.Seconds }, ft) == null, "sessioni nuove: misura vera, nessuna stima");

            // Senza GPU, con un tratto troppo corto o con il gioco vero lento: nessuna conclusione.
            var noGpu = old.Seconds.Select(x => new SecondSample { T = x.T, Fps = x.Fps }).ToList();
            T.True(FocusFilter.DetectBackground(noGpu) == null, "senza dati GPU non si conclude nulla");
            var shortSeg = old.Seconds.Select(x => new SecondSample { T = x.T, Fps = x.T < 2 ? 30 : 165, GpuPercent = x.T < 2 ? 4 : 30 }).ToList();
            T.True(FocusFilter.DetectBackground(shortSeg) == null, "2 s non bastano");
            var slowGame = old.Seconds.Select(x => new SecondSample { T = x.T, Fps = x.T < 7 ? 30 : 40, GpuPercent = x.T < 7 ? 4 : 99 }).ToList();
            T.True(FocusFilter.DetectBackground(slowGame) == null, "gioco vero a 40 FPS: non è un tratto in secondo piano");
        }

        private static void AnalyzerHonesty()
        {
            var e = MakeEvidence(realistic: true);
            var old = LegacySession(e, out var ft);

            // Prima: la sessione vecchia "grezza" dava Frame molto irregolari + Molti stutter.
            var withFt = PerfAnalyzer.Analyze(old, new[] { old }, ft);
            T.True(withFt.Any(i => i.Title == "Gioco in secondo piano escluso"), "spiega l'esclusione stimata");
            T.True(!withFt.Any(i => i.Title == "Frame molto irregolari" || i.Title == "Molti stutter" || i.Title == "Qualche stutter"), "nessun falso allarme");
            T.True(withFt.Any(i => i.Title == "FPS limitati dal cap"), "FPS al limite di 165");
            T.True(!withFt.Any(i => i.Severity == CheckStatus.Bad), "nessun Problema");
            var msg = withFt.First(i => i.Title == "Gioco in secondo piano escluso").Message;
            T.Contains(msg, "Nei primi 7 s e negli ultimi 8 s", "tratti nominati");

            var noFt = PerfAnalyzer.Analyze(old, new[] { old });
            T.True(noFt.Any(i => i.Title == "Probabile gioco in secondo piano"), "senza frametime: lo dice");
            T.True(!noFt.Any(i => i.Title == "Frame molto irregolari" || i.Title == "Molti stutter"), "e non incolpa il PC");

            // Sessione nuova con la misura del primo piano.
            var (frames, _) = Build(e);
            var tracked = new PerfSession
            {
                Id = "n", StartedAt = old.StartedAt, ProcessName = Fn, DurationSec = frames.DurationSec, Stats = frames.Stats,
                Seconds = frames.Seconds, FpsCap = 165, RefreshHz = 165, FocusTracked = true, UnfocusedSec = frames.UnfocusedSec,
                ExcludedFrames = frames.ExcludedFrames, ExcludedRanges = frames.ExcludedRanges
            };
            var ins = PerfAnalyzer.Analyze(tracked, new[] { tracked }, e.Ft);
            T.True(ins.Any(i => i.Title == "Tempo fuori fuoco escluso" && i.Severity == CheckStatus.Info), "tempo fuori fuoco spiegato");
            T.True(!ins.Any(i => i.Severity == CheckStatus.Bad), "nessun Problema");
            T.True(!ins.Any(i => i.Title.Contains("secondo piano")), "nessuna stima: la misura è vera");
        }

        // ================= Log =================

        private static string Lu(DateTime utc, string body, int frame = 1) =>
            $"[{utc:yyyy.MM.dd-HH.mm.ss}:{utc.Millisecond:000}][{frame,3}]{body}";

        private static readonly DateTime SessionStartUtc = new(2026, 10, 7, 10, 14, 0, DateTimeKind.Utc);

        private static List<string> TwoRunLog()
        {
            var lines = new List<string>();
            // Avvio precedente (backup): sera prima, DirectX 12, molti timeout e hitch.
            var b = new DateTime(2026, 10, 6, 22, 43, 56);
            lines.Add("Log file open, 10/07/26 00:43:50");
            lines.Add("LogInit: Display: Build: ++Fortnite+Release-42.20-CL-11111111");
            lines.Add("LogRHI: Checking if RHI D3D12 with Feature Level SM6 is supported by your system.");
            lines.Add("LogRHI: RHI D3D12 with Feature Level SM6 is supported and will be used.");
            for (int i = 0; i < 17; i++) lines.Add(Lu(b.AddMinutes(i), "LogNet: Warning: Connection TIMEOUT! RemoteAddr: 34.1.2.3:7777"));
            for (int i = 0; i < 129; i++) lines.Add(Lu(b.AddSeconds(30 + i), "LogStreaming: Warning: Detected hitch of 120.5ms during level streaming"));
            for (int i = 0; i < 500; i++) lines.Add(Lu(b.AddSeconds(10 + i % 300), "LogFort: Error: something noisy " + i));
            lines.Add(Lu(b.AddMinutes(16), "LogWindows: Error: Fatal error: [File:D3D12Util.cpp] GPU crashed or D3D Device Removed. DXGI_ERROR_DEVICE_REMOVED"));

            // Avvio della sessione: Performance (D3D11 · ES3_1). Le prime righe sono senza orario.
            lines.Add("Log file open, 10/07/26 12:12:30");
            lines.Add("LogInit: Display: Build: ++Fortnite+Release-42.30-CL-58813929");
            lines.Add("LogRHI: Checking if RHI D3D11 with Feature Level ES3_1 is supported by your system.");
            lines.Add("LogD3D11RHI: D3D11 adapters:");
            lines.Add("LogRHI: RHI D3D11 with Feature Level ES3_1 is supported and will be used.");
            var m = new DateTime(2026, 10, 7, 10, 12, 33);
            lines.Add(Lu(m, "LogInit: Display: Starting Game."));
            for (int i = 0; i < 300; i++) lines.Add(Lu(m.AddSeconds(i % 20), "LogFortQuest: Warning: quest noise " + i));
            // Durante la sessione (10:14:00 → 10:16:42): un hitch, un timeout, qualche avviso.
            lines.Add(Lu(SessionStartUtc.AddSeconds(75), "LogStreaming: Warning: Detected hitch of 35.7ms during level streaming"));
            lines.Add(Lu(SessionStartUtc.AddSeconds(90), "LogNet: Warning: Connection TIMEOUT! RemoteAddr: 34.1.2.3:7777"));
            lines.Add("    riga di continuazione senza orario (nel periodo)");
            for (int i = 0; i < 4; i++) lines.Add(Lu(SessionStartUtc.AddSeconds(100 + i), "LogFort: Error: in-session noise"));
            // Dopo la sessione.
            for (int i = 0; i < 30; i++) lines.Add(Lu(SessionStartUtc.AddMinutes(4).AddSeconds(i), "LogStreaming: Warning: Detected hitch of 80.1ms during level streaming"));
            return lines;
        }

        private static (DateTime, DateTime) Window() =>
            FortniteLogAnalyzer.SessionWindowUtc(SessionStartUtc.ToLocalTime(), 162.6);

        private static void SessionWindowConversion()
        {
            var (a, b) = Window();
            T.Equal(new DateTime(2026, 10, 7, 10, 13, 0), a, "inizio = ora locale convertita in UTC − 60 s");
            T.Equal(new DateTime(2026, 10, 7, 10, 17, 42, 600), b, "fine = inizio + durata + 60 s");
            var (c, _) = FortniteLogAnalyzer.SessionWindowUtc(SessionStartUtc, 10, 0);
            T.Equal(new DateTime(2026, 10, 7, 10, 14, 0), c, "un istante già UTC resta com'è");
            var (_, e) = FortniteLogAnalyzer.SessionWindowUtc(SessionStartUtc, double.NaN, 0);
            T.Equal(new DateTime(2026, 10, 7, 10, 14, 0), e, "durata non valida = 0");
        }

        private static void LogWindowCounts()
        {
            var (ws, we) = Window();
            var f = FortniteLogAnalyzer.Analyze(TwoRunLog(), new SanitizeContext(), ws, we);
            T.True(f.Session != null && f.WindowStart == ws && f.WindowEnd == we, "periodo registrato");
            var s = f.Session!;
            T.Equal(1, s.Hitches, "hitch nella sessione");
            T.Equal(160, f.Hitches, "hitch nell'intero log (contesto)");
            T.Equal(1, s.NetworkIssues, "rete nella sessione");
            T.Equal(18, f.NetworkIssues, "rete nell'intero log");
            T.Equal(0, s.CrashMarkers, "nessun crash nella sessione");
            T.Equal(1, f.CrashMarkers, "crash dell'avvio precedente nel contesto");
            T.Equal(4, s.Errors, "errori nella sessione");
            T.Equal(2, s.Warnings, "avvisi nella sessione");
            T.True(s.Lines == 7, $"righe nel periodo, continuazione compresa ({s.Lines})");
            T.Equal("D3D11 · ES3_1", f.RhiInUse, "API grafica dell'avvio della sessione");
            T.Equal("++Fortnite+Release-42.30-CL-58813929", f.GameBuild, "versione dell'avvio della sessione");
            T.True(f.GpuInfo.Any(g => g.Contains("will be used")), "riga RHI tra le info GPU");
            T.True(f.Highlights[0].InSession, "prima le righe della sessione");
            var hitch = f.Highlights.First(h => h.Kind == "hitch" && h.InSession);
            T.True(hitch.SessionCount >= 1 && hitch.Count > hitch.SessionCount, "gruppo con righe dentro e fuori sessione");
            var text = FortniteLogAnalyzer.FormatHighlights(f);
            T.Contains(text, "· sessione]", "etichetta sessione");
            T.Contains(text, "· fuori sessione]", "etichetta contesto");

            // Periodo nell'avvio precedente → versione e RHI di quell'avvio.
            var old = FortniteLogAnalyzer.Analyze(TwoRunLog(), new SanitizeContext(),
                new DateTime(2026, 10, 6, 22, 44, 0), new DateTime(2026, 10, 6, 22, 50, 0));
            T.Equal("D3D12 · SM6", old.RhiInUse, "RHI dell'avvio precedente");
            T.Equal("++Fortnite+Release-42.20-CL-11111111", old.GameBuild, "versione dell'avvio precedente");
            T.True(old.Session!.Hitches > 100, "lì gli hitch c'erano");

            // Senza periodo: tutto il log, ultimo avvio.
            var all = FortniteLogAnalyzer.Analyze(TwoRunLog(), new SanitizeContext());
            T.True(all.Session == null && all.WindowStart == null, "nessun periodo");
            T.Equal("D3D11 · ES3_1", all.RhiInUse, "ultimo avvio");
            T.True(!FortniteLogAnalyzer.FormatHighlights(all).Contains("sessione]"), "nessuna etichetta senza periodo");

            // "Using Default RHI" come ripiego (log di versioni più vecchie).
            var legacy = FortniteLogAnalyzer.Analyze(new[] { "LogRHI: Display: Using Default RHI: D3D12" }, new SanitizeContext());
            T.Equal("D3D12", legacy.RhiInUse, "RHI predefinito");

            // Periodo che il log non copre.
            var none = FortniteLogAnalyzer.Analyze(TwoRunLog(), new SanitizeContext(), new DateTime(2026, 9, 1), new DateTime(2026, 9, 1, 0, 5, 0));
            T.Equal(0, none.Session!.Lines, "nessuna riga nel periodo");
        }

        private static void LogWindowRecommendations()
        {
            var (ws, we) = Window();
            var d = new ReportData { Log = FortniteLogAnalyzer.Analyze(TwoRunLog(), new SanitizeContext(), ws, we) };
            var recs = ReportBuilder.BuildRecommendations(d);
            T.True(!recs.Any(r => r.Title == "Hitch registrati dal gioco"), "160 hitch nel log ma 1 nella sessione: nessun consiglio");
            T.True(!recs.Any(r => r.Title == "Problemi di connessione nel log di Fortnite"), "timeout di ieri sera: nessun consiglio");
            T.True(!recs.Any(r => r.Severity == CheckStatus.Bad), "il crash di un avvio precedente non è un Problema della sessione");
            var crash = recs.SingleOrDefault(r => r.Title == "Segnali di crash nel log, fuori dalla sessione");
            T.True(crash != null && crash.Severity == CheckStatus.Info, "ma resta come informazione");

            // Senza sessione si usa tutto il log (e lo si dice).
            var whole = new ReportData { Log = FortniteLogAnalyzer.Analyze(TwoRunLog(), new SanitizeContext()) };
            var wr = ReportBuilder.BuildRecommendations(whole);
            var h = wr.SingleOrDefault(r => r.Title == "Hitch registrati dal gioco");
            T.True(h != null, "senza periodo: hitch dall'intero log");
            T.Contains(h!.Problem, "tutto il log", "detto esplicitamente");
            T.True(wr.Any(r => r.Title == "Crash della GPU nel log di Fortnite" && r.Severity == CheckStatus.Bad), "crash GPU senza periodo");
        }

        private static void LogFileBoundary()
        {
            // Righe come le passa AnalyzeDirectory: backup completo, poi il log corrente letto solo in coda
            // (troppo grande: niente "Log file open", niente LogInit/LogRHI).
            var all = TwoRunLog();
            int split = all.FindIndex(1, l => l.StartsWith("Log file open", StringComparison.Ordinal));
            var backup = all.Take(split).ToList();
            var mainTail = all.Skip(split).Where(l => l.StartsWith("[", StringComparison.Ordinal)).ToList();
            var lines = new List<string> { FortniteLogAnalyzer.FileStart };
            lines.AddRange(backup);
            lines.Add(FortniteLogAnalyzer.FileStartHeadMissing);
            lines.AddRange(mainTail);
            var (ws, we) = Window();
            var f = FortniteLogAnalyzer.Analyze(lines, new SanitizeContext(), ws, we);
            T.True(f.RhiInUse == null, $"RHI sconosciuta, non quella del backup ({f.RhiInUse})");
            T.True(f.GameBuild == null, $"versione sconosciuta, non quella del backup ({f.GameBuild})");
            T.Equal(backup.Count + mainTail.Count, f.TotalLines, "le righe sintetiche non contano");
            T.Equal(1, f.Session!.Hitches, "conteggi della sessione invariati");
            // Il backup resta riconosciuto se il periodo cade lì.
            var old = FortniteLogAnalyzer.Analyze(lines, new SanitizeContext(), new DateTime(2026, 10, 6, 22, 44, 0), new DateTime(2026, 10, 6, 22, 50, 0));
            T.Equal("D3D12 · SM6", old.RhiInUse, "RHI dell'avvio del backup");

            // File completo (con "Log file open"): versione e RHI ci sono.
            var whole = new List<string> { FortniteLogAnalyzer.FileStart };
            whole.AddRange(backup);
            whole.Add(FortniteLogAnalyzer.FileStart);
            whole.AddRange(all.Skip(split));
            var w = FortniteLogAnalyzer.Analyze(whole, new SanitizeContext(), ws, we);
            T.Equal("D3D11 · ES3_1", w.RhiInUse, "RHI del log corrente");
            T.Equal("++Fortnite+Release-42.30-CL-58813929", w.GameBuild, "versione del log corrente");

            // Su disco: log corrente oltre il limite di righe → letto in coda e senza ereditare dal backup.
            var dir = Path.Combine(Path.GetTempPath(), "fnboost-logtest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllLines(Path.Combine(dir, "FortniteGame-backup-2026.10.07-10.12.30.log"), backup);
                using (var sw = new StreamWriter(Path.Combine(dir, "FortniteGame.log")))
                {
                    foreach (var l in all.Skip(split).Take(5)) sw.WriteLine(l); // testa: Log file open, Build, RHI
                    int n = FortniteLogAnalyzer.MaxLines - FortniteLogAnalyzer.MaxLines / 5 + 1000;
                    for (int i = 0; i < n; i++) sw.WriteLine(Lu(SessionStartUtc.AddMilliseconds(i % 100000), "LogFort: x"));
                }
                var d = FortniteLogAnalyzer.AnalyzeDirectory(dir, new SanitizeContext(), true, ws, we);
                T.True(d.Truncated, "log corrente troncato");
                T.Equal(2, d.Sources.Count, "due file letti");
                T.True(d.RhiInUse == null && d.GameBuild == null, $"niente RHI/versione del backup ({d.RhiInUse} / {d.GameBuild})");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { /* temp */ }
            }
        }

        // ================= Report =================

        private static ReportData GoodReport()
        {
            var e = MakeEvidence(realistic: true);
            var (frames, _) = Build(e);
            var session = new PerfSession
            {
                Id = "20261007-121400-abcd", StartedAt = SessionStartUtc.ToLocalTime(), ProcessName = Fn, DurationSec = frames.DurationSec,
                Stats = frames.Stats, Seconds = frames.Seconds, FpsCap = 165, RefreshHz = 165, RenderMode = "Performance",
                FocusTracked = true, UnfocusedSec = frames.UnfocusedSec, ExcludedFrames = frames.ExcludedFrames, ExcludedRanges = frames.ExcludedRanges,
                Network = new NetworkSummary { PingTargetKind = "regione", RegionName = "Europa", Game = new PingStats { AvgMs = 37, JitterMs = 4.2, Sent = 160, Received = 160 }, ConnectionType = "Ethernet" }
            };
            foreach (var x in session.Seconds) { x.CpuPercent = 18; x.GpuPercent = x.Unfocused ? 4.5 : 26; x.RamPercent = 55; }
            var (ws, we) = Window();
            var log = FortniteLogAnalyzer.Analyze(TwoRunLog(), new SanitizeContext(), ws, we);
            var d = new ReportData
            {
                GeneratedAt = new DateTime(2026, 10, 7, 12, 18, 8),
                AppVersion = "1.0.0",
                Checks = { new ReportCheck { Title = "Velocità RAM (XMP)", Status = CheckStatus.Warn, Message = "4800 MT/s", Hint = "Attiva XMP" } },
                Fortnite = new ReportFortnite
                {
                    Installed = true, ConfigFound = true, RhiInUse = log.RhiInUse, GameBuild = log.GameBuild,
                    Settings = new FortniteSettingsSummary { RenderMode = "Prestazioni", FpsCap = 165, Reflex = 1, ResolutionX = 3440, ResolutionY = 1440, PreferredRhi = "dx11", PreferredFeatureLevel = "es31" }
                },
                Session = session,
                Insights = PerfAnalyzer.Analyze(session, new[] { session }, e.Ft),
                Histogram = ReportBuilder.BuildHistogram(FocusFilter.FocusedFrametimes(e.Ft, session)),
                Log = log
            };
            ReportBuilder.SanitizeInPlace(d, new SanitizeContext());
            d.Recommendations = ReportBuilder.BuildRecommendations(d);
            return d;
        }

        private static void GoodSessionReport()
        {
            var d = GoodReport();
            T.True(d.PerformsWell, "sessione buona riconosciuta");
            T.Contains(d.Verdict ?? "", "Il PC fa girare bene Fortnite", "verdetto chiaro");
            T.Contains(d.Verdict ?? "", "esclusi", "dice anche del tempo fuori fuoco escluso");
            T.True(d.Recommendations.All(r => r.Optional || r.Source == "Rete" || r.Title.Contains("rash")), "solo miglioramenti facoltativi");
            T.True(d.Recommendations.All(r => r.Severity <= CheckStatus.Info), "nessun Attenzione/Problema");
            var xmp = d.Recommendations.FirstOrDefault(r => r.Title == "Velocità RAM (XMP)");
            T.True(xmp != null && xmp.Optional && ReportBuilder.SevText(xmp) == "Facoltativo", "XMP come miglioramento facoltativo");

            var text = ReportBuilder.BuildSummaryText(d);
            T.Contains(text, "[VERDETTO]", "riassunto: verdetto");
            T.Contains(text, "[MIGLIORAMENTI FACOLTATIVI]", "riassunto: titolo della lista");
            T.True(!text.Contains("[COSA NON VA / COSA MIGLIORARE]"), "riassunto: niente \"cosa non va\"");
            T.Contains(text, "Esclusi", "riassunto: tempo fuori fuoco");
            T.Contains(text, "Durante la sessione", "riassunto: log della sessione");
            T.Contains(text, "Intero log (contesto", "riassunto: log come contesto");
            T.Contains(text, "API usata (log) D3D11 · ES3_1", "riassunto: RHI dal log");
            T.Contains(text, "PreferredRHI=dx11 PreferredFeatureLevel=es31", "riassunto: valori del file ini");
            T.True(text.Length <= ReportBuilder.SummaryMaxChars, "entro 4000 caratteri");

            var html = ReportBuilder.BuildHtml(d);
            foreach (var s in new[]
                     {
                         "Verdetto e miglioramenti facoltativi", "class=\"verdict s-ok\"", "Facoltativo", "Durante la sessione", "Intero log (contesto)",
                         "API grafica usata (dal log)", "D3D11 · ES3_1 (Direct3D 11 a feature level ridotto", "PreferredRHI=dx11", "Versione del gioco (dal log)",
                         "Fuori fuoco (esclusi)", "gioco fuori fuoco", "<rect x=", "rumore normale", "Periodo della sessione"
                     })
                T.Contains(html, s, "HTML: " + s);
            T.Equal(html.Split("<section").Length, html.Split("</section>").Length, "sezioni bilanciate");
            var json = ReportBuilder.BuildJson(d);
            T.Contains(json, "\"performsWell\": true", "JSON: giudizio");
            T.Contains(json, "\"unfocused\": true", "JSON: secondi fuori fuoco");
            T.Contains(json, "\"rhiInUse\": \"D3D11 · ES3_1\"", "JSON: RHI");
            var bands = ReportBuilder.UnfocusedBands(d.Session!.Seconds);
            T.Equal(2, bands.Count, "due bande fuori fuoco");
            T.Near(0, bands[0].From, 1e-9, "banda iniziale da 0");
        }

        private static void BadSessionVerdict()
        {
            var e = MakeEvidence(realistic: true);
            var plain = FocusFilter.BuildSession(e.Ft, e.Ts, null, 2.5, 12); // come il report reale, senza esclusione
            var d = new ReportData
            {
                Session = new PerfSession { Id = "x", Stats = plain.Stats, Seconds = plain.Seconds, FpsCap = 165, RefreshHz = 165, DurationSec = plain.DurationSec },
                Insights = { new PerfInsight { Severity = CheckStatus.Warn, Title = "Programmi in background pesanti", Message = "chrome", Hint = "chiudi" } }
            };
            var recs = ReportBuilder.BuildRecommendations(d);
            T.True(!d.PerformsWell, "1% low al 19% della media: non è una sessione buona");
            T.Contains(d.Verdict ?? "", "Prestazioni da migliorare", "verdetto onesto");
            T.True(recs.All(r => !r.Optional), "niente facoltativi");
            T.True(recs.Any(r => r.Severity == CheckStatus.Warn), "gli avvisi restano avvisi");
            var (good, verdict) = ReportBuilder.ComputeVerdict(new ReportData());
            T.True(!good && verdict == null, "senza sessione nessun verdetto");
            // Cap non raggiunto: non è "buona" anche se regolare.
            var capMiss = new ReportData
            {
                Session = new PerfSession { Stats = new FrameStatsResult { Frames = 1000, AvgFps = 120, Low1Fps = 110, StuttersPerMin = 0 }, FpsCap = 165 }
            };
            T.True(!ReportBuilder.ComputeVerdict(capMiss).Good, "media sotto il 95% del limite");
            capMiss.Session.FpsCap = null;
            capMiss.Session.RefreshHz = 120;
            T.True(ReportBuilder.ComputeVerdict(capMiss).Good, "senza limite: confronto con il refresh");
        }

        private static void LegacyVerdictWithoutFrametimes()
        {
            // Sessione vecchia con il tratto a ~30 FPS e GPU ferma, ma frametime mancanti: niente correzione.
            var e = MakeEvidence(realistic: true);
            var old = LegacySession(e, out _);
            var d = new ReportData { Session = old, Insights = PerfAnalyzer.Analyze(old, new[] { old }) };
            ReportBuilder.BuildRecommendations(d);
            T.True(!d.PerformsWell, "nessun verdetto positivo");
            var v = d.Verdict ?? "";
            T.Contains(v, "Nessun giudizio sugli FPS", "lo dice");
            T.True(!v.Contains("Prestazioni da migliorare") && !v.Contains("parecchi stutter"), "non incolpa il PC");
            T.Contains(v, "Fortnite va a circa 16", "FPS del gioco vero (~165)");
            T.True(d.Insights.Any(i => i.Title == "Probabile gioco in secondo piano"), "coerente con l'analisi");
        }

        private static void OtherGameTexts()
        {
            const string other = "VALORANT-Win64-Shipping";
            var e = MakeEvidence(realistic: true);
            var (frames, _) = Build(e);
            var session = new PerfSession
            {
                Id = "o", StartedAt = SessionStartUtc.ToLocalTime(), ProcessName = other, DurationSec = frames.DurationSec, Stats = frames.Stats,
                Seconds = frames.Seconds, FpsCap = 165, RefreshHz = 165, FocusTracked = true, UnfocusedSec = frames.UnfocusedSec,
                ExcludedFrames = frames.ExcludedFrames, ExcludedRanges = frames.ExcludedRanges
            };
            var d = new ReportData { Session = session, Insights = PerfAnalyzer.Analyze(session, new[] { session }, e.Ft) };
            ReportBuilder.BuildRecommendations(d);
            T.True(d.PerformsWell, "sessione buona");
            T.Contains(d.Verdict ?? "", "Il PC fa girare bene " + other, "verdetto col nome del gioco");
            T.True(!(d.Verdict ?? "").Contains("Fortnite"), "niente Fortnite nel verdetto");
            var focus = d.Insights.First(i => i.Title == "Tempo fuori fuoco escluso").Message;
            T.True(!focus.Contains("Fortnite si limita da solo"), "analisi: nessuna affermazione su Fortnite");
            var text = ReportBuilder.BuildSummaryText(d);
            T.True(!text.Contains("Fortnite scende da solo") && !text.Contains("Fortnite si limita da solo"), "riassunto: idem");
            T.Equal("in secondo piano Fortnite si limita da solo a ~30 FPS", PerfAnalyzer.BackgroundThrottleText(Fn), "per Fortnite resta specifico");
            T.Equal("il gioco", PerfAnalyzer.GameRef(""), "senza nome: il gioco");
            var unnamed = new ReportData { Session = new PerfSession { Stats = new FrameStatsResult { Frames = 1000, AvgFps = 165, Low1Fps = 150 }, FpsCap = 165 } };
            T.Contains(ReportBuilder.ComputeVerdict(unnamed).Verdict ?? "", "Il PC fa girare bene il gioco", "processo sconosciuto");
        }

        private static void PerformanceModeIni()
        {
            var dir = Path.Combine(Path.GetTempPath(), "fnboost-initest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var path = Path.Combine(dir, "GameUserSettings.ini");
                File.WriteAllText(path,
                    "[/Script/FortniteGame.FortGameUserSettings]\nFrameRateLimit=165.000000\nbUseVSync=False\n\n" +
                    "[D3DRHIPreference]\nPreferredRHI=dx12\nPreferredFeatureLevel=sm6\n");
                var s = FortniteSettings.Read(path);
                T.Equal(RenderMode.DirectX12, s.RenderMode, "letto DirectX 12");
                T.Equal("dx12", s.PreferredRhi, "valore grezzo RHI");
                T.Equal("sm6", s.PreferredFeatureLevel, "valore grezzo feature level");

                s.RenderMode = RenderMode.Performance;
                s.Write(path);
                var p = FortniteSettings.Read(path);
                T.Equal(RenderMode.Performance, p.RenderMode, "Performance riconosciuta");
                T.Equal("dx11", p.PreferredRhi, "Performance = dx11 (D3D11, come scrive il gioco)");
                T.Equal("es31", p.PreferredFeatureLevel, "Performance = es31");
                T.Equal("dx11/es31", p.RawRhi, "RawRhi");
                T.Near(165, p.FrameRateLimit, 1e-9, "il resto non cambia");

                p.RenderMode = RenderMode.DirectX12;
                p.Write(path);
                var x = FortniteSettings.Read(path);
                T.Equal("dx12", x.PreferredRhi, "DirectX 12 = dx12");
                T.Equal("sm6", x.PreferredFeatureLevel, "DirectX 12 = sm6");

                // Valore non standard: non viene toccato se la modalità resta "sconosciuta".
                File.WriteAllText(path, "[D3DRHIPreference]\nPreferredRHI=dx11\nPreferredFeatureLevel=sm5\n");
                var u = FortniteSettings.Read(path);
                T.Equal(RenderMode.Unknown, u.RenderMode, "dx11/sm5 non standard");
                u.Write(path);
                T.Equal("dx11/sm5", FortniteSettings.Read(path).RawRhi, "lasciato com'è");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { /* temp */ }
            }
        }
    }
}
