using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

// Questo file non dipende da WPF né da API di Windows: viene compilato anche dal progetto di test su Linux.
//
// PERCHÉ SERVE. Una sessione registrata contiene lobby, menu, schermate di caricamento e partite. In lobby Fortnite
// gira a FPS diversi (anche ~30 FPS quando sei inattivo, per risparmiare energia) e nei caricamenti presenta frame da
// 1-3 secondi: mescolati alla partita fanno crollare 1% low, 0,1% low e regolarità e i consigli diventano sbagliati.
// Qui si classifica ogni secondo (fuori fuoco / lobby / caricamento / partita) e si calcolano le statistiche "solo
// partita", la lista degli scatti e quanto pesano sui "low". È la stessa logica per le sessioni nuove (PerfService)
// e per quelle vecchie (ricalcolate al volo all'analisi, come FocusFilter.RepairLegacy).

namespace FNBoost.Perf
{
    /// <summary>Classificazione delle fasi di una sessione e statistiche della sola partita.</summary>
    public static class SessionPhases
    {
        /// <summary>Versione del calcolo: le sessioni con una versione più vecchia vengono ricalcolate all'analisi.</summary>
        public const int Version = 2;

        /// <summary>Pacchetti al secondo dal server sotto cui si è in lobby (in partita il server ne manda 30-100).</summary>
        public const double MinPacketsPerSec = 5;
        /// <summary>Pacchetti ricevuti senza quasi nulla inviato per almeno così tanti secondi: non è una partita (es. chat vocale).</summary>
        public const int MinOneWaySec = 10;
        /// <summary>Un frame così lungo è una schermata di caricamento, non uno scatto di gioco.</summary>
        public const double LoadingFrameMs = 250;
        /// <summary>GPU sotto questa soglia mentre si è collegati a un server: caricamento (in partita la GPU lavora).</summary>
        public const double LoadingGpuMax = 8;
        /// <summary>
        /// La soglia della GPU è relativa alla sessione: sotto il 30% della mediana dei secondi di gioco (al massimo 8%).
        /// Con una GPU molto potente e il cap degli FPS la partita può stare al 6-9%: una soglia fissa la scambierebbe per caricamento.
        /// </summary>
        public const double LoadingGpuRel = 0.3;
        /// <summary>Tratti più corti di così (lobby in mezzo alla partita o viceversa) sono sfarfallii e vengono assorbiti.</summary>
        public const int MinSegmentSec = 5;
        /// <summary>I primi secondi dopo l'inizio del collegamento a un server sono ingresso/caricamento.</summary>
        public const int JoinLoadingSec = 2;
        /// <summary>Se entro così tanti secondi dal collegamento c'è una schermata di caricamento, quello che la precede è matchmaking.</summary>
        public const int PreLoadingWindowSec = 20;
        /// <summary>Lobby inattiva: Fortnite limita gli FPS a ~30 con la GPU quasi ferma.</summary>
        public const double IdleFpsMin = 22, IdleFpsMax = 38, IdleGpuMax = 15;
        /// <summary>Senza dati di rete: cursore visibile per almeno così tanti secondi (vicino a lobby/caricamenti) = menu.</summary>
        public const int CursorLobbySec = 10;
        public const double CursorLobbyFraction = 0.9;
        /// <summary>Una pausa della cattura oltre 5 s lascia almeno così tanti secondi senza frame (sessioni vecchie senza FrameGaps).</summary>
        private const int MinPauseEmptySec = 4;
        /// <summary>Differenza massima (s) tra la somma dei frametime e la durata della sessione per allineare frame e secondi.</summary>
        private const double MaxTimelineDriftSec = 2;
        /// <summary>Freeze di rete nei primi secondi dopo l'ingresso in partita: normali (il server sta caricando), non contano.</summary>
        public const int JoinFreezeGraceSec = 10;
        /// <summary>Soglie degli scatti (ms).</summary>
        public const double HitchMs = 25, BigHitchMs = 50, HugeHitchMs = 100;
        public const int MaxHitchList = 30;
        private const int MaxSpikeSeconds = 5000;

        // ================= Classificazione =================

        /// <summary>
        /// Fase di ogni secondo. Con i dati di rete (pacchetti dal server misurati nella maggior parte dei secondi e almeno
        /// un tratto collegato): lobby = meno di <see cref="MinPacketsPerSec"/> pacchetti/s dal server, o un collegamento in
        /// cui il PC quasi non invia (non è una partita), o la lobby inattiva a ~30 FPS; caricamento = secondi collegati con
        /// un frame ≥ 250 ms (più i secondi vicini, se non sembrano gioco) o con la GPU ferma (sotto il 30% della mediana,
        /// al massimo 8%), i primi 2 s di ogni collegamento e il matchmaking che precede una schermata di caricamento;
        /// il resto collegato = partita.
        /// Senza dati di rete: lobby = tratti di almeno 5 s a ~30 FPS con la GPU sotto il 15% (lobby inattiva) oppure
        /// con il cursore visibile accanto a lobby/caricamenti; caricamento = frame ≥ 250 ms; il resto = partita.
        /// Un "caricamento" di al massimo 3 s in mezzo alla partita è uno scatto e resta partita.
        /// Infine i tratti di partita o di lobby più corti di 5 s vengono assorbiti dai vicini.
        /// </summary>
        public static SessionPhase[] Classify(IReadOnlyList<SecondSample>? seconds, out bool fromNetwork)
        {
            fromNetwork = false;
            int n = seconds?.Count ?? 0;
            var result = new SessionPhase[n];
            if (seconds == null || n == 0) return result;

            // Si lavora sulla sequenza dei soli secondi in primo piano: i secondi fuori fuoco sono "trasparenti".
            var idx = new List<int>(n);
            for (int i = 0; i < n; i++)
            {
                if (seconds[i] == null || seconds[i].Unfocused) result[i] = SessionPhase.Unfocused;
                else idx.Add(i);
            }
            int m = idx.Count;
            if (m == 0) return result;
            var secs = new SecondSample[m];
            for (int k = 0; k < m; k++) secs[k] = seconds[idx[k]];

            int withPackets = secs.Count(x => InPackets(x).HasValue);
            bool anyConnected = secs.Any(x => InPackets(x) is >= MinPacketsPerSec);
            fromNetwork = withPackets * 2 >= m && anyConnected;

            var p = fromNetwork ? ClassifyNetwork(secs) : ClassifyFallback(secs);
            KeepMidMatchHitches(secs, p);
            Smooth(p);
            for (int k = 0; k < m; k++) result[idx[k]] = p[k];
            return result;
        }

        /// <summary>
        /// Pacchetti ricevuti nel secondo dal server (l'indirizzo pubblico che ne ha mandati di più); per le sessioni
        /// registrate prima di questa misura, tutti i pacchetti UDP ricevuti dal gioco.
        /// </summary>
        private static double? InPackets(SecondSample x) => x.ServerPacketsInPerSec ?? x.PacketsInPerSec;

        private static SessionPhase[] ClassifyNetwork(SecondSample[] s)
        {
            int m = s.Length;
            // ---- collegato al server: pacchetti ≥ 5/s; secondi non misurati = come il vicino misurato ----
            // Una partita è traffico nei due sensi (il client invia e conferma decine di pacchetti al secondo): almeno 10 s
            // di pacchetti ricevuti senza quasi nulla inviato (es. si ascolta la chat vocale del party in lobby) non sono
            // un collegamento a un server di gioco. Più corti (una schermata di caricamento) non contano.
            var oneWay = new bool[m];
            for (int k = 0; k < m; k++)
                oneWay[k] = InPackets(s[k]) is >= MinPacketsPerSec && s[k].PacketsOutPerSec is { } po && po < MinPacketsPerSec;
            ForRuns(oneWay, true, (a, b) =>
            {
                if (b - a >= MinOneWaySec) return;
                for (int k = a; k < b; k++) oneWay[k] = false;
            });
            var known = new bool?[m];
            for (int k = 0; k < m; k++)
                if (InPackets(s[k]) is { } pk) known[k] = pk >= MinPacketsPerSec && !oneWay[k];
            var conn = new bool[m];
            bool? last = null;
            for (int k = 0; k < m; k++)
            {
                if (known[k].HasValue) last = known[k];
                conn[k] = last ?? false;
            }
            int firstKnown = Array.FindIndex(known, x => x.HasValue);
            for (int k = 0; k < firstKnown && firstKnown >= 0; k++) conn[k] = known[firstKnown]!.Value;

            // Pause brevi del traffico in mezzo a un collegamento (freeze, perdite) restano collegate;
            // brevi raffiche di pacchetti in lobby (matchmaking, beacon) non sono un collegamento.
            FillShortRuns(conn, false, MinSegmentSec, bothSides: true);
            FillShortRuns(conn, true, MinSegmentSec, bothSides: false);

            var p = new SessionPhase[m];
            for (int k = 0; k < m; k++) p[k] = conn[k] ? SessionPhase.Match : SessionPhase.Lobby;

            double gpuLow = GpuLowThreshold(s, conn);
            bool LoadSignal(int k) => conn[k] && (s[k].MaxFrametimeMs >= LoadingFrameMs || s[k].Fps <= 0 ||
                                                   s[k].GpuPercent is { } g && g < gpuLow);
            for (int k = 0; k < m; k++)
                if (LoadSignal(k)) p[k] = SessionPhase.Loading;

            // ---- inizio di ogni collegamento: ingresso e matchmaking prima della schermata di caricamento ----
            for (int k = 0; k < m; k++)
            {
                if (!conn[k] || (k > 0 && conn[k - 1])) continue;
                int end = k;
                while (end < m && conn[end]) end++;
                int firstLoad = -1;
                for (int j = k; j < Math.Min(end, k + PreLoadingWindowSec); j++)
                    if (LoadSignal(j)) { firstLoad = j; break; }
                int upTo;
                if (firstLoad < 0) upTo = k > 0 ? Math.Min(end, k + JoinLoadingSec) : k;
                else if (k > 0) upTo = firstLoad;
                else
                {
                    // Registrazione iniziata già collegati: nessun ingresso osservato. Quello che precede il primo
                    // caricamento è matchmaking solo se è una vera schermata di caricamento (più di 3 s), non uno scatto.
                    int loadEnd = firstLoad;
                    while (loadEnd < end && LoadSignal(loadEnd)) loadEnd++;
                    upTo = loadEnd - firstLoad > MaxHitchLoadingSec ? firstLoad : k;
                }
                for (int j = k; j < upTo; j++) p[j] = SessionPhase.Loading;
            }

            MarkLongFrameNeighbours(s, p, conn, gpuLow);

            // Rete di sicurezza: ~30 FPS con la GPU quasi ferma per almeno 5 s è la lobby inattiva anche se arrivano
            // pacchetti (es. chat vocale del party): in partita Fortnite non si limita a 30 FPS.
            MarkIdleLobby(s, p, requireShortFrames: true);
            return p;
        }

        /// <summary>
        /// GPU "ferma" (caricamento): sotto il 30% della mediana dei secondi che sembrano gioco (collegati, con frame e senza
        /// frame lunghissimi), al massimo 8%. Senza misure della GPU: 8% (non scatta mai).
        /// </summary>
        private static double GpuLowThreshold(SecondSample[] s, bool[]? conn)
        {
            var g = new List<double>();
            for (int k = 0; k < s.Length; k++)
                if ((conn == null || conn[k]) && s[k].Fps > 0 && s[k].MaxFrametimeMs < LoadingFrameMs && s[k].GpuPercent is { } v)
                    g.Add(v);
            return g.Count > 0 ? Math.Min(LoadingGpuMax, LoadingGpuRel * NetStats.Median(g)) : LoadingGpuMax;
        }

        /// <summary>Lobby inattiva: tratti di almeno 5 s a ~30 FPS con la GPU quasi ferma (con requireShortFrames: senza frame ≥ 250 ms).</summary>
        private static void MarkIdleLobby(SecondSample[] s, SessionPhase[] p, bool requireShortFrames)
        {
            int m = s.Length;
            var idle = new bool[m];
            for (int k = 0; k < m; k++) idle[k] = IsIdle(s[k]) && (!requireShortFrames || s[k].MaxFrametimeMs < LoadingFrameMs);
            ForRuns(idle, true, (a, b) =>
            {
                if (b - a < MinSegmentSec) return;
                for (int k = a; k < b; k++) p[k] = SessionPhase.Lobby;
            });
        }

        private static SessionPhase[] ClassifyFallback(SecondSample[] s)
        {
            int m = s.Length;
            var p = new SessionPhase[m];
            for (int k = 0; k < m; k++) p[k] = SessionPhase.Match;

            // ---- lobby inattiva: ~30 FPS con la GPU quasi ferma per almeno 5 s ----
            MarkIdleLobby(s, p, requireShortFrames: false);

            // ---- caricamenti: frame lunghissimi, o nessun frame per un secondo intero con il gioco in primo piano ----
            for (int k = 0; k < m; k++)
                if (s[k].MaxFrametimeMs >= LoadingFrameMs || s[k].Fps <= 0) p[k] = SessionPhase.Loading;
            MarkLongFrameNeighbours(s, p, null, GpuLowThreshold(s, null));

            // ---- menu: cursore visibile a lungo, accanto a lobby/caricamenti o all'inizio/alla fine ----
            var cursor = new bool[m];
            for (int k = 0; k < m; k++) cursor[k] = s[k].CursorVisible is >= CursorLobbyFraction && p[k] == SessionPhase.Match;
            ForRuns(cursor, true, (a, b) =>
            {
                if (b - a < CursorLobbySec) return;
                bool touches = a <= 2 || b >= m - 2;
                for (int j = Math.Max(0, a - 2); j < Math.Min(m, b + 2) && !touches; j++)
                    if (j < a || j >= b) touches = p[j] != SessionPhase.Match;
                if (!touches) return;
                for (int k = a; k < b; k++) p[k] = SessionPhase.Lobby;
            });
            return p;
        }

        private static bool IsIdle(SecondSample x) =>
            x.Fps >= IdleFpsMin && x.Fps <= IdleFpsMax && x.GpuPercent is { } g && g < IdleGpuMax;

        /// <summary>
        /// I secondi accanto a un frame ≥ 250 ms fanno parte del caricamento (il frame lungo inizia nel secondo prima),
        /// a meno che sembrino gioco vero: GPU non ferma (o non misurata), FPS ≥ 60% della mediana della partita e nessun frame lunghissimo.
        /// </summary>
        private static void MarkLongFrameNeighbours(SecondSample[] s, SessionPhase[] p, bool[]? conn, double gpuLow)
        {
            int m = s.Length;
            var matchFps = new List<double>();
            for (int k = 0; k < m; k++)
                if (p[k] == SessionPhase.Match && s[k].Fps > 0) matchFps.Add(s[k].Fps);
            double median = matchFps.Count > 0 ? NetStats.Median(matchFps) : 0;
            bool Gameplay(SecondSample x) =>
                x.MaxFrametimeMs < LoadingFrameMs && x.Fps >= 0.6 * median && !(x.GpuPercent is { } g && g < gpuLow);

            var longFrame = new bool[m];
            for (int k = 0; k < m; k++) longFrame[k] = s[k].MaxFrametimeMs >= LoadingFrameMs && (conn == null || conn[k]);
            for (int k = 0; k < m; k++)
            {
                if (!longFrame[k]) continue;
                foreach (int j in new[] { k - 1, k + 1 })
                {
                    if (j < 0 || j >= m || p[j] != SessionPhase.Match) continue;
                    if (conn != null && !conn[j]) continue;
                    if (!Gameplay(s[j])) p[j] = SessionPhase.Loading;
                }
            }
        }

        /// <summary>Un "caricamento" di al massimo questi secondi in mezzo alla partita è uno scatto vero (le schermate di caricamento durano 5-8 s).</summary>
        public const int MaxHitchLoadingSec = 3;
        private const int MatchContextSec = 10;

        /// <summary>
        /// Un "caricamento" di al massimo 3 s in mezzo alla partita (almeno 10 s di partita prima e dopo, stesso server) è
        /// uno scatto vero, non una schermata di caricamento: resta nella partita e nelle sue statistiche. Qui non si guardano
        /// GPU e FPS: durante un blocco di 1-3 s la GPU resta ferma e nessun frame finisce, eppure sono proprio gli scatti
        /// che l'1% e lo 0,1% low devono mostrare. Le schermate di caricamento vere durano di più (5-8 s).
        /// </summary>
        private static void KeepMidMatchHitches(SecondSample[] s, SessionPhase[] p)
        {
            ForRuns(p, SessionPhase.Loading, (a, b) =>
            {
                if (b - a > MaxHitchLoadingSec || a == 0 || b >= p.Length) return;
                if (p[a - 1] != SessionPhase.Match || p[b] != SessionPhase.Match) return;
                // Partita vera da entrambi i lati (almeno 10 s): all'avvio del gioco o tra i menu i frame lunghi sono caricamenti.
                int left = 0, right = 0;
                while (a - 1 - left >= 0 && p[a - 1 - left] == SessionPhase.Match) left++;
                while (b + right < p.Length && p[b + right] == SessionPhase.Match) right++;
                if (left < MatchContextSec || right < MatchContextSec) return;
                if (ServerChanged(s, p, a - 1, b)) return;
                for (int k = a; k < b; k++) p[k] = SessionPhase.Match;
            });
        }

        /// <summary>
        /// true se il server noto più vicino prima di <paramref name="before"/> (incluso) e quello dopo <paramref name="after"/>
        /// (incluso), cercati tra i secondi di partita entro 10 s, sono diversi: sono due partite.
        /// </summary>
        private static bool ServerChanged(IReadOnlyList<SecondSample?> s, IReadOnlyList<SessionPhase> p, int before, int after)
        {
            int? Find(int from, int step)
            {
                for (int j = from, c = 0; j >= 0 && j < s.Count && j < p.Count && c < MatchContextSec; j += step, c++)
                    if (p[j] == SessionPhase.Match && s[j]?.ServerIdx is { } si) return si;
                return null;
            }
            return Find(before, -1) is { } x && Find(after, 1) is { } y && x != y;
        }

        /// <summary>
        /// Sfarfallii: un tratto di partita più corto di 5 s diventa caricamento (se tocca un caricamento) o lobby;
        /// un tratto di lobby più corto di 5 s tra due tratti non-lobby diventa partita (se entrambi i vicini sono partita)
        /// o caricamento.
        /// </summary>
        private static void Smooth(SessionPhase[] p)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                ForRuns(p, SessionPhase.Match, (a, b) =>
                {
                    if (b - a >= MinSegmentSec) return;
                    SessionPhase? before = a > 0 ? p[a - 1] : null, after = b < p.Length ? p[b] : null;
                    if (before == null && after == null) return;
                    var to = before == SessionPhase.Loading || after == SessionPhase.Loading ? SessionPhase.Loading : SessionPhase.Lobby;
                    for (int k = a; k < b; k++) p[k] = to;
                });
                ForRuns(p, SessionPhase.Lobby, (a, b) =>
                {
                    if (b - a >= MinSegmentSec || a == 0 || b >= p.Length) return;
                    var to = p[a - 1] == SessionPhase.Match && p[b] == SessionPhase.Match ? SessionPhase.Match : SessionPhase.Loading;
                    for (int k = a; k < b; k++) p[k] = to;
                });
            }
        }

        private static void ForRuns<TV>(TV[] values, TV wanted, Action<int, int> action)
        {
            var cmp = EqualityComparer<TV>.Default;
            int i = 0;
            while (i < values.Length)
            {
                if (!cmp.Equals(values[i], wanted))
                {
                    i++;
                    continue;
                }
                int start = i;
                while (i < values.Length && cmp.Equals(values[i], wanted)) i++;
                action(start, i);
            }
        }

        /// <summary>Tratti di <paramref name="value"/> più corti di minLen diventano l'opposto (bothSides: solo se racchiusi).</summary>
        private static void FillShortRuns(bool[] flags, bool value, int minLen, bool bothSides)
        {
            ForRuns(flags, value, (a, b) =>
            {
                if (b - a >= minLen) return;
                bool enclosed = a > 0 && b < flags.Length;
                if (bothSides && !enclosed) return;
                if (!bothSides && a == 0 && b == flags.Length) return;
                for (int k = a; k < b; k++) flags[k] = !value;
            });
        }

        /// <summary>Fasi come tratti consecutivi [StartSec, EndSec).</summary>
        public static List<PhaseSegment> Segments(IReadOnlyList<SessionPhase> phases)
        {
            var list = new List<PhaseSegment>();
            int i = 0;
            while (i < phases.Count)
            {
                int start = i;
                while (i < phases.Count && phases[i] == phases[start]) i++;
                list.Add(new PhaseSegment { Phase = phases[start], StartSec = start, EndSec = i });
            }
            return list;
        }

        /// <summary>Fase di ogni secondo ricostruita dai tratti salvati (Unfocused dove manca).</summary>
        public static SessionPhase[] FromSegments(IReadOnlyList<PhaseSegment>? segments, int n)
        {
            var p = new SessionPhase[Math.Max(0, n)];
            if (segments == null) return p;
            foreach (var seg in segments)
            {
                if (seg == null) continue;
                for (int k = Math.Max(0, seg.StartSec); k < Math.Min(n, seg.EndSec); k++) p[k] = seg.Phase;
            }
            return p;
        }

        /// <summary>
        /// Partite = tratti di secondi in partita, uniti se separati solo da secondi fuori fuoco (un Alt+Tab non chiude la
        /// partita) o da un "caricamento" di al massimo 3 s con lo stesso server (uno scatto lungo non spezza la partita).
        /// Server = il più frequente tra i secondi della partita (indice in <paramref name="servers"/>).
        /// </summary>
        public static List<MatchSegment> FindMatches(IReadOnlyList<SessionPhase> p, IReadOnlyList<SecondSample>? seconds,
            IReadOnlyList<string>? servers)
        {
            var list = new List<MatchSegment>();
            int n = p.Count;
            int i = 0;
            while (i < n)
            {
                if (p[i] != SessionPhase.Match)
                {
                    i++;
                    continue;
                }
                int start = i, lastMatch = i;
                int k = i;
                while (k < n)
                {
                    if (p[k] == SessionPhase.Match) lastMatch = k;
                    else if (p[k] == SessionPhase.Loading)
                    {
                        // Caricamento breve seguito dalla stessa partita: si salta.
                        int j = k;
                        while (j < n && p[j] == SessionPhase.Loading) j++;
                        int next = j;
                        while (next < n && p[next] == SessionPhase.Unfocused) next++;
                        if (j - k > MaxHitchLoadingSec || next >= n || p[next] != SessionPhase.Match ||
                            (seconds != null && ServerChanged(seconds, p, lastMatch, next)))
                            break;
                        k = next;
                        continue;
                    }
                    else if (p[k] != SessionPhase.Unfocused) break;
                    k++;
                }
                int end = lastMatch + 1;
                int before = start - 1;
                while (before >= 0 && p[before] == SessionPhase.Unfocused) before--;
                var seg = new MatchSegment
                {
                    StartSec = start,
                    EndSec = end,
                    DurationSec = Enumerable.Range(start, end - start).Count(x => p[x] == SessionPhase.Match),
                    Joined = before >= 0 && (p[before] == SessionPhase.Lobby || p[before] == SessionPhase.Loading)
                };
                if (seconds != null && servers != null && servers.Count > 0)
                {
                    var counts = new Dictionary<int, int>();
                    for (int s = start; s < end && s < seconds.Count; s++)
                        if (p[s] == SessionPhase.Match && seconds[s]?.ServerIdx is { } si && si >= 0 && si < servers.Count)
                            counts[si] = counts.TryGetValue(si, out var c) ? c + 1 : 1;
                    if (counts.Count > 0) seg.Server = servers[counts.OrderByDescending(kv => kv.Value).First().Key];
                }
                list.Add(seg);
                i = end;
            }
            return list;
        }

        /// <summary>Durate delle fasi (secondi) e lobby inattiva a ~30 FPS.</summary>
        public static PhaseDurations Durations(IReadOnlyList<SessionPhase> p, IReadOnlyList<SecondSample>? seconds, bool fromNetwork)
        {
            var d = new PhaseDurations { FromNetwork = fromNetwork };
            for (int k = 0; k < p.Count; k++)
            {
                switch (p[k])
                {
                    case SessionPhase.Lobby: d.LobbySec++; break;
                    case SessionPhase.Loading: d.LoadingSec++; break;
                    case SessionPhase.Match: d.MatchSec++; break;
                    default: d.UnfocusedSec++; break;
                }
            }
            if (seconds != null)
            {
                var idle = new bool[p.Count];
                for (int k = 0; k < p.Count && k < seconds.Count; k++)
                    idle[k] = p[k] == SessionPhase.Lobby && seconds[k] != null && IsIdle(seconds[k]);
                ForRuns(idle, true, (a, b) =>
                {
                    if (b - a >= MinSegmentSec) d.LobbyIdleSec += b - a;
                });
            }
            return d;
        }

        // ================= Frame → secondo =================

        /// <summary>
        /// Istante di fine di ogni frame (ms dall'inizio del primo frame) ricostruito sommando i frametime e le pause salvate
        /// (<see cref="PerfSession.FrameGaps"/>). Senza pause è la stessa ricostruzione del CSV dei frametime.
        /// </summary>
        public static double[] FrameEndTimes(IReadOnlyList<float> ft, IReadOnlyList<FrameGap>? gaps = null)
        {
            var end = new double[ft?.Count ?? 0];
            if (ft == null) return end;
            Dictionary<int, double>? g = null;
            if (gaps is { Count: > 0 })
            {
                g = new Dictionary<int, double>();
                foreach (var x in gaps)
                    if (x != null && x.Index >= 0 && double.IsFinite(x.Ms) && x.Ms > 0)
                        g[x.Index] = (g.TryGetValue(x.Index, out var v) ? v : 0) + x.Ms;
            }
            double t = 0;
            for (int i = 0; i < ft.Count; i++)
            {
                if (g != null && g.TryGetValue(i, out var pause)) t += pause;
                if (FrameStats.IsValid(ft[i])) t += ft[i];
                end[i] = t;
            }
            return end;
        }

        /// <summary>
        /// Secondo della sessione di ogni frame: un frame appartiene al secondo in cui termina, (k·1000, (k+1)·1000] → k
        /// (stessa regola di FocusFilter.BuildSession). endMs = fine del frame, baseMs = inizio del primo frame.
        /// </summary>
        public static int[] FrameSeconds(IReadOnlyList<double> endMs, double baseMs = 0)
        {
            var sec = new int[endMs.Count];
            for (int i = 0; i < sec.Length; i++)
            {
                double e = endMs[i] - baseMs;
                int k = double.IsFinite(e) ? (int)Math.Ceiling(e / 1000.0) - 1 : -1;
                sec[i] = Math.Max(0, k);
            }
            return sec;
        }

        /// <summary>Pause tra frame consecutivi oltre la durata del frame (istanti ETW di fine frame): vedi <see cref="PerfSession.FrameGaps"/>.</summary>
        public static List<FrameGap> FindGaps(IReadOnlyList<float> ft, IReadOnlyList<double> ts, double minMs = 50, int max = 10000)
        {
            var list = new List<FrameGap>();
            int n = Math.Min(ft?.Count ?? 0, ts?.Count ?? 0);
            for (int i = 1; i < n && list.Count < max; i++)
            {
                double f = FrameStats.IsValid(ft![i]) ? ft[i] : 0;
                double gap = ts![i] - ts[i - 1] - f;
                if (gap > minMs) list.Add(new FrameGap { Index = i, Ms = Math.Round(gap, 1) });
            }
            return list;
        }

        // ================= Applicazione a una sessione =================

        /// <summary>
        /// Calcola e scrive nella sessione fasi, partite, durate e (con i frametime) statistiche della sola partita e scatti.
        /// excluded = frame fuori fuoco (null = dalla sessione); frameSecond = secondo di ogni frame (null = ricostruito
        /// dai frametime e dalle pause salvate).
        /// </summary>
        public static void Apply(PerfSession session, IReadOnlyList<float>? ft, IReadOnlyList<bool>? excluded = null,
            IReadOnlyList<int>? frameSecond = null, double stutterFactor = 2.5, double stutterMinMs = 12)
        {
            if (session == null) return;
            var secs = session.Seconds ?? new List<SecondSample>();
            var phases = Classify(secs, out bool fromNet);
            session.Phases = Segments(phases);
            session.PhaseSeconds = Durations(phases, secs, fromNet);
            session.Matches = FindMatches(phases, secs, session.Network?.ServerEndpoints);
            session.PhaseVersion = Version;
            if (ft == null || ft.Count < 2) return;

            double[] endMs = FrameEndTimes(ft, session.FrameGaps);
            if (frameSecond == null && session.FrameGaps == null && secs.Count > 0 && !Aligned(endMs, secs.Count))
            {
                // Sessione vecchia senza le pause salvate: la cattura scarta le pause oltre 5 s (gioco ridotto a icona),
                // quindi sommando i frametime ogni frame dopo una pausa finirebbe nel secondo sbagliato. Si ricostruiscono
                // le pause dai secondi senza frame; se i conti non tornano, niente statistiche "solo partita" (meglio i numeri
                // della sessione intera che una partita disallineata con dentro i caricamenti).
                var rebuilt = RebuildGaps(ft, secs);
                var endRebuilt = rebuilt != null ? FrameEndTimes(ft, rebuilt) : null;
                if (endRebuilt == null || !Aligned(endRebuilt, secs.Count))
                {
                    session.MatchStats = null;
                    session.Hitches = null;
                    return;
                }
                session.FrameGaps = rebuilt;
                endMs = endRebuilt;
            }
            frameSecond ??= FrameSeconds(endMs);
            var mask = MatchMask(phases, ft, excluded ?? FocusFilter.MaskFromRanges(ft.Count, session.ExcludedRanges), frameSecond);
            var matchFt = new List<float>(ft.Count);
            var matchIdx = new List<int>(ft.Count);
            for (int i = 0; i < mask.Length; i++)
            {
                if (!mask[i]) continue;
                matchFt.Add(ft[i]);
                matchIdx.Add(i);
            }
            session.MatchStats = FrameStats.Compute(matchFt, stutterFactor, stutterMinMs);
            session.Hitches = matchFt.Count >= 2
                ? BuildHitches(matchFt, matchIdx, endMs, frameSecond, session.Matches, secs, session.MatchStats)
                : null;
        }

        /// <summary>La fine dell'ultimo frame cade entro 2 s dalla fine della sessione (frame e secondi allineati).</summary>
        private static bool Aligned(double[] endMs, int seconds) =>
            endMs.Length > 0 && Math.Abs(endMs[^1] / 1000.0 - seconds) <= MaxTimelineDriftSec;

        /// <summary>
        /// Pause della cattura ricostruite dai secondi senza alcun frame (almeno 4 di fila, non fuori fuoco): il primo frame
        /// che finirebbe dentro un tratto vuoto ricomincia all'inizio del secondo successivo al tratto, a meno che sia lui
        /// stesso a coprirlo (un frame lunghissimo ma valido). Null se non c'è nessun tratto vuoto.
        /// </summary>
        private static List<FrameGap>? RebuildGaps(IReadOnlyList<float> ft, IReadOnlyList<SecondSample> secs)
        {
            var runs = new List<(int A, int B)>();
            int i = 0;
            while (i < secs.Count)
            {
                if (!Empty(secs[i]))
                {
                    i++;
                    continue;
                }
                int a = i;
                while (i < secs.Count && Empty(secs[i])) i++;
                if (i - a >= MinPauseEmptySec) runs.Add((a, i));
            }
            if (runs.Count == 0) return null;

            var gaps = new List<FrameGap>();
            double t = 0;
            int r = 0;
            for (int k = 0; k < ft.Count && r < runs.Count; k++)
            {
                double f = FrameStats.IsValid(ft[k]) ? ft[k] : 0;
                while (r < runs.Count && t + f > runs[r].A * 1000.0)
                {
                    double restart = runs[r].B * 1000.0;
                    double add = Math.Round(restart - t, 1);
                    if (t + f <= restart && add > 0)
                    {
                        gaps.Add(new FrameGap { Index = k, Ms = add });
                        t += add;
                    }
                    r++;
                }
                t += f;
            }
            return gaps.Count > 0 ? gaps : null;

            static bool Empty(SecondSample? x) => x != null && !x.Unfocused && x.Fps <= 0 && x.MaxFrametimeMs <= 0;
        }

        /// <summary>Per ogni frame, true se è un frame della partita in primo piano (quelli delle statistiche "solo partita").</summary>
        private static bool[] MatchMask(SessionPhase[] phases, IReadOnlyList<float> ft, IReadOnlyList<bool>? excluded, IReadOnlyList<int> frameSecond)
        {
            var mask = new bool[ft.Count];
            for (int i = 0; i < ft.Count && i < frameSecond.Count; i++)
            {
                if (excluded != null && i < excluded.Count && excluded[i]) continue;
                int s = frameSecond[i];
                mask[i] = s >= 0 && s < phases.Length && phases[s] == SessionPhase.Match && FrameStats.IsValid(ft[i]);
            }
            return mask;
        }

        /// <summary>
        /// Frame della partita (in primo piano) di una sessione salvata: per l'istogramma e la colonna "match" del CSV.
        /// Null se la sessione non ha fasi o la partita è troppo breve per i numeri "solo partita".
        /// </summary>
        public static bool[]? MatchMask(PerfSession? session, IReadOnlyList<float>? ft)
        {
            if (session == null || ft == null || ft.Count < 2 || !session.HeadlineIsMatch) return null;
            var phases = PhasesOf(session);
            var frameSecond = FrameSeconds(FrameEndTimes(ft, session.FrameGaps));
            return MatchMask(phases, ft, FocusFilter.MaskFromRanges(ft.Count, session.ExcludedRanges), frameSecond);
        }

        private static HitchSummary BuildHitches(List<float> matchFt, List<int> matchIdx, double[] endMs, IReadOnlyList<int> frameSecond,
            List<MatchSegment> matches, List<SecondSample> secs, FrameStatsResult matchStats)
        {
            double median = matchStats.MedianFrametimeMs;
            var h = new HitchSummary
            {
                ThresholdMs = Math.Max(HitchMs, 2 * median),
                BigThresholdMs = Math.Max(BigHitchMs, 2 * median),
                MatchMinutes = matchStats.DurationSec / 60.0,
                Low1Fps = matchStats.Low1Fps,
                Low01Fps = matchStats.Low01Fps
            };
            var spikes = new List<int>(); // posizioni in matchFt
            var withoutSmall = new List<float>(matchFt.Count);
            var withoutBig = new List<float>(matchFt.Count);
            for (int k = 0; k < matchFt.Count; k++)
            {
                float f = matchFt[k];
                if (f >= h.ThresholdMs)
                {
                    spikes.Add(k);
                    h.Count++;
                    if (f >= HugeHitchMs) h.CountHuge++;
                    int s = frameSecond[matchIdx[k]];
                    if (h.SpikeSeconds.Count < MaxSpikeSeconds) h.SpikeSeconds.Add(s);
                }
                else withoutSmall.Add(f);
                if (f >= h.BigThresholdMs) h.CountBig++;
                else withoutBig.Add(f);
            }
            h.PerMin = h.MatchMinutes > 0 ? h.Count / h.MatchMinutes : 0;
            h.Low1WithoutFps = withoutSmall.Count > 0 ? FrameStats.LowFps(withoutSmall, 0.01) : 0;
            h.Low01WithoutFps = withoutSmall.Count > 0 ? FrameStats.LowFps(withoutSmall, 0.001) : 0;
            h.Low1WithoutBigFps = withoutBig.Count > 0 ? FrameStats.LowFps(withoutBig, 0.01) : 0;
            h.Low01WithoutBigFps = withoutBig.Count > 0 ? FrameStats.LowFps(withoutBig, 0.001) : 0;

            // I più lunghi (a parità, i primi), poi in ordine di tempo.
            var top = spikes.OrderByDescending(k => matchFt[k]).ThenBy(k => k).Take(MaxHitchList).OrderBy(k => k);
            foreach (int k in top)
            {
                int i = matchIdx[k];
                int s = frameSecond[i];
                int mi = matches.FindIndex(x => s >= x.StartSec && s < x.EndSec);
                var x = s >= 0 && s < secs.Count ? secs[s] : null;
                bool freeze = false;
                for (int j = Math.Max(0, s - 1); j <= Math.Min(secs.Count - 1, s + 1); j++)
                    if (secs[j]?.NetFreezes > 0) freeze = true;
                h.Top.Add(new HitchInfo
                {
                    Frame = i,
                    SessionSec = Math.Round(endMs[i] / 1000.0, 2),
                    MatchSec = mi >= 0 ? Math.Round(Math.Max(0, endMs[i] / 1000.0 - matches[mi].StartSec), 2) : 0,
                    Match = mi + 1,
                    Ms = Math.Round(matchFt[k], 1),
                    OtherAppsKbps = x?.OtherAppsKbps,
                    PingMs = x?.PingMs,
                    NetFreezeNear = freeze,
                    CpuPercent = x?.CpuPercent ?? 0,
                    GpuPercent = x?.GpuPercent,
                    TopDownloader = x?.TopDownloaders is { Count: > 0 } td ? td[0].Name : null
                });
            }
            return h;
        }

        private static readonly JsonSerializerOptions CloneOpts = new()
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// Sessione senza fasi aggiornate (registrata prima di questo calcolo, o corretta da FocusFilter.RepairLegacy),
        /// oppure con le fasi ma senza le statistiche della partita che ora si possono calcolare perché ci sono i frametime:
        /// restituisce una COPIA con fasi, partite e (con i frametime) statistiche della sola partita e scatti.
        /// Null se non serve nulla (la sessione salvata non viene mai modificata).
        /// </summary>
        public static PerfSession? Ensure(PerfSession? session, IReadOnlyList<float>? frametimes,
            double stutterFactor = 2.5, double stutterMinMs = 12)
        {
            if (session == null) return null;
            bool hasFt = frametimes != null && frametimes.Count >= 2;
            bool current = session.PhaseVersion >= Version && session.Phases != null;
            bool needStats = hasFt && session.MatchStats == null && (session.PhaseSeconds?.MatchSec ?? 0) > 0;
            if (current && !needStats) return null;
            var copy = JsonSerializer.Deserialize<PerfSession>(JsonSerializer.SerializeToUtf8Bytes(session, CloneOpts), CloneOpts)!;
            Apply(copy, hasFt ? frametimes : null, null, null, stutterFactor, stutterMinMs);
            return copy;
        }

        // ================= Domande sulle fasi =================

        /// <summary>Fase di ogni secondo della sessione (dai tratti salvati, o ricalcolata se mancano).</summary>
        public static SessionPhase[] PhasesOf(PerfSession session)
        {
            int n = session?.Seconds?.Count ?? 0;
            if (session == null) return Array.Empty<SessionPhase>();
            if (session.Phases is { Count: > 0 }) return FromSegments(session.Phases, n);
            return Classify(session.Seconds, out _);
        }

        /// <summary>
        /// Freeze di rete durante le partite, esclusi i primi <see cref="JoinFreezeGraceSec"/> secondi dopo ogni ingresso
        /// (lì sono normali: il server sta ancora caricando). Per ognuno: secondo della sessione, numero della partita,
        /// secondi dall'inizio della partita e pausa più lunga misurata (ms, se nota).
        /// </summary>
        public static List<(int Sec, int Match, int MatchSec, double? GapMs)> MidMatchFreezes(PerfSession session, out int joinFreezes)
        {
            joinFreezes = 0;
            var list = new List<(int, int, int, double?)>();
            var secs = session?.Seconds;
            if (session == null || secs == null) return list;
            var p = PhasesOf(session);
            var matches = session.Matches ?? FindMatches(p, secs, session.Network?.ServerEndpoints);
            for (int s = 0; s < secs.Count && s < p.Length; s++)
            {
                if (secs[s] == null || secs[s].NetFreezes <= 0) continue;
                int mi = matches.FindIndex(x => s >= x.StartSec && s < x.EndSec);
                // Freeze proprio nel caricamento d'ingresso (o subito prima della partita): anche questi sono normali.
                if (mi < 0)
                {
                    int next = matches.FindIndex(x => x.Joined && x.StartSec > s && x.StartSec - s <= JoinFreezeGraceSec);
                    if (next >= 0 && p[s] != SessionPhase.Lobby) joinFreezes += secs[s].NetFreezes;
                    continue;
                }
                var m = matches[mi];
                if (m.Joined && s - m.StartSec < JoinFreezeGraceSec)
                {
                    joinFreezes += secs[s].NetFreezes;
                    continue;
                }
                double? gap = null;
                for (int j = s; j <= Math.Min(secs.Count - 1, s + 1); j++)
                    if (secs[j]?.MaxRecvGapMs is { } g && g >= FreezeDetector.DefaultThresholdMs) gap = Math.Max(gap ?? 0, g);
                list.Add((s, mi + 1, s - m.StartSec, gap));
            }
            return list;
        }

        /// <summary>"7:22" o "1:02:03".</summary>
        public static string Clock(double sec)
        {
            if (!double.IsFinite(sec) || sec < 0) sec = 0;
            var t = TimeSpan.FromSeconds(Math.Floor(sec));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        }
    }
}
