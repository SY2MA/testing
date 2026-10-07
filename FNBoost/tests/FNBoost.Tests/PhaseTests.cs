using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FNBoost.Core;
using FNBoost.Perf;
using FNBoost.Report;

namespace FNBoost.Tests
{
    /// <summary>
    /// Fasi della sessione (lobby / caricamento / partita), statistiche "solo partita", scatti e traffico per programma.
    /// La sessione sintetica riproduce il secondo report reale (Fortnite, i5-13600K, RTX 3070 Ti, 165 Hz, cap 165):
    /// lobby con 30 s a ~30 FPS (inattività) e nessun pacchetto dal server, due schermate di caricamento con frame da
    /// 1-2,6 s, due partite a ~163 FPS con 60 scatti (sei oltre i 100 ms), 2 minuti di download a 20 Mbit/s all'inizio
    /// di ogni partita e freeze di rete all'ingresso in partita (normali) e a metà partita (418 s e 434 s).
    /// </summary>
    internal static class PhaseTests
    {
        private const string Fn = "FortniteClient-Win64-Shipping";

        public static void RunAll()
        {
            Console.WriteLine("Fasi della sessione e statistiche solo partita");
            T.Run("report reale: lobby, caricamenti e due partite riconosciuti dal traffico del server", EvidencePhases);
            T.Run("report reale: statistiche solo partita (media ~163, 1% low ~67, 0,1% low ~22) e peso degli scatti", EvidenceMatchStats);
            T.Run("report reale: analisi (download di Fortnite, freeze a metà partita, lobby a 30 FPS, low spiegati)", EvidenceInsights);
            T.Run("report reale: verdetto, riquadri, riassunto e tabella scatti usano la partita", EvidenceReport);
            T.Run("download di un'altra app e sessione senza traffico per programma", DownloadAttributionVariants);
            T.Run("senza dati di rete: lobby da 30 FPS inattivi e cursore, caricamenti dai frame lunghi", FallbackPhases);
            T.Run("sfarfallii: tratti di partita/lobby sotto i 5 s assorbiti, Alt+Tab non spezza la partita", SmoothingAndMatches);
            T.Run("sessione vecchia: fasi e statistiche ricalcolate al volo, originale intatto", LegacyRecompute);
            T.Run("istanti dei frame con le pause salvate e secondo di ogni frame", FrameTimeline);
            T.Run("frame della partita: CSV con colonna match, archivio e 4 ore di sessione", MatchFramesAndStore);

            Console.WriteLine("Traffico per programma (TCP + UDP)");
            T.Run("payload TCP/UDP: PID e dimensione", TcpPayload);
            T.Run("aggregazione per PID e per secondo, limiti di memoria, eventi in ritardo", PidAggregation);
            T.Run("da PID a programmi: Fortnite (download contenuti), esclusioni, totali", Attribution);
        }

        // ================= Sessione sintetica =================

        private static readonly (double At, float Ms)[] HugeSpikes =
        {
            (89.2, 108f), (239.8, 142.6f), (299.9, 223.5f), (320.6, 103.0f), (328.4, 156.1f), (523.7, 141.8f)
        };

        private static bool InDownload(int sec) => (sec >= 88 && sec <= 149) || (sec >= 328 && sec <= 364);

        /// <summary>Tutti gli scatti della partita: 6 ≥ 100 ms, 7 tra 50 e 100, 47 tra 30 e 46 (21 durante i download).</summary>
        private static List<(double At, float Ms)> AllSpikes()
        {
            var list = HugeSpikes.ToList();
            double[] bigAt = { 130.5, 180.3, 260.7, 350.2, 400.6, 460.4, 500.8 };
            for (int i = 0; i < bigAt.Length; i++) list.Add((bigAt[i], 60f + 5 * i));
            var mid = new List<double>();
            for (int i = 0; i < 11; i++) mid.Add(92.5 + 5.2 * i);   // download 1
            for (int i = 0; i < 6; i++) mid.Add(331.5 + 5.5 * i);   // download 2
            for (int i = 0; i < 15; i++) mid.Add(155.5 + 10 * i);   // partita 1, senza download
            for (int i = 0; i < 15; i++) mid.Add(370.5 + 11 * i);   // partita 2, senza download
            for (int i = 0; i < mid.Count; i++) list.Add((mid[i], 30f + (i % 5) * 4));
            return list.OrderBy(x => x.At).ToList();
        }

        private sealed class Synthetic
        {
            public float[] Ft = Array.Empty<float>();
            public bool[] Excluded = Array.Empty<bool>();
            public PerfSession Session = new();
        }

        /// <summary>
        /// Costruisce frametime e sessione come fa PerfService: FocusFilter.BuildSession sui frame, poi i campioni al
        /// secondo (GPU, rete, download) per istante e infine SessionPhases.Apply.
        /// </summary>
        private static Synthetic BuildEvidence(bool network = true, bool perProcess = true, string downloader = NetProcessAttribution.FortniteContentName,
            bool cursor = false)
        {
            var ft = new List<float>();
            var ex = new List<bool>();
            double t = 0;
            void Add(float f, bool excluded = false)
            {
                ft.Add(f);
                ex.Add(excluded);
                t += f;
            }
            void FillTo(double endMs, float f, bool excluded = false)
            {
                while (t + f < endMs) Add(f, excluded);
            }

            // ---- lobby: 120 FPS, avvio con qualche frame lungo, 30 FPS inattivo 37-66 s ----
            FillTo(5000, 8.333f);
            Add(458f);
            Add(329f);
            FillTo(7500, 8.333f);
            Add(304f);
            FillTo(37000, 8.333f);
            FillTo(66000, 33.333f);
            FillTo(83000, 8.333f);
            // ---- caricamento 1 (83-88 s) ----
            FillTo(83400, 8f);
            Add(1268f);
            FillTo(85000, 6f);
            Add(443f);
            Add(321f);
            FillTo(86800, 6f);
            Add(1074f);
            FillTo(87995, 6f);
            // ---- partita 1 (88-312 s) ----
            var spikes = AllSpikes();
            MatchSeconds(88, 312);
            // ---- caricamento 2 (312-320 s) ----
            FillTo(312500, 6.6f);
            Add(2600f);
            Add(263f);
            FillTo(316000, 6f);
            Add(337f);
            Add(250f);
            FillTo(319995, 6.06f);
            // ---- partita 2 (320-538 s) ----
            MatchSeconds(320, 538);
            // ---- fuori fuoco (538-548 s): Fortnite in secondo piano a 30 FPS ----
            FillTo(548000, 33.333f, excluded: true);

            void MatchSeconds(int from, int to)
            {
                // 2 frame "lenti" (9,5 e 13,5 ms) in 4 secondi su 5: la coda dei frametime del report (P1 ~108 FPS).
                const float normal = 6.04f;
                for (int k = from; k < to; k++)
                {
                    double end = (k + 1) * 1000.0;
                    var inSec = spikes.Where(x => x.At >= k && x.At < k + 1).ToList();
                    int mediums = k % 5 == 0 ? 0 : 2;
                    foreach (var sp in inSec)
                    {
                        FillTo(sp.At * 1000.0 - sp.Ms, normal);
                        Add(sp.Ms);
                    }
                    for (int m = 0; m < mediums; m++)
                    {
                        FillTo(k * 1000.0 + 300 + 400 * m, normal);
                        if (t < end - 20) Add(m == 0 ? 9.5f : 13.5f);
                    }
                    FillTo(end - 0.5, normal);
                }
            }

            var ftArr = ft.ToArray();
            var exArr = ex.ToArray();
            var ts = new double[ftArr.Length];
            double c = 0;
            for (int i = 0; i < ftArr.Length; i++)
            {
                c += ftArr[i];
                ts[i] = c;
            }
            var frames = FocusFilter.BuildSession(ftArr, ts, exArr, 2.5, 12);
            var s = new PerfSession
            {
                Id = "20261007-130000-ev02",
                StartedAt = new DateTime(2026, 10, 7, 13, 0, 0),
                ProcessName = Fn,
                FpsCap = 165,
                RefreshHz = 165,
                RenderMode = "Performance",
                Stats = frames.Stats,
                DurationSec = frames.DurationSec,
                FocusTracked = true,
                UnfocusedSec = frames.UnfocusedSec,
                ExcludedFrames = frames.ExcludedFrames,
                ExcludedRanges = frames.ExcludedRanges.Count > 0 ? frames.ExcludedRanges : null,
                Seconds = frames.Seconds
            };
            int[] lobbyPk = { 5, 4, 3, 6, 13, 24 };     // 77-82 s: matchmaking
            int[] load1Pk = { 7, 13, 77, 47, 57 };      // 83-87 s
            int[] load2Pk = { 25, 5, 5, 46, 30, 30, 55, 28 }; // 312-319 s
            foreach (var x in s.Seconds)
            {
                int k = (int)x.T;
                bool lobby = k < 83, load1 = k >= 83 && k < 88, load2 = k >= 312 && k < 320, match = !lobby && !load1 && !load2 && k < 538;
                x.CpuPercent = 20;
                x.RamPercent = 55;
                x.GpuPercent = lobby ? (k >= 37 && k < 66 ? 12 : k >= 66 ? 30 : 45) : load1 || load2 ? 0.8 : k == 320 ? 11.7 : k >= 538 ? 6 : 33;
                if (cursor) x.CursorVisible = lobby || load1 || load2 ? 1 : 0;
                if (!network) continue;
                x.PacketsInPerSec = k < 77 ? 0 : lobby ? lobbyPk[k - 77] : load1 ? load1Pk[k - 83] : load2 ? load2Pk[k - 312] : 40 + k % 11;
                x.PacketsOutPerSec = x.PacketsInPerSec > 0 ? 30 : 0;
                x.ServerIdx = match || load2 ? (k < 312 ? 0 : 1) : null;
                bool dl = match && InDownload(k);
                x.OtherAppsKbps = dl ? 19000 + (k % 5) * 500 : 100;
                x.PingMs = lobby ? 27 : dl ? 53 : 43.5;
                if (k == 91 || k == 92 || k == 320 || k == 322 || k == 418 || k == 434)
                {
                    x.NetFreezes = 1;
                    x.MaxRecvGapMs = k == 418 ? 543 : k == 434 ? 365 : 300;
                }
                if (dl && perProcess)
                    x.TopDownloaders = new List<NetProcRate> { new() { Name = downloader, Kbps = 19000 } };
            }
            if (network)
            {
                s.Network = new NetworkSummary
                {
                    ServerEndpoints = { "34.1.2.3:7777", "35.4.5.6:7777" },
                    RegionName = "Europa",
                    PingTargetKind = "server",
                    Game = new PingStats { Target = "Server di gioco", Host = "34.1.2.3", AvgMs = 46, MinMs = 37, MaxMs = 98, P95Ms = 55, JitterMs = 4, LossPct = 0, Sent = 540, Received = 540 },
                    ConnectionType = "Ethernet",
                    AvgPacketsInPerSec = 45,
                    AvgOtherAppsKbps = 3700,
                    MaxOtherAppsKbps = 21000,
                    Freezes = 6,
                    LongestFreezeMs = 543,
                    ProcessTrafficMeasured = perProcess
                };
                if (perProcess)
                    s.TopNetworkProcesses = new List<ProcessNetUsage>
                    {
                        new() { Name = downloader, MbDown = 310, MbUp = 4, PeakMbps = 24, MatchSecondsActive = 99, GameContent = downloader == NetProcessAttribution.FortniteContentName }
                    };
            }
            SessionPhases.Apply(s, ftArr, exArr, SessionPhases.FrameSeconds(ts), 2.5, 12);
            return new Synthetic { Ft = ftArr, Excluded = exArr, Session = s };
        }

        private static Synthetic? _evidence;
        private static Synthetic Evidence => _evidence ??= BuildEvidence();

        private static SessionPhase PhaseAt(PerfSession s, int sec) => SessionPhases.PhasesOf(s)[sec];

        // ================= Test =================

        private static void EvidencePhases()
        {
            var s = Evidence.Session;
            T.True(s.Seconds.Count >= 547, $"secondi: {s.Seconds.Count}");
            T.True(s.PhaseSeconds?.FromNetwork == true, "fasi dal traffico del server");
            foreach (int k in new[] { 0, 6, 10, 36, 50, 65, 70, 76 }) T.Equal(SessionPhase.Lobby, PhaseAt(s, k), $"{k} s: lobby");
            foreach (int k in new[] { 77, 80, 82, 83, 85, 87, 312, 313, 315, 318, 319 }) T.Equal(SessionPhase.Loading, PhaseAt(s, k), $"{k} s: caricamento");
            foreach (int k in new[] { 88, 89, 100, 200, 311, 320, 321, 400, 537 }) T.Equal(SessionPhase.Match, PhaseAt(s, k), $"{k} s: partita");
            T.Equal(SessionPhase.Unfocused, PhaseAt(s, 540), "540 s: fuori fuoco");

            var m = s.Matches!;
            T.Equal(2, m.Count, "due partite");
            T.Equal(88, m[0].StartSec, "partita 1: inizio");
            T.Equal(312, m[0].EndSec, "partita 1: fine");
            T.Equal(320, m[1].StartSec, "partita 2: inizio");
            T.Equal(538, m[1].EndSec, "partita 2: fine");
            T.True(m[0].Joined && m[1].Joined, "ingresso in partita misurato");
            T.Equal("34.1.2.3:7777", m[0].Server, "server partita 1");
            T.Equal("35.4.5.6:7777", m[1].Server, "server partita 2");
            var ph = s.PhaseSeconds!;
            T.Near(442, ph.MatchSec, 1, "secondi di partita");
            T.Near(77, ph.LobbySec, 1, "secondi di lobby");
            T.Near(19, ph.LoadingSec, 1, "secondi di caricamento (matchmaking + 2 caricamenti)");
            T.Near(29, ph.LobbyIdleSec, 1, "lobby inattiva a 30 FPS");
            T.True(ph.UnfocusedSec >= 9, "fuori fuoco");
            T.Equal(SessionPhases.Version, s.PhaseVersion, "versione");
        }

        private static void EvidenceMatchStats()
        {
            var s = Evidence.Session;
            var whole = s.Stats;
            var m = s.MatchStats!;
            T.True(s.HeadlineIsMatch, "numeri principali = solo partita");
            T.True(ReferenceEquals(m, s.HeadlineStats), "HeadlineStats = MatchStats");
            T.Near(163, m.AvgFps, 2, "media solo partita");
            T.True(m.Low1Fps >= 64 && m.Low1Fps <= 70, $"1% low solo partita ~67: {m.Low1Fps:0.0}");
            T.True(m.Low01Fps >= 20 && m.Low01Fps <= 23.5, $"0,1% low solo partita ~22: {m.Low01Fps:0.0}");
            T.Near(223.5, m.MaxFrametimeMs, 0.1, "frame più lungo della partita");
            // L'intera sessione è falsata da lobby e caricamenti (come nel report: 0,1% low ~5, frame da 2,6 s).
            T.True(whole.AvgFps < m.AvgFps - 5, $"media sessione intera più bassa: {whole.AvgFps:0}");
            T.True(whole.Low01Fps < 8, $"0,1% low sessione intera: {whole.Low01Fps:0.0}");
            T.Near(2600, whole.MaxFrametimeMs, 1, "frame da 2,6 s nella sessione intera");

            var h = s.Hitches!;
            T.Equal(25.0, h.ThresholdMs, "soglia 25 ms");
            T.Equal(60, h.Count, "60 scatti ≥ 25 ms");
            T.Equal(13, h.CountBig, "13 ≥ 50 ms");
            T.Equal(6, h.CountHuge, "6 ≥ 100 ms");
            T.Near(8.1, h.PerMin, 0.4, "scatti al minuto");
            T.True(h.Low1WithoutFps >= 82 && h.Low1WithoutFps <= 92, $"1% low senza scatti ≥ 25 ms ~87: {h.Low1WithoutFps:0.0}");
            T.True(h.Low01WithoutFps > h.Low01Fps * 2, $"0,1% low senza scatti: {h.Low01WithoutFps:0.0}");
            T.True(h.Low1WithoutBigFps > h.Low1Fps && h.Low1WithoutBigFps < h.Low1WithoutFps, "senza ≥ 50 ms: in mezzo");
            T.Equal(30, h.Top.Count, "al massimo 30 scatti in lista");
            T.True(h.Top.Zip(h.Top.Skip(1)).All(p => p.First.SessionSec <= p.Second.SessionSec), "lista in ordine di tempo");
            foreach (var (at, ms) in HugeSpikes)
            {
                var hit = h.Top.FirstOrDefault(x => Math.Abs(x.Ms - ms) < 0.1);
                T.True(hit != null, $"scatto da {ms} ms in lista");
                if (hit == null) continue;
                T.Near(at, hit.SessionSec, 0.05, $"istante dello scatto da {ms} ms");
            }
            var first = h.Top.First(x => Math.Abs(x.Ms - 108) < 0.1);
            T.Equal(1, first.Match, "108 ms nella partita 1");
            T.Near(1.2, first.MatchSec, 0.05, "108 ms a 0:01 della partita");
            T.True(first.OtherAppsKbps > 5000, "durante il download");
            T.Equal(NetProcessAttribution.FortniteContentName, first.TopDownloader, "chi scaricava");
            var p2 = h.Top.First(x => Math.Abs(x.Ms - 103) < 0.1);
            T.Equal(2, p2.Match, "103 ms nella partita 2");
            T.True(p2.NetFreezeNear, "103 ms vicino al freeze d'ingresso (320 s)");
            T.Equal(60, h.SpikeSeconds.Count, "un secondo per ogni scatto");
        }

        private static void EvidenceInsights()
        {
            var e = Evidence;
            var ins = PerfAnalyzer.Analyze(e.Session, Array.Empty<PerfSession>(), e.Ft);
            var titles = string.Join(" | ", ins.Select(i => i.Title));

            var phases = Get(ins, "Statistiche solo partita");
            T.True(phases != null, "statistiche solo partita: " + titles);
            T.Contains(phases?.Message ?? "", "media 163 FPS", "numeri della partita");
            T.Contains(phases?.Message ?? "", "2 partite", "due partite");
            T.Contains(phases?.Message ?? "", "incluse lobby e caricamenti", "sessione intera come confronto");

            var idle = Get(ins, "Lobby a ~30 FPS (inattività)");
            T.True(idle != null && idle.Severity == CheckStatus.Info, "lobby inattiva solo informativa");
            T.Contains(idle?.Message ?? "", "Fortnite limita gli FPS quando sei inattivo", "spiegazione");

            var overall = ins.FirstOrDefault(i => i.Title is "Gioco fluido" or "Fluidità buona" or "Fluida, ma con scatti isolati" or "Fluidità altalenante" or "Frame molto irregolari");
            T.True(overall != null, "giudizio complessivo");
            T.Equal("Fluida, ma con scatti isolati", overall?.Title, "non «Frame molto irregolari»: il 99% dei frame è regolare");
            T.Contains(overall?.Message ?? "", "Solo partita: media 163", "giudizio sulla partita");

            var lows = Get(ins, "Cosa abbassa 1% e 0,1% low");
            T.True(lows != null, "spiegazione dei low");
            string low1 = Math.Round(e.Session.MatchStats!.Low1Fps).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            string low1No = Math.Round(e.Session.Hitches!.Low1WithoutFps).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            T.Contains(lows?.Message ?? "", $"Se togliamo questi 60 scatti l'1% low passa da {low1} a {low1No} FPS", "impatto degli scatti");
            T.Contains(lows?.Message ?? "", "224 ms a 4:59", "scatto più lungo con l'orario");
            T.Contains(lows?.Message ?? "", "21 di questi scatti sono avvenuti durante un download", "coincidenze con i download");

            var dl = Get(ins, "Download durante la partita");
            T.True(dl != null, "download durante la partita: " + titles);
            T.True(dl?.Severity == CheckStatus.Warn, "correlazione con scatti e ping → Attenzione");
            T.Contains(dl?.Message ?? "", "sempre subito dopo l'inizio della partita", "quando");
            T.Contains(dl?.Message ?? "", "1:28–2:30", "prima finestra");
            T.Contains(dl?.Message ?? "", "Fortnite stesso", "chi: Fortnite");
            T.Contains(dl?.Message ?? "", "310 MB", "quanto (dal totale per programma)");
            T.Contains(dl?.Message ?? "", "53 ms durante i download e 44 ms senza", "ping");
            T.Contains(dl?.Message ?? "", "bufferbloat", "bufferbloat");
            T.Contains(dl?.Message ?? "", "non prova", "correlazione, non prova");
            T.Contains(dl?.Hint ?? "", "Pre-download Streamed Assets", "opzione rimossa da Epic");
            T.Contains(dl?.Hint ?? "", "QoS/SQM", "suggerimento QoS");
            T.True(!Has(ins, "Altre app usano la connessione"), "niente doppione generico");

            var fr = Get(ins, "Freeze di rete a metà partita");
            T.True(fr != null, "freeze a metà partita: " + titles);
            T.Contains(fr?.Message ?? "", "6:58 (543 ms)", "freeze a 418 s");
            T.Contains(fr?.Message ?? "", "7:14 (365 ms)", "freeze a 434 s");
            T.True(!(fr?.Message ?? "").Contains("1:31", StringComparison.Ordinal), "freeze all'ingresso esclusi (91 s)");
            T.True(!(fr?.Message ?? "").Contains("5:20", StringComparison.Ordinal), "freeze all'ingresso esclusi (320 s)");
            T.Contains(fr?.Message ?? "", "Altre 4 subito dopo l'ingresso", "ingressi contati a parte");
            T.True(!Has(ins, "Freeze di rete (lag)"), "niente analisi generica dei freeze");

            var stut = Get(ins, "Stutter = scatti isolati");
            T.True(stut != null && stut.Severity == CheckStatus.Info, "stutter = gli stessi scatti: un solo consiglio");
            T.Contains(stut?.Message ?? "", "di partita", "stutter contati sulla partita");
            T.True(!Has(ins, "Qualche stutter") && !Has(ins, "Molti stutter"), "niente doppione sugli stutter");
            T.True(Has(ins, "FPS limitati dal cap"), "media vicina al cap 165 → limitata dal cap");
        }

        private static void EvidenceReport()
        {
            var e = Evidence;
            var d = new ReportData
            {
                GeneratedAt = new DateTime(2026, 10, 7, 13, 10, 9),
                AppVersion = "1.0.0",
                Session = e.Session,
                Insights = PerfAnalyzer.Analyze(e.Session, Array.Empty<PerfSession>(), e.Ft)
            };
            d.Recommendations = ReportBuilder.BuildRecommendations(d);
            T.True(!d.PerformsWell, "1% low ancora sotto il 60% della media");
            T.Contains(d.Verdict ?? "", "solo partita", "verdetto sulla partita");
            T.Contains(d.Verdict ?? "", "media 163 FPS", "media della partita");
            T.Contains(d.Verdict ?? "", "scatti isolati", "spiegazione onesta dei low");
            T.True(!(d.Verdict ?? "").Contains("media 148", StringComparison.Ordinal), "non la media della sessione intera");
            var first = d.Recommendations.FirstOrDefault();
            T.True(first != null && first.Title != "Frame molto irregolari", "primo consiglio non falsato da lobby e caricamenti: " + first?.Title);
            T.True(d.Recommendations.Any(r => r.Title == "Download durante la partita"), "download tra i consigli");
            T.Equal("Cosa abbassa 1% e 0,1% low", first?.Title, "per primo: cosa abbassa i low (la domanda dell'utente)");

            var html = ReportBuilder.BuildHtml(d);
            T.Contains(html, "solo partita", "riquadri etichettati");
            T.Contains(html, "Sessione intera", "sessione intera mostrata a parte");
            T.Contains(html, "Scatti più lunghi della partita", "tabella degli scatti");
            string low1 = Math.Round(e.Session.MatchStats!.Low1Fps).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            T.Contains(html, $"se togliamo questi 60 scatti l'1% low passa da {low1} a", "riga di spiegazione");
            T.Contains(html, "4:59", "orario dello scatto da 223 ms");
            T.Contains(html, "Fasi della sessione", "fasi");
            T.Contains(html, "Fortnite (download contenuti)", "traffico per programma");
            T.Equal(html.Split("<section").Length, html.Split("</section>").Length, "sezioni bilanciate");

            var text = ReportBuilder.BuildSummaryText(d);
            T.True(text.Length <= ReportBuilder.SummaryMaxChars, "riassunto nel limite");
            T.Contains(text, "Solo partita", "riassunto: partita");
            T.Contains(text, "Sessione intera (incluse lobby e caricamenti)", "riassunto: sessione intera");
            T.Contains(text, "scatti ≥ 25 ms: 60", "riassunto: scatti");

            var row = ReportBuilder.Summarize(e.Session, true);
            T.Near(e.Session.MatchStats!.AvgFps, row.AvgFps, 0.01, "tabella sessioni: media della partita");
            T.True(row.MatchOnly, "tabella sessioni: segnata come solo partita");

            using var _ = JsonDocument.Parse(ReportBuilder.BuildJson(d));
            // Il JSON della sessione contiene le nuove parti.
            var json = ReportBuilder.BuildJson(d);
            T.Contains(json, "\"matchStats\"", "matchStats nel JSON");
            T.Contains(json, "\"hitches\"", "hitches nel JSON");
            T.Contains(json, "\"topDownloaders\"", "download per secondo nel JSON");
        }

        private static void DownloadAttributionVariants()
        {
            var other = BuildEvidence(downloader: "steam");
            var ins = PerfAnalyzer.Analyze(other.Session, Array.Empty<PerfSession>(), other.Ft);
            var dl = Get(ins, "Download durante la partita");
            T.Contains(dl?.Message ?? "", "Il programma che scaricava di più: steam", "altra app nominata");
            T.Contains(dl?.Hint ?? "", "Metti in pausa steam", "suggerimento di metterla in pausa");
            T.True(!(dl?.Hint ?? "").Contains("Pre-download", StringComparison.Ordinal), "niente spiegazione su Fortnite");

            var unknown = BuildEvidence(perProcess: false);
            var ins2 = PerfAnalyzer.Analyze(unknown.Session, Array.Empty<PerfSession>(), unknown.Ft);
            var dl2 = Get(ins2, "Download durante la partita");
            T.Contains(dl2?.Message ?? "", "Non sappiamo quale programma", "onestà: programma non misurato");
            T.Contains(dl2?.Hint ?? "", "anche Fortnite stesso", "Fortnite tra le possibilità");
        }

        private static void FallbackPhases()
        {
            var e = BuildEvidence(network: false, cursor: true);
            var s = e.Session;
            T.True(s.PhaseSeconds is { FromNetwork: false }, "fasi stimate");
            foreach (int k in new[] { 20, 50, 70 }) T.Equal(SessionPhase.Lobby, PhaseAt(s, k), $"{k} s: lobby (cursore / 30 FPS inattivi)");
            foreach (int k in new[] { 84, 86, 313, 316 }) T.Equal(SessionPhase.Loading, PhaseAt(s, k), $"{k} s: caricamento (frame lunghi)");
            foreach (int k in new[] { 100, 200, 400 }) T.Equal(SessionPhase.Match, PhaseAt(s, k), $"{k} s: partita");
            T.True(s.HeadlineIsMatch, "statistiche solo partita anche senza rete");
            T.Near(163, s.MatchStats!.AvgFps, 3, "media partita");
            T.True(s.MatchStats.MaxFrametimeMs < 250, "nessun frame di caricamento nella partita");

            // Senza rete e senza cursore (sessione vecchia): lobby a 120 FPS non distinguibile, ma 30 FPS inattivi e caricamenti sì.
            var old = BuildEvidence(network: false, cursor: false).Session;
            T.Equal(SessionPhase.Lobby, PhaseAt(old, 50), "30 FPS inattivi = lobby");
            T.Equal(SessionPhase.Loading, PhaseAt(old, 84), "caricamento");
            T.True(old.MatchStats!.MaxFrametimeMs < 250, "caricamenti esclusi");
        }

        private static void SmoothingAndMatches()
        {
            // 0-19 lobby, 20-21 traffico (2 s), 22-29 lobby, 30-34 caricamento, 35-99 partita con 3 s senza pacchetti
            // (60-62) e 4 s fuori fuoco (70-73), 100-109 lobby.
            var secs = new List<SecondSample>();
            for (int k = 0; k < 110; k++)
            {
                var x = new SecondSample { T = k, Fps = 160, MaxFrametimeMs = 9, GpuPercent = 40, CpuPercent = 20 };
                bool match = k >= 35 && k < 100;
                x.PacketsInPerSec = (k is 20 or 21) || (k >= 30 && k < 100 && !(k >= 60 && k <= 62)) ? 40 : 0;
                if (k >= 30 && k < 35)
                {
                    x.GpuPercent = 1;
                    x.MaxFrametimeMs = 400;
                }
                if (k >= 70 && k <= 73)
                {
                    x.Unfocused = true;
                    x.Fps = 0;
                }
                if (!match && x.PacketsInPerSec == 0) x.GpuPercent = 45;
                secs.Add(x);
            }
            var p = SessionPhases.Classify(secs, out bool net);
            T.True(net, "dal traffico");
            T.Equal(SessionPhase.Lobby, p[20], "2 s di traffico in lobby = lobby");
            T.Equal(SessionPhase.Lobby, p[21], "2 s di traffico in lobby = lobby (2)");
            T.Equal(SessionPhase.Loading, p[32], "caricamento");
            T.Equal(SessionPhase.Match, p[61], "3 s senza pacchetti a metà partita = partita (freeze)");
            T.Equal(SessionPhase.Unfocused, p[71], "fuori fuoco resta fuori fuoco");
            T.Equal(SessionPhase.Lobby, p[105], "lobby dopo la partita");
            var matches = SessionPhases.FindMatches(p, secs, null);
            T.Equal(1, matches.Count, "Alt+Tab non spezza la partita");
            T.Equal(35, matches[0].StartSec, "inizio");
            T.Equal(100, matches[0].EndSec, "fine");
            T.Near(61, matches[0].DurationSec, 0.1, "secondi di partita senza quelli fuori fuoco");

            // Uno scatto vero da 400 ms a metà partita (GPU che lavora) resta partita: non è un caricamento da nascondere.
            var hitch = secs.Select(x => new SecondSample
            {
                T = x.T, Fps = x.Fps, MaxFrametimeMs = x.MaxFrametimeMs, GpuPercent = x.GpuPercent, PacketsInPerSec = x.PacketsInPerSec, Unfocused = x.Unfocused
            }).ToList();
            hitch[50].MaxFrametimeMs = 400;
            hitch[50].Fps = 120;
            var ph = SessionPhases.Classify(hitch, out _);
            T.Equal(SessionPhase.Match, ph[50], "scatto da 400 ms a metà partita = partita");
            T.Equal(1, SessionPhases.FindMatches(ph, hitch, null).Count, "la partita non viene spezzata");
            hitch[50].GpuPercent = 2; // GPU ferma: allora è davvero un caricamento
            T.Equal(SessionPhase.Loading, SessionPhases.Classify(hitch, out _)[50], "con la GPU ferma è caricamento");
            hitch[50].GpuPercent = 40;
            hitch[51].MaxFrametimeMs = 900;
            hitch[51].Fps = 10;
            hitch[52].MaxFrametimeMs = 700;
            T.Equal(SessionPhase.Loading, SessionPhases.Classify(hitch, out _)[51], "3 s di frame lunghissimi = caricamento");

            // Un secondo isolato "di partita" in mezzo alla lobby diventa lobby.
            var flick = Enumerable.Range(0, 40).Select(k => new SecondSample { T = k, Fps = 120, MaxFrametimeMs = 9, GpuPercent = 40, PacketsInPerSec = 0 }).ToList();
            flick[15].PacketsInPerSec = 30;
            var pf = SessionPhases.Classify(flick, out bool net2);
            T.True(net2, "c'è un secondo collegato");
            T.True(pf.All(x => x == SessionPhase.Lobby), "sfarfallio assorbito");

            // Nessun secondo collegato in tutta la sessione: niente "tutto lobby", si usa la stima.
            var none = Enumerable.Range(0, 120).Select(k => new SecondSample { T = k, Fps = 160, MaxFrametimeMs = 9, GpuPercent = 50, PacketsInPerSec = 0 }).ToList();
            var pn = SessionPhases.Classify(none, out bool net3);
            T.True(!net3, "senza collegamenti si usa la stima");
            T.True(pn.All(x => x == SessionPhase.Match), "senza indizi di lobby: tutto partita (come prima)");
            T.Equal(0, SessionPhases.Classify(null, out _).Length, "null");
        }

        private static void LegacyRecompute()
        {
            var e = BuildEvidence();
            var saved = e.Session;
            // Sessione salvata prima di questa funzione: niente fasi, niente statistiche della partita.
            var old = JsonSerializer.Deserialize<PerfSession>(JsonSerializer.Serialize(saved))!;
            old.PhaseVersion = 0;
            old.MatchStats = null;
            old.Phases = null;
            old.PhaseSeconds = null;
            old.Matches = null;
            old.Hitches = null;
            foreach (var x in old.Seconds) x.TopDownloaders = null;
            old.TopNetworkProcesses = null;
            T.True(!old.HeadlineIsMatch, "senza fasi: numeri della sessione intera");

            var noFt = SessionPhases.Ensure(old, null);
            T.True(noFt != null && noFt.Matches?.Count == 2 && noFt.MatchStats == null, "senza frametime: fasi sì, statistiche no");
            T.True(noFt != null && !noFt.HeadlineIsMatch, "senza frametime restano i numeri della sessione intera");

            var fixedS = SessionPhases.Ensure(old, e.Ft);
            T.True(fixedS != null, "ricalcolo");
            if (fixedS == null) return;
            T.True(fixedS.HeadlineIsMatch, "con i frametime: solo partita");
            T.Near(saved.MatchStats!.AvgFps, fixedS.MatchStats!.AvgFps, 0.5, "stessa media della sessione nuova");
            T.Near(saved.MatchStats.Low1Fps, fixedS.MatchStats.Low1Fps, 1, "stesso 1% low");
            T.Equal(60, fixedS.Hitches!.Count, "stessi scatti");
            T.True(old.MatchStats == null && old.Phases == null, "originale intatto");
            T.True(SessionPhases.Ensure(fixedS, e.Ft) == null, "già aggiornata: niente copia");

            var ins = PerfAnalyzer.Analyze(old, Array.Empty<PerfSession>(), e.Ft);
            T.True(Has(ins, "Statistiche solo partita"), "l'analisi ricalcola le fasi");
            T.True(Has(ins, "Download durante la partita"), "download riconosciuti anche senza traffico per programma");
            T.Contains(Get(ins, "Download durante la partita")?.Message ?? "", "Non sappiamo quale programma", "programma non misurato");
        }

        private static void FrameTimeline()
        {
            var ft = new float[] { 10, 10, 10, 10 };
            var end = SessionPhases.FrameEndTimes(ft, new List<FrameGap> { new() { Index = 2, Ms = 6000 } });
            T.Near(10, end[0], 1e-9, "primo");
            T.Near(6030, end[2], 1e-9, "dopo la pausa");
            T.Near(6040, end[3], 1e-9, "ultimo");
            var sec = SessionPhases.FrameSeconds(new double[] { 999, 1000, 1000.5, 2500 });
            T.Equal(0, sec[0], "999 ms → secondo 0");
            T.Equal(0, sec[1], "1000 ms → secondo 0 (fine inclusa)");
            T.Equal(1, sec[2], "1000,5 ms → secondo 1");
            T.Equal(2, sec[3], "2500 ms → secondo 2");
            var gaps = SessionPhases.FindGaps(new float[] { 10, 10, 10 }, new double[] { 10, 20, 7030 });
            T.Equal(1, gaps.Count, "una pausa");
            T.Equal(2, gaps[0].Index, "dopo il secondo frame");
            T.Near(7000, gaps[0].Ms, 0.1, "durata della pausa");
            T.Equal("7:22", SessionPhases.Clock(442.9), "orario m:ss");
            T.Equal("1:00:05", SessionPhases.Clock(3605), "orario h:mm:ss");
        }

        private static void MatchFramesAndStore()
        {
            var e = Evidence;
            var mask = SessionPhases.MatchMask(e.Session, e.Ft);
            T.True(mask != null, "maschera della partita");
            T.Equal(e.Session.MatchStats!.Frames, mask?.Count(x => x) ?? 0, "frame della maschera = frame delle statistiche solo partita");
            var sw = new System.IO.StringWriter();
            ReportBuilder.WriteFrametimesCsv(sw, e.Ft, e.Excluded, mask);
            var lines = sw.ToString().Split('\n');
            T.Equal("index,time_ms,frametime_ms,fps,focused,match", lines[0], "intestazione con match");
            T.True(lines[1].EndsWith(",1,0", StringComparison.Ordinal), "primo frame: in primo piano, lobby");
            var csvMatch = lines.Skip(1).Where(l => l.Length > 0).Select(l => l.Split(',')).Where(c => c[5] == "1").Select(c => float.Parse(c[2], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            T.Near(e.Session.MatchStats.Low1Fps, FrameStats.Compute(csvMatch).Low1Fps, 0.5, "dal CSV si riottiene l'1% low della partita");
            var sw2 = new System.IO.StringWriter();
            ReportBuilder.WriteFrametimesCsv(sw2, new float[] { 5, 5 });
            T.True(sw2.ToString().StartsWith("index,time_ms,frametime_ms,fps,focused\n", StringComparison.Ordinal), "senza fasi: intestazione di prima");
            T.True(SessionPhases.MatchMask(new PerfSession(), e.Ft) == null, "senza fasi nessuna maschera");

            // Archivio: le nuove parti sopravvivono a salvataggio e lettura; i null per secondo non vengono scritti.
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fnboost-phase-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new PerfSessionStore(dir, 10);
                store.Save(e.Session, e.Ft);
                var json = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, e.Session.Id + ".json"));
                T.Contains(json, "\"Phase\": \"Match\"", "fasi salvate come testo");
                T.True(!json.Contains("\"TopDownloaders\": null", StringComparison.Ordinal), "null per secondo non scritti");
                var back = store.List().First();
                T.True(back.HeadlineIsMatch, "dopo la lettura: solo partita");
                T.Near(e.Session.MatchStats.AvgFps, back.MatchStats!.AvgFps, 1e-9, "MatchStats");
                T.Equal(2, back.Matches!.Count, "partite");
                T.Equal(60, back.Hitches!.Count, "scatti");
                T.True(SessionPhases.Ensure(back, store.LoadFrametimes(back.Id)) == null, "nessun ricalcolo per una sessione nuova");
            }
            finally
            {
                try { System.IO.Directory.Delete(dir, true); } catch { /* pulizia best-effort */ }
            }

            // Andamento: sessioni "solo partita" e sessioni intere vecchie non si mescolano (i low vecchi sono falsati).
            var olds = new List<PerfSession>();
            for (int i = 0; i < 3; i++)
                olds.Add(new PerfSession { Id = "o" + i, ProcessName = Fn, StartedAt = new DateTime(2026, 9, 1 + i), Stats = new FrameStatsResult { Frames = 1000, AvgFps = 148, Low1Fps = 21, DurationSec = 500 } });
            var news = new List<PerfSession>();
            for (int i = 0; i < 3; i++)
            {
                var n = JsonSerializer.Deserialize<PerfSession>(JsonSerializer.Serialize(e.Session))!;
                n.Id = "n" + i;
                n.StartedAt = new DateTime(2026, 10, 1 + i);
                news.Add(n);
            }
            var trend = PerfAnalyzer.Trend(news.Concat(olds).ToList());
            T.True(!trend.Any(i => i.Title == "Prestazioni in miglioramento"), "nessun finto miglioramento dal cambio di metodo");
            var stable = trend.FirstOrDefault(i => i.Title == "Prestazioni stabili");
            T.True(stable != null, "le 3 sessioni nuove confrontate tra loro");
            T.Contains(stable?.Message ?? "", "(solo partita)", "andamento etichettato");

            // 4 ore (14.400 secondi): la classificazione resta veloce.
            var big = new List<SecondSample>(14400);
            for (int k = 0; k < 14400; k++)
                big.Add(new SecondSample { T = k, Fps = 160, MaxFrametimeMs = k % 900 == 0 ? 1200 : 9, GpuPercent = k % 900 < 3 ? 1 : 40, PacketsInPerSec = k % 900 < 60 ? 0 : 45 });
            var sw3 = System.Diagnostics.Stopwatch.StartNew();
            var p = SessionPhases.Classify(big, out _);
            sw3.Stop();
            T.True(sw3.ElapsedMilliseconds < 1000, $"4 ore classificate in {sw3.ElapsedMilliseconds} ms");
            T.Equal(16, SessionPhases.FindMatches(p, big, null).Count, "16 partite");
        }

        // ================= Traffico per programma =================

        private static void TcpPayload()
        {
            var b = new byte[44];
            BitConverter.GetBytes(4321u).CopyTo(b, 0);
            BitConverter.GetBytes(65536u).CopyTo(b, 4);
            T.True(KernelNetPayload.TryReadPidSize(b, out int pid, out int size), "lettura");
            T.Equal(4321, pid, "PID");
            T.Equal(65536, size, "dimensione (un evento TCP può riassumere più segmenti)");
            T.True(!KernelNetPayload.TryReadPidSize(new byte[7], out _, out _), "troppo corto");
            BitConverter.GetBytes(uint.MaxValue).CopyTo(b, 4);
            T.True(!KernelNetPayload.TryReadPidSize(b, out _, out _), "dimensione assurda");
        }

        private static void PidAggregation()
        {
            var agg = new ProcessTrafficAggregator();
            agg.Add(100, 10, true, 1000, tcp: true);
            agg.Add(200, 10, true, 500, tcp: false);
            agg.Add(300, 10, false, 200, tcp: true);
            agg.Add(1500, 20, true, 4000, tcp: true);
            agg.Add(900, 30, true, 0, tcp: true); // vuoto: ignorato
            T.True(agg.TcpSeen, "TCP visto");
            var first = agg.Advance(1000);
            T.Equal(1, first.Count, "un secondo chiuso");
            var p10 = first[0].Pids.First(x => x.Pid == 10).Bytes;
            T.Equal(1000L, p10.TcpIn, "TCP ricevuto");
            T.Equal(500L, p10.UdpIn, "UDP ricevuto");
            T.Equal(200L, p10.TcpOut, "TCP inviato");
            T.True(first[0].Pids.All(x => x.Pid != 30), "eventi vuoti ignorati");
            // Evento in ritardo (secondo 0 già chiuso): va nel primo secondo aperto, i totali restano giusti.
            agg.Add(800, 10, true, 300, tcp: true);
            T.Equal(1L, agg.Late, "in ritardo contato");
            var second = agg.Advance(2000);
            T.Equal(1, second.Count, "secondo 1");
            T.True(second[0].Pids.Any(x => x.Pid == 10 && x.Bytes.TcpIn == 300), "ritardatario nel secondo aperto");
            T.True(second[0].Pids.Any(x => x.Pid == 20 && x.Bytes.TcpIn == 4000), "PID 20");
            T.Equal(0, agg.Advance(2000).Count, "niente da chiudere");

            // Limite di PID per secondo: gli altri finiscono in "altri processi".
            var many = new ProcessTrafficAggregator();
            for (int pid = 1; pid <= ProcessTrafficAggregator.MaxPids + 50; pid++) many.Add(5000, pid, true, 10, tcp: true);
            var ms = many.Advance(6000);
            T.Equal(ProcessTrafficAggregator.MaxPids + 1, ms[0].Pids.Length, "PID limitati + altri");
            T.Equal(500L, ms[0].Pids.First(x => x.Pid == ProcessTrafficAggregator.OtherPid).Bytes.TcpIn, "50 PID in altri");

            // Limite di secondi aperti (istanti anomali).
            var odd = new ProcessTrafficAggregator();
            for (int k = 0; k < ProcessTrafficAggregator.MaxOpenSeconds + 5; k++) odd.Add(k * 1000.0 + 10, 1, true, 10, tcp: false);
            T.Equal(5L, odd.Dropped, "secondi oltre il limite scartati");
            T.Equal(ProcessTrafficAggregator.MaxOpenSeconds, odd.Advance(1e9).Count, "chiusi tutti");
        }

        private static void Attribution()
        {
            const int game = 100, steam = 200, chrome1 = 300, chrome2 = 301, eac = 400, gone = 500;
            var names = new Dictionary<int, string>
            {
                [game] = Fn, [steam] = "steam", [chrome1] = "chrome", [chrome2] = "chrome", [eac] = "EasyAntiCheat_EOS"
            };
            string? NameOf(int pid) => names.TryGetValue(pid, out var n) ? n : null;
            ProcessSecond Sec(double start, params (int Pid, PidCounters C)[] pids) => new() { StartMs = start, Pids = pids };
            var secs = new List<ProcessSecond>
            {
                Sec(0, (game, new PidCounters { TcpIn = 2_500_000, UdpIn = 9_000, UdpOut = 4_000 }),
                    (steam, new PidCounters { TcpIn = 125_000 }),
                    (chrome1, new PidCounters { UdpIn = 50_000 }), (chrome2, new PidCounters { TcpIn = 25_000 }),
                    (eac, new PidCounters { TcpIn = 30_000 }), (gone, new PidCounters { TcpIn = 20_000 })),
                Sec(1000, (game, new PidCounters { TcpIn = 2_500_000 }))
            };
            var list = NetProcessAttribution.Attribute(secs, game, NameOf, "FNBoost");
            var fn = list.FirstOrDefault(x => x.Name == NetProcessAttribution.FortniteContentName);
            T.True(fn != null && fn.GameContent, "TCP del gioco = Fortnite (download contenuti)");
            T.Equal(5_000_000L, fn?.BytesIn ?? 0, "solo TCP del gioco (l'UDP è la partita)");
            T.Near(20_000, fn?.KbpsIn ?? 0, 0.1, "20 Mbit/s di media su 2 s");
            T.Equal(list[0].Name, NetProcessAttribution.FortniteContentName, "ordinati per download");
            var ch = list.FirstOrDefault(x => x.Name == "chrome");
            T.Equal(75_000L, ch?.BytesIn ?? 0, "processi con lo stesso nome sommati (TCP + UDP/QUIC)");
            T.True(list.All(x => !x.Name.StartsWith("EasyAntiCheat", StringComparison.Ordinal)), "anti-cheat non nominato");
            T.True(list.Any(x => x.Name == NetProcessAttribution.UnknownName), "PID sconosciuto");
            // PID del gioco non ancora noto: il client di Fortnite è riconosciuto dal nome.
            var noPid = NetProcessAttribution.Attribute(secs, 0, NameOf);
            T.True(noPid.Any(x => x.Name == NetProcessAttribution.FortniteContentName && x.BytesIn == 5_000_000), "Fortnite dal nome");

            var top = NetProcessAttribution.TopDownloaders(list);
            T.True(top.Count <= 3 && top[0].Name == NetProcessAttribution.FortniteContentName, "primi 3");
            T.True(top.All(x => x.Kbps >= NetProcessAttribution.MinListedKbps), "solo sopra 100 kbit/s");

            var totals = new ProcessNetTotals();
            totals.Add(list);
            totals.Add(list);
            totals.AddMatchSecond(list);
            var t = totals.Top();
            var tf = t.First(x => x.GameContent);
            T.Near(10, tf.MbDown, 0.01, "MB totali");
            T.Near(20, tf.PeakMbps, 0.01, "picco");
            T.Equal(1, tf.MatchSecondsActive, "secondi di partita con download");
            T.True(t.All(x => x.MbDown + x.MbUp >= 1), "solo programmi con almeno 1 MB");
            T.Contains(NetProcessAttribution.Describe("svchost"), "Windows Update", "svchost spiegato");
        }

        private static PerfInsight? Get(List<PerfInsight> list, string title) => list.FirstOrDefault(i => i.Title == title);
        private static bool Has(List<PerfInsight> list, string title) => Get(list, title) != null;
    }
}
