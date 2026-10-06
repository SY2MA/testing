using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FNBoost.Core;
using FNBoost.Perf;

namespace FNBoost.Tests
{
    internal static class Program
    {
        private const string Fn = "FortniteClient-Win64-Shipping";

        private static int Main()
        {
            Console.WriteLine("FN Boost — test dei moduli Prestazioni e Mirino");

            Console.WriteLine("FrameStats");
            T.Run("240 fps costanti", Constant240);
            T.Run("1% di picchi: low, P1, min, max esatti", Spikes);
            T.Run("rilevamento stutter", StutterDetection);
            T.Run("input vuoto, 1 frame, NaN e negativi", DegenerateInput);
            T.Run("FPS al secondo", PerSecond);
            T.Run("prestazioni su 1,5 milioni di frame", LargeInput);

            Console.WriteLine("PerfSessionStore");
            T.Run("salva, carica, aggiorna, elimina, pota", StoreRoundtrip);

            Console.WriteLine("PerfAnalyzer");
            T.Run("dati insufficienti", AnalyzerNoData);
            T.Run("gioco fluido e limite del cap", AnalyzerCapLimited);
            T.Run("FPS agganciati al refresh (VSync)", AnalyzerVsync);
            T.Run("limite GPU", AnalyzerGpuBound);
            T.Run("probabile limite CPU", AnalyzerCpuBound);
            T.Run("molti stutter, shader iniziali, attività in background", AnalyzerStutterHeavy);
            T.Run("VRAM e RAM quasi piene", AnalyzerMemory);
            T.Run("confronto con la sessione precedente e tweak cambiati", AnalyzerComparison);
            T.Run("Compare: testo di confronto", CompareText);
            T.Run("andamento: calo, miglioramento, migliore", TrendTests);

            CrosshairTests.RunAll();
            ReportTests.RunAll();

            Console.WriteLine();
            Console.WriteLine($"{T.Passed} test superati, {T.FailureList.Count} asserzioni fallite.");
            return T.FailureList.Count == 0 ? 0 : 1;
        }

        // ================= FrameStats =================

        private static void Constant240()
        {
            var ft = Enumerable.Repeat(1000f / 240f, 2400).ToArray();
            var r = FrameStats.Compute(ft);
            T.True(r.HasData, "HasData");
            T.Equal(2400, r.Frames, "Frames");
            T.Near(240, r.AvgFps, 1e-3, "AvgFps");
            T.Near(240, r.Low1Fps, 1e-3, "Low1Fps");
            T.Near(240, r.Low01Fps, 1e-3, "Low01Fps");
            T.Near(240, r.P1Fps, 1e-3, "P1Fps");
            T.Near(240, r.MinFps, 1e-3, "MinFps");
            T.Near(240, r.MaxFps, 1e-3, "MaxFps");
            T.Near(10, r.DurationSec, 1e-3, "DurationSec");
            T.Near(0, r.StdDevFrametimeMs, 1e-6, "StdDev");
            T.Equal(0, r.Stutters, "Stutters");
            T.Near(100, r.ConsistencyScore, 1e-6, "ConsistencyScore");
        }

        private static void Spikes()
        {
            // 1000 frame: 990 da 5 ms, 9 picchi da 20 ms e uno da 40 ms, distanziati di 100 frame.
            var ft = Enumerable.Repeat(5f, 1000).ToArray();
            for (int i = 0; i < 10; i++) ft[50 + i * 100] = i == 9 ? 40f : 20f;
            var r = FrameStats.Compute(ft);
            T.Equal(1000, r.Frames, "Frames");
            T.Near(1000.0 * 1000 / 5170, r.AvgFps, 1e-9, "AvgFps");
            T.Near(1000.0 / 22, r.Low1Fps, 1e-9, "Low1Fps (media dei 10 più lenti = 22 ms)");
            T.Near(25, r.Low01Fps, 1e-9, "Low01Fps (1 frame = 40 ms)");
            T.Near(1000.0 / 5.15, r.P1Fps, 1e-6, "P1Fps (P99 interpolato = 5,15 ms)");
            T.Near(5.15, r.P99FrametimeMs, 1e-9, "P99FrametimeMs");
            T.Near(25, r.MinFps, 1e-9, "MinFps");
            T.Near(200, r.MaxFps, 1e-9, "MaxFps");
            T.Near(5, r.MedianFrametimeMs, 1e-9, "Median");
            T.Near(40, r.MaxFrametimeMs, 1e-9, "MaxFrametime");
            T.Equal(10, r.Stutters, "Stutters");
            T.Near(10 / (5.17 / 60), r.StuttersPerMin, 1e-6, "StuttersPerMin");
            T.True(r.ConsistencyScore < 50, "ConsistencyScore bassa con picchi forti: " + r.ConsistencyScore);
        }

        private static void StutterDetection()
        {
            T.True(FrameStats.IsStutter(30, 10, 2.5, 12), "30 ms su mediana 10 è stutter");
            T.True(FrameStats.IsStutter(25, 10, 2.5, 12), "25 ms su mediana 10 è stutter (soglia inclusa)");
            T.True(!FrameStats.IsStutter(24, 10, 2.5, 12), "24 ms su mediana 10 non è stutter");
            T.True(!FrameStats.IsStutter(8, 2, 2.5, 12), "8 ms su mediana 2: sotto la soglia assoluta");
            T.True(!FrameStats.IsStutter(float.NaN, 10, 2.5, 12), "NaN non è stutter");

            var a = Seq((100, 10f), (1, 24f), (1, 26f), (100, 10f));
            T.Equal(1, FrameStats.Compute(a).Stutters, "solo il frame da 26 ms");

            var burst = Seq((100, 10f), (3, 40f), (100, 10f));
            T.Equal(3, FrameStats.Compute(burst).Stutters, "raffica di 3 stutter (mediana robusta)");

            var fast = Seq((200, 2f), (1, 8f), (200, 2f));
            T.Equal(0, FrameStats.Compute(fast).Stutters, "8 ms a 500 fps: sotto i 12 ms minimi");
            T.Equal(1, FrameStats.Compute(fast, 2.5, 0).Stutters, "senza soglia minima diventa stutter");

            var first = new[] { 50f, 10f, 10f };
            T.Equal(0, FrameStats.Compute(first).Stutters, "il primo frame non ha storico");

            // Dopo un cambio stabile di frametime la mediana locale si adegua entro metà finestra (30 frame).
            var shift = Seq((100, 5f), (200, 15f));
            T.Equal(30, FrameStats.Compute(shift).Stutters, "salto 5 → 15 ms: solo i primi 30 frame");
        }

        private static void DegenerateInput()
        {
            var empty = FrameStats.Compute(Array.Empty<float>());
            T.True(!empty.HasData, "vuoto: HasData false");
            T.Equal(0, empty.Frames, "vuoto: Frames");
            T.Near(0, empty.AvgFps, 0, "vuoto: AvgFps");

            var one = FrameStats.Compute(new[] { 16.6f });
            T.True(!one.HasData, "1 frame: HasData false");
            T.Near(0, one.AvgFps, 0, "1 frame: AvgFps");
            T.Near(0, one.Low1Fps, 0, "1 frame: Low1Fps");

            var nan = FrameStats.Compute(new[] { float.NaN, -5f, 10f, 0f, float.PositiveInfinity, 10f });
            T.True(nan.HasData, "NaN/negativi ignorati: restano 2 frame");
            T.Equal(2, nan.Frames, "Frames validi");
            T.Near(100, nan.AvgFps, 1e-9, "AvgFps");

            var allBad = FrameStats.Compute(new[] { float.NaN, -1f, float.NegativeInfinity });
            T.True(!allBad.HasData, "solo valori non validi");
            T.Equal(0, FrameStats.PerSecondFps(Array.Empty<float>()).Length, "PerSecondFps vuoto");
        }

        private static void PerSecond()
        {
            var a = FrameStats.PerSecondFps(Enumerable.Repeat(4f, 625).ToArray()); // 2,5 s
            T.Equal(3, a.Length, "2,5 s → 3 valori (ultimo parziale ≥ 0,5 s)");
            foreach (var v in a) T.Near(250, v, 1e-9, "250 fps");

            var b = FrameStats.PerSecondFps(Enumerable.Repeat(4f, 600).ToArray()); // 2,4 s
            T.Equal(2, b.Length, "2,4 s → l'ultimo 0,4 s è scartato");

            var c = FrameStats.PerSecondFps(Seq((250, 4f), (1, 3000f)));
            T.Equal(4, c.Length, "frame lunghissimo → secondi a 0");
            T.Near(250, c[0], 0, "s0");
            T.Near(0, c[1], 0, "s1");
            T.Near(0, c[2], 0, "s2");
            T.Near(1, c[3], 0, "s3");

            var d = FrameStats.PerSecondFps(new[] { 10f, float.NaN, -3f, 10f });
            T.Equal(0, d.Length, "20 ms totali: nessun secondo");
        }

        private static void LargeInput()
        {
            var rnd = new Random(42);
            var ft = new float[1_500_000];
            for (int i = 0; i < ft.Length; i++) ft[i] = 4f + (float)rnd.NextDouble() * 2f + (i % 5000 == 0 ? 30f : 0f);
            var sw = Stopwatch.StartNew();
            var r = FrameStats.Compute(ft);
            sw.Stop();
            Console.WriteLine($"       (1,5M frame in {sw.ElapsedMilliseconds} ms)");
            T.True(r.HasData && r.Stutters >= 290 && r.Stutters <= 300, "300 picchi da +30 ms rilevati: " + r.Stutters);
            T.True(sw.ElapsedMilliseconds < 5000, "Compute veloce");
        }

        // ================= PerfSessionStore =================

        private static void StoreRoundtrip()
        {
            var dir = Path.Combine(Path.GetTempPath(), "fnboost-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new PerfSessionStore(dir, 2);
                int changed = 0;
                store.Changed += () => changed++;
                T.Equal(0, store.List().Count, "archivio vuoto");

                var ft1 = Seq((100, 10f));
                var ft2 = Seq((50, 5f), (1, 33.3f));
                var ft3 = new[] { 1.5f, 2.25f, 1000f, 0.001f };
                var s1 = Make("20260101-100000-aaaa", new DateTime(2026, 1, 1, 10, 0, 0), ft1);
                var s2 = Make("20260102-100000-bbbb", new DateTime(2026, 1, 2, 10, 0, 0), ft2);
                var s3 = Make("20260103-100000-cccc", new DateTime(2026, 1, 3, 10, 0, 0), ft3);
                s3.ActiveTweaks = new List<string> { "game-mode", "power-plan" };
                s3.RenderMode = "Performance";
                s3.FpsCap = 240;

                store.Save(s1, ft1);
                store.Save(s2, ft2);
                T.Equal(2, store.List().Count, "due sessioni");
                store.Save(s3, ft3);

                var list = store.List();
                T.Equal(2, list.Count, "potatura a maxSessions = 2");
                T.Equal("20260103-100000-cccc", list[0].Id, "la più recente per prima");
                T.Equal("20260102-100000-bbbb", list[1].Id, "poi la seconda");
                T.True(store.LoadFrametimes(s1.Id) == null, "la più vecchia è stata eliminata");
                T.True(!File.Exists(Path.Combine(dir, s1.Id + ".ft")), "anche il file .ft è stato eliminato");

                var back = store.LoadFrametimes(s3.Id);
                T.True(back != null && back.SequenceEqual(ft3), "frametime identici dopo il round-trip");
                T.Equal(16L, new FileInfo(Path.Combine(dir, s3.Id + ".ft")).Length, "float32: 4 byte per frame");
                var json = File.ReadAllText(Path.Combine(dir, s3.Id + ".json"));
                T.Contains(json, "\n  \"", "JSON indentato");
                T.Contains(json, "\"Performance\"", "RenderMode salvato");

                var reloaded = new PerfSessionStore(dir, 2).List();
                T.Equal(2, reloaded.Count, "nuova istanza legge i file");
                var r3 = reloaded[0];
                T.Equal(2, r3.ActiveTweaks.Count, "tweak attivi");
                T.Near(240, r3.FpsCap ?? 0, 0, "FpsCap");
                T.Near(s3.Stats.AvgFps, r3.Stats.AvgFps, 1e-9, "statistiche");
                T.Equal(s3.Seconds.Count, r3.Seconds.Count, "campioni al secondo");

                s2.Label = "dopo tweak";
                store.UpdateMeta(s2);
                T.Equal("dopo tweak", store.List().First(s => s.Id == s2.Id).Label, "UpdateMeta aggiorna la cache");
                T.Equal("dopo tweak", new PerfSessionStore(dir, 2).List().First(s => s.Id == s2.Id).Label, "UpdateMeta su disco");
                T.True(store.LoadFrametimes(s2.Id)!.SequenceEqual(ft2), "UpdateMeta non tocca i frametime");

                File.WriteAllText(Path.Combine(dir, "rotto.json"), "{ non è json");
                T.Equal(2, new PerfSessionStore(dir, 2).List().Count, "un file danneggiato viene ignorato");

                store.Delete(s3.Id);
                T.Equal(1, store.List().Count, "dopo Delete");
                T.True(store.LoadFrametimes(s3.Id) == null, "frametime eliminati");
                T.True(store.LoadFrametimes("../../etc/passwd") == null, "id manipolato non esce dalla cartella");

                T.Equal(5, changed, "Changed: 3 Save + 1 UpdateMeta + 1 Delete");
                T.True(PerfSessionStore.DefaultDirectory.EndsWith(Path.Combine("FNBoost", "sessions"), StringComparison.Ordinal),
                    "DefaultDirectory: " + PerfSessionStore.DefaultDirectory);
                T.True(PerfSessionStore.NewId(new DateTime(2026, 5, 4, 3, 2, 1)).StartsWith("20260504-030201-", StringComparison.Ordinal), "NewId");
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch
                {
                    // pulizia best-effort
                }
            }
        }

        // ================= PerfAnalyzer =================

        private static void AnalyzerNoData()
        {
            var s = new PerfSession { Id = "x", StartedAt = DateTime.Now, ProcessName = Fn };
            var ins = PerfAnalyzer.Analyze(s, Array.Empty<PerfSession>());
            T.Equal(1, ins.Count, "una sola osservazione");
            T.Equal("Dati insufficienti", ins[0].Title, "titolo");

            // Se mancano le statistiche ma ci sono i frametime, si calcolano al volo.
            var withFt = PerfAnalyzer.Analyze(s, Array.Empty<PerfSession>(), Const(144, 30));
            T.True(Has(withFt, "Gioco fluido"), "statistiche ricavate dai frametime");
        }

        private static void AnalyzerCapLimited()
        {
            var s = Make("cap", Day(1), Const(143.9, 120), refresh: 240, cap: 144, sec: (_, x) => x.GpuPercent = 50);
            var ins = PerfAnalyzer.Analyze(s, new[] { s });
            var overall = Get(ins, "Gioco fluido");
            T.True(overall != null && overall.Severity == CheckStatus.Ok, "risultato complessivo Ok");
            T.Contains(overall?.Message ?? "", "Media 144 FPS", "messaggio con la media");
            var cap = Get(ins, "FPS limitati dal cap");
            T.True(cap != null && cap.Severity == CheckStatus.Info, "cap riconosciuto");
            T.True(!Has(ins, "Probabile limite CPU"), "con il cap non si parla di limite CPU");
            T.True(!Has(ins, "FPS sotto il refresh del monitor"), "con il cap non è un problema di refresh");
            T.True(Has(ins, "Pochissimi stutter"), "nessuno stutter");
            T.True(!ins.Any(i => i.Title.StartsWith("Meglio", StringComparison.Ordinal) || i.Title.StartsWith("Peggio", StringComparison.Ordinal) ||
                                 i.Title.StartsWith("Simile", StringComparison.Ordinal)), "la sessione stessa nella cronologia viene saltata");
        }

        private static void AnalyzerVsync()
        {
            var s = Make("vsync", Day(1), Const(144, 120), refresh: 144, sec: (_, x) => x.GpuPercent = 60);
            var ins = PerfAnalyzer.Analyze(s, Array.Empty<PerfSession>());
            T.True(Has(ins, "FPS agganciati al refresh"), "VSync riconosciuto");
            T.True(!Has(ins, "Probabile limite CPU"), "nessun falso limite CPU");
        }

        private static void AnalyzerGpuBound()
        {
            var s = Make("gpu", Day(1), Const(100, 120), refresh: 144, sec: (_, x) => { x.GpuPercent = 98; x.CpuPercent = 35; });
            var ins = PerfAnalyzer.Analyze(s, Array.Empty<PerfSession>());
            var gpu = Get(ins, "Limite GPU");
            T.True(gpu != null && gpu.Severity == CheckStatus.Warn, "limite GPU (Warn perché sotto il refresh)");
            T.Contains(gpu?.Hint ?? "", "TSR", "suggerimento upscaling");
            T.True(Has(ins, "FPS sotto il refresh del monitor"), "sotto il refresh");
            T.True(!Has(ins, "Probabile limite CPU"), "non anche limite CPU");
        }

        private static void AnalyzerCpuBound()
        {
            var s = Make("cpu", Day(1), Const(100, 120), refresh: 144, sec: (_, x) => { x.GpuPercent = 60; x.CpuPercent = 45; });
            var ins = PerfAnalyzer.Analyze(s, Array.Empty<PerfSession>());
            var cpu = Get(ins, "Probabile limite CPU");
            T.True(cpu != null && cpu.Severity == CheckStatus.Warn, "limite CPU");
            T.Contains(cpu?.Hint ?? "", "XMP", "suggerimento XMP");
            T.True(!Has(ins, "Limite GPU"), "non limite GPU");

            // Senza dati GPU non si tirano conclusioni sul collo di bottiglia.
            var noGpu = Make("cpu2", Day(1), Const(100, 120), refresh: 144);
            var ins2 = PerfAnalyzer.Analyze(noGpu, Array.Empty<PerfSession>());
            T.True(!Has(ins2, "Probabile limite CPU") && !Has(ins2, "Limite GPU"), "senza GPU% nessuna ipotesi");
        }

        private static void AnalyzerStutterHeavy()
        {
            // 3 minuti a 100 fps: 40 stutter nel primo minuto, 5 dopo → 15 al minuto.
            var ft = Enumerable.Repeat(10f, 18000).ToArray();
            for (int i = 0; i < 40; i++) ft[100 + i * 140] = 45f;
            for (int i = 0; i < 5; i++) ft[8000 + i * 2000] = 45f;
            var s = Make("stutter", Day(1), ft, refresh: 144, sec: (st, x) => { x.CpuPercent = st > 0 ? 70 : 40; x.GpuPercent = 85; });
            T.Equal(45, s.Stats.Stutters, "45 stutter");

            var ins = PerfAnalyzer.Analyze(s, Array.Empty<PerfSession>(), ft);
            var rate = Get(ins, "Molti stutter");
            T.True(rate != null && rate.Severity == CheckStatus.Bad, "stutter > 10/min → Bad");
            T.Contains(rate?.Hint ?? "", "shader", "suggerimento sugli shader");
            var early = Get(ins, "Stutter concentrati all'inizio");
            T.True(early != null, "compilazione shader iniziale");
            T.Contains(early?.Message ?? "", "40 stutter su 45", "conteggio nel primo minuto");
            T.True(Has(ins, "Attività in background durante gli stutter"), "CPU più alta nei secondi con stutter");

            // Pochi stutter → Warn.
            var ft2 = Enumerable.Repeat(10f, 18000).ToArray();
            for (int i = 0; i < 15; i++) ft2[500 + i * 1100] = 45f; // 5 al minuto, distribuiti
            var s2 = Make("stutter2", Day(1), ft2);
            var ins2 = PerfAnalyzer.Analyze(s2, Array.Empty<PerfSession>());
            var rate2 = Get(ins2, "Qualche stutter");
            T.True(rate2 != null && rate2.Severity == CheckStatus.Warn, "2-10/min → Warn");
            T.True(!Has(ins2, "Stutter concentrati all'inizio"), "distribuiti: non è compilazione shader");
        }

        private static void AnalyzerMemory()
        {
            var s = Make("mem", Day(1), Const(200, 90), sec: (_, x) => { x.VramUsedGb = 7.7; x.RamPercent = 93; });
            s.VramTotalGb = 8;
            var ins = PerfAnalyzer.Analyze(s, Array.Empty<PerfSession>());
            T.True(Has(ins, "Memoria video quasi piena"), "VRAM ≥ 92%");
            T.True(Has(ins, "RAM quasi piena"), "RAM ≥ 90%");

            var ok = Make("mem2", Day(1), Const(200, 90), sec: (_, x) => { x.VramUsedGb = 5; x.RamPercent = 60; });
            ok.VramTotalGb = 8;
            var ins2 = PerfAnalyzer.Analyze(ok, Array.Empty<PerfSession>());
            T.True(!Has(ins2, "Memoria video quasi piena") && !Has(ins2, "RAM quasi piena"), "nessun allarme con memoria libera");
        }

        private static void AnalyzerComparison()
        {
            var prev = Make("prev", Day(1), Const(100, 120));
            prev.ActiveTweaks = new List<string> { "game-mode", "visual-effects" };
            prev.RenderMode = "DirectX12";
            var older = Make("older", Day(0), Const(50, 120));
            var cur = Make("cur", Day(2), Const(120, 120));
            cur.ActiveTweaks = new List<string> { "game-mode", "power-plan" };
            cur.RenderMode = "Performance";
            cur.FpsCap = 240;
            var other = Make("other", Day(1).AddHours(5), Const(300, 120), proc: "OtherGame");
            var newer = Make("newer", Day(3), Const(10, 120)); // più recente di cur: non è "precedente"

            var history = new[] { newer, cur, other, prev, older };
            var ins = PerfAnalyzer.Analyze(cur, history);
            var cmp = Get(ins, "Meglio della sessione precedente");
            T.True(cmp != null && cmp.Severity == CheckStatus.Ok, "miglioramento rispetto alla precedente dello stesso gioco");
            T.Contains(cmp?.Message ?? "", "Media +20%", "delta media");
            T.Contains(cmp?.Message ?? "", "1% low +20%", "delta 1% low");
            T.Contains(cmp?.Message ?? "", prev.Title, "nome della sessione di riferimento");
            T.Contains(cmp?.Message ?? "", "tweak aggiunti: power-plan", "tweak aggiunti");
            T.Contains(cmp?.Message ?? "", "tweak rimossi: visual-effects", "tweak rimossi");
            T.Contains(cmp?.Message ?? "", "rendering DirectX12 → Performance", "rendering cambiato");
            T.Contains(cmp?.Message ?? "", "limite FPS illimitato → 240", "cap cambiato");
            T.Contains(cmp?.Hint ?? "", "correlazione", "onestà: correlazione, non prova");

            var plain = Make("plain", Day(1), Const(100, 120));
            var worse = Make("worse", Day(2), Const(80, 120));
            var ins2 = PerfAnalyzer.Analyze(worse, new[] { plain });
            var w = Get(ins2, "Peggio della sessione precedente");
            T.True(w != null && w.Severity == CheckStatus.Warn, "peggioramento");
            T.Contains(w?.Hint ?? "", "Nessuna modifica di configurazione", "nessuna differenza di configurazione");
        }

        private static void CompareText()
        {
            var b = Make("b", Day(1), Const(100, 60));
            var c = Make("c", Day(2), Const(112, 60));
            T.Equal($"Media +12% · 1% low +12% · stutter = rispetto a {b.Title}", PerfAnalyzer.Compare(c, b), "nessuno stutter");

            var bs = Make("bs", Day(1), StutterRun(100, 60, 20));
            var cs = Make("cs", Day(2), StutterRun(100, 60, 13));
            var text = PerfAnalyzer.Compare(cs, bs);
            T.Contains(text, "stutter −35%", "stutter in calo del 35% con segno meno tipografico");

            var zero = PerfAnalyzer.Compare(cs, b);
            T.Contains(zero, "stutter da 0 a", "da zero stutter");
        }

        private static void TrendTests()
        {
            // Calo: le 3 più recenti a 150 fps, le 3 precedenti a 200.
            var sessions = new List<PerfSession>();
            for (int i = 0; i < 6; i++)
                sessions.Add(Make("t" + i, Day(10 - i), Const(i < 3 ? 150 : 200, 60)));
            var trend = PerfAnalyzer.Trend(sessions);
            var down = Get(trend, "Prestazioni in calo");
            T.True(down != null && down.Severity == CheckStatus.Warn, "calo > 10% → Warn");
            T.Contains(down?.Message ?? "", "media −25%", "delta del 25%");
            var best = Get(trend, "Sessione migliore");
            T.True(best != null, "sessione migliore");
            T.Contains(best?.Message ?? "", sessions[3].Title, "la migliore è una delle più vecchie");

            var upList = new List<PerfSession>();
            for (int i = 0; i < 6; i++)
                upList.Add(Make("u" + i, Day(10 - i), Const(i < 3 ? 200 : 150, 60)));
            var up = Get(PerfAnalyzer.Trend(upList), "Prestazioni in miglioramento");
            T.True(up != null && up.Severity == CheckStatus.Ok, "miglioramento > 10% → Ok");

            var stable = new List<PerfSession>();
            for (int i = 0; i < 4; i++)
                stable.Add(Make("s" + i, Day(10 - i), Const(i % 2 == 0 ? 200 : 196, 60)));
            T.True(Has(PerfAnalyzer.Trend(stable), "Prestazioni stabili"), "variazioni piccole → stabili");

            var few = PerfAnalyzer.Trend(new[] { Make("f1", Day(2), Const(100, 60)), Make("f2", Day(1), Const(100, 60)) });
            T.True(Has(few, "Andamento non ancora disponibile"), "servono almeno 3 sessioni");

            // Giochi diversi non si mescolano.
            var mixed = new List<PerfSession>
            {
                Make("m1", Day(5), Const(100, 60)),
                Make("m2", Day(4), Const(400, 60), proc: "Other"),
                Make("m3", Day(3), Const(400, 60), proc: "Other")
            };
            T.True(Has(PerfAnalyzer.Trend(mixed), "Andamento non ancora disponibile"), "3 sessioni ma di giochi diversi");
        }

        // ================= utilità =================

        private static DateTime Day(int d) => new DateTime(2026, 3, 1, 20, 0, 0).AddDays(d);

        private static float[] Const(double fps, double seconds) =>
            Enumerable.Repeat((float)(1000.0 / fps), (int)Math.Round(fps * seconds)).ToArray();

        /// <summary>FPS costanti con un numero fisso di stutter (frame da 5× il normale) distribuiti.</summary>
        private static float[] StutterRun(double fps, double seconds, int stutters)
        {
            var ft = Const(fps, seconds);
            int step = ft.Length / (stutters + 1);
            for (int i = 1; i <= stutters; i++) ft[i * step] = ft[i * step] * 5;
            return ft;
        }

        private static float[] Seq(params (int count, float ms)[] parts) =>
            parts.SelectMany(p => Enumerable.Repeat(p.ms, p.count)).ToArray();

        /// <summary>Sessione sintetica con statistiche e campioni al secondo coerenti con i frametime.</summary>
        private static PerfSession Make(string id, DateTime at, float[] ft, int? refresh = null, double? cap = null,
            Action<int, SecondSample>? sec = null, string proc = Fn)
        {
            var s = new PerfSession
            {
                Id = id,
                StartedAt = at,
                ProcessName = proc,
                RefreshHz = refresh,
                FpsCap = cap,
                Stats = FrameStats.Compute(ft)
            };
            s.DurationSec = s.Stats.DurationSec;

            var flags = FrameStats.StutterFlags(ft, 2.5, 12);
            var fps = FrameStats.PerSecondFps(ft);
            var stutters = new int[fps.Length];
            double t = 0;
            for (int i = 0; i < ft.Length; i++)
            {
                t += ft[i];
                int k = Math.Max(0, (int)Math.Ceiling(t / 1000.0) - 1);
                if (flags[i] && k < stutters.Length) stutters[k]++;
            }
            for (int k = 0; k < fps.Length; k++)
            {
                var x = new SecondSample { T = k, Fps = fps[k], Stutters = stutters[k], CpuPercent = 30, RamPercent = 50 };
                sec?.Invoke(stutters[k], x);
                s.Seconds.Add(x);
            }
            return s;
        }

        private static PerfInsight? Get(List<PerfInsight> list, string title) => list.FirstOrDefault(i => i.Title == title);
        private static bool Has(List<PerfInsight> list, string title) => Get(list, title) != null;
    }
}
