using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FNBoost.Core;
using FNBoost.Perf;

// Questo file non dipende da WPF né da API di Windows: viene compilato anche dal progetto di test su Linux.

namespace FNBoost.Report
{
    /// <summary>
    /// Trasforma <see cref="ReportData"/> nei file del report: JSON (per strumenti e assistenti), HTML
    /// autosufficiente (per le persone) e un riassunto di testo da incollare in chat.
    /// Contiene anche le regole che costruiscono la sezione "Cosa non va / Cosa migliorare".
    /// </summary>
    public static class ReportBuilder
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public const int SummaryMaxChars = 4000;

        // ================= JSON =================

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            // I valori null ("non misurato") vengono omessi: il file resta compatto anche con ore di campioni.
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Il JSON è un file a sé (non incluso in HTML): lettere accentate leggibili.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private static readonly JsonSerializerOptions CloneOpts = new()
        {
            Converters = { new JsonStringEnumConverter() },
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        public static string BuildJson(ReportData data) => JsonSerializer.Serialize(data, JsonOpts);

        // ================= Privacy =================

        /// <summary>
        /// Ripulisce tutti i testi liberi del report (etichette, note, messaggi, host dei ping, righe del log).
        /// Sessione e rete vengono prima copiate: gli oggetti dell'archivio non vengono modificati.
        /// </summary>
        public static void SanitizeInPlace(ReportData d, SanitizeContext ctx)
        {
            if (d == null) return;
            ctx ??= new SanitizeContext();
            // I server di gioco sono ammessi per definizione.
            var servers = new List<string?>();
            if (d.Session?.Network?.ServerEndpoints != null) servers.AddRange(d.Session.Network.ServerEndpoints);
            if (d.LiveNetwork?.ServerEndpoint != null) servers.Add(d.LiveNetwork.ServerEndpoint);
            if (d.Log?.ServerAddresses != null) servers.AddRange(d.Log.ServerAddresses);
            var c = ctx.With(servers);
            string S(string? s) => Sanitizer.Sanitize(s, c);
            string I(string? s) => Sanitizer.ScrubIdentity(s, c);

            d.Notes = d.Notes.Select(S).ToList();
            if (d.System is { } sys)
            {
                sys.Os = I(sys.Os);
                sys.Cpu = I(sys.Cpu);
                sys.Board = I(sys.Board);
                sys.BiosVersion = I(sys.BiosVersion);
                sys.PageFile = S(sys.PageFile);
                sys.PowerPlan = I(sys.PowerPlan);
                foreach (var g in sys.Gpus) { g.Name = I(g.Name); g.Vendor = I(g.Vendor); }
            }
            foreach (var ch in d.Checks) { ch.Title = S(ch.Title); ch.Message = S(ch.Message); ch.Hint = S(ch.Hint); }
            foreach (var t in d.Tweaks) { t.Title = I(t.Title); t.Category = I(t.Category); }

            if (d.Session != null)
            {
                var s = Clone(d.Session)!;
                s.Label = S(s.Label);
                s.Notes = S(s.Notes);
                s.ProcessName = I(s.ProcessName);
                s.ActiveTweaks = (s.ActiveTweaks ?? new List<string>()).Select(I).ToList();
                if (s.Network is { } n)
                {
                    n.ServerEndpoints = (n.ServerEndpoints ?? new List<string>()).Select(S).ToList();
                    n.RegionName = n.RegionName == null ? null : S(n.RegionName);
                    n.ConnectionType = S(n.ConnectionType);
                    n.BestRegionName = n.BestRegionName == null ? null : S(n.BestRegionName);
                    SanitizePing(n.Game, c);
                    SanitizePing(n.Region, c);
                    SanitizePing(n.Gateway, c);
                    SanitizePing(n.Internet, c);
                }
                foreach (var p in s.TopProcesses ?? new List<ProcessUsage>()) p.Name = S(p.Name);
                d.Session = s;
            }
            if (d.LiveNetwork != null)
            {
                var n = Clone(d.LiveNetwork)!;
                n.StatusText = S(n.StatusText);
                n.ServerEndpoint = n.ServerEndpoint == null ? null : S(n.ServerEndpoint);
                n.RegionName = n.RegionName == null ? null : S(n.RegionName);
                n.ConnectionType = S(n.ConnectionType);
                n.AdapterName = S(n.AdapterName);
                n.BestRegionName = n.BestRegionName == null ? null : S(n.BestRegionName);
                SanitizePing(n.Game, c);
                SanitizePing(n.Region, c);
                SanitizePing(n.Gateway, c);
                SanitizePing(n.Internet, c);
                d.LiveNetwork = n;
            }
            d.Insights = d.Insights.Select(x => SanitizeInsight(x, c)).ToList();
            d.Trend = d.Trend.Select(x => SanitizeInsight(x, c)).ToList();
            foreach (var p in d.PreviousSessions) p.Label = S(p.Label);
            d.Fortnite.GameBuild = d.Fortnite.GameBuild == null ? null : S(d.Fortnite.GameBuild);
            d.Fortnite.RhiInUse = d.Fortnite.RhiInUse == null ? null : S(d.Fortnite.RhiInUse);
            d.Verdict = d.Verdict == null ? null : S(d.Verdict);
            if (d.Log is { } log)
            {
                log.Note = log.Note == null ? null : S(log.Note);
                log.GameBuild = log.GameBuild == null ? null : S(log.GameBuild);
                log.RhiInUse = log.RhiInUse == null ? null : S(log.RhiInUse);
                log.GpuInfo = log.GpuInfo.Select(S).ToList();
                log.Sources = log.Sources.Select(S).ToList();
                foreach (var h in log.Highlights) h.Text = S(h.Text);
            }
            foreach (var r in d.Recommendations)
            {
                r.Title = S(r.Title);
                r.Problem = S(r.Problem);
                r.WhyItMatters = S(r.WhyItMatters);
                r.WhatToDo = S(r.WhatToDo);
            }
        }

        private static void SanitizePing(PingStats? p, SanitizeContext c)
        {
            if (p == null) return;
            p.Host = Sanitizer.Sanitize(p.Host, c);
            p.Target = Sanitizer.Sanitize(p.Target, c);
        }

        private static PerfInsight SanitizeInsight(PerfInsight x, SanitizeContext c) => new()
        {
            Severity = x.Severity,
            Title = Sanitizer.Sanitize(x.Title, c),
            Message = Sanitizer.Sanitize(x.Message, c),
            Hint = Sanitizer.Sanitize(x.Hint, c)
        };

        private static T? Clone<T>(T? value) where T : class =>
            value == null ? null : JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, CloneOpts), CloneOpts);

        // ================= Frametime =================

        /// <summary>Istogramma con ~40 colonne fino a 1,5 × il 99,5° percentile; l'ultima raccoglie i frame più lunghi.</summary>
        public static FrametimeHistogram? BuildHistogram(IReadOnlyList<float>? frametimes)
        {
            if (frametimes == null) return null;
            var valid = frametimes.Where(FrameStats.IsValid).Select(v => (double)v).ToArray();
            if (valid.Length < 2) return null;
            Array.Sort(valid);
            double Pct(double p) => valid[Math.Min(valid.Length - 1, (int)Math.Floor(p * (valid.Length - 1)))];
            double median = Pct(0.5);
            double upper = Math.Max(Pct(0.995) * 1.5, median * 2);
            double width = NiceStep(upper / 40);
            int bins = Math.Max(1, (int)Math.Ceiling(upper / width));
            var h = new FrametimeHistogram { Total = valid.Length, MedianMs = median, P99Ms = Pct(0.99) };
            for (int i = 0; i <= bins; i++) h.EdgesMs.Add(Math.Round(i * width, 6));
            var counts = new int[bins];
            foreach (var v in valid) counts[Math.Min(bins - 1, (int)(v / width))]++;
            h.Counts.AddRange(counts);
            return h;
        }

        /// <summary>
        /// CSV dei frametime: index,time_ms,frametime_ms,fps,focused (cultura invariante, time_ms = fine del frame).
        /// Si esportano TUTTI i frame; focused = 0 indica quelli esclusi dalle statistiche perché il gioco era fuori fuoco
        /// (excluded[i] = true), così chi analizza il file può riprodurre esattamente i numeri del report.
        /// </summary>
        public static void WriteFrametimesCsv(TextWriter w, IReadOnlyList<float> frametimes, IReadOnlyList<bool>? excluded = null)
        {
            w.Write("index,time_ms,frametime_ms,fps,focused\n");
            double t = 0;
            for (int i = 0; i < frametimes.Count; i++)
            {
                var ft = frametimes[i];
                bool ok = FrameStats.IsValid(ft);
                if (ok) t += ft;
                w.Write(i.ToString(Inv));
                w.Write(',');
                w.Write(t.ToString("0.###", Inv));
                w.Write(',');
                w.Write(ok ? ft.ToString("0.###", Inv) : "");
                w.Write(',');
                w.Write(ok && ft > 0 ? (1000.0 / ft).ToString("0.##", Inv) : "");
                w.Write(excluded != null && i < excluded.Count && excluded[i] ? ",0\n" : ",1\n");
            }
        }

        // ================= Raccomandazioni =================

        private static readonly Dictionary<string, string> WhyByTitle = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Frequenza del monitor"] = "Se il monitor lavora sotto la sua frequenza massima vedi meno immagini al secondo di quelle che il PC produce: il gioco sembra meno fluido e la latenza percepita aumenta, qualunque siano gli FPS.",
            ["Velocità RAM (XMP)"] = "Quando Fortnite non è limitato dalla GPU dipende molto dalla velocità della memoria: la RAM alla velocità base abbassa soprattutto l'1% low e la regolarità dei frametime.",
            ["File di paging"] = "Senza un file di paging adeguato la memoria impegnata può esaurirsi: il gioco o il driver video possono chiudersi all'improvviso.",
            ["Memoria impegnata"] = "Vicino al limite della memoria impegnata Windows deve spostare dati su disco: compaiono scatti e, nei casi peggiori, crash.",
            ["Secure Boot / TPM"] = "Non cambia gli FPS, ma Epic li richiede per i tornei: senza potresti non poter partecipare.",
            ["BIOS / microcode Intel"] = "Le CPU Intel di 13ª/14ª generazione con microcode vecchio possono diventare instabili: crash e stutter casuali che nessuna impostazione grafica risolve.",
            ["Driver scheda video"] = "I driver recenti includono correzioni e ottimizzazioni per Fortnite e per la cache degli shader.",
            ["Disco di Fortnite"] = "Il gioco carica di continuo texture e dati dal disco: un HDD è troppo lento e provoca scatti e texture che compaiono in ritardo.",
            ["Spazio libero"] = "Aggiornamenti e cache degli shader hanno bisogno di spazio: con il disco quasi pieno gli aggiornamenti falliscono e la cache non si salva.",
            ["Frame molto irregolari"] = "L'1% low misura i momenti peggiori ed è ciò che percepisci come scatti, più della media degli FPS.",
            ["Fluidità altalenante"] = "L'1% low misura i momenti peggiori ed è ciò che percepisci come scatti, più della media degli FPS.",
            ["Molti stutter"] = "Ogni stutter è un frame molto più lungo dei vicini: anche con FPS medi alti si sente come un piccolo blocco, e in uno scontro può costare la mira.",
            ["Qualche stutter"] = "Ogni stutter è un frame molto più lungo dei vicini: anche con FPS medi alti si sente come un piccolo blocco.",
            ["FPS sotto il refresh del monitor"] = "Sotto il refresh il monitor ripete alcune immagini: movimento meno fluido e latenza più alta.",
            ["Limite GPU"] = "Sapere chi limita gli FPS dice quali impostazioni contano: con la GPU al limite aiutano risoluzione 3D e qualità grafica.",
            ["Probabile limite CPU"] = "Con la CPU al limite abbassare la grafica serve poco: contano modalità di rendering, memoria (XMP) e programmi in background.",
            ["Memoria video quasi piena"] = "Con la VRAM piena il gioco sposta texture nella RAM di sistema, molto più lenta: scatti e texture sfocate.",
            ["RAM quasi piena"] = "Con la RAM piena Windows usa il disco come memoria: scatti lunghi e caricamenti lenti.",
            ["Attività in background durante gli stutter"] = "Un programma che usa la CPU a ondate ruba tempo al gioco proprio quando compaiono gli scatti.",
            ["Peggio della sessione precedente"] = "Un peggioramento indica che qualcosa è cambiato (patch, driver, programmi o impostazioni): conviene capire cosa.",
            ["Prestazioni in calo"] = "Un calo costante nel tempo indica che qualcosa è cambiato (patch, driver, programmi o impostazioni): conviene capire cosa."
        };

        private static readonly (string Needle, int Impact)[] ImpactRules =
        {
            ("frequenza del monitor", 3), ("xmp", 3), ("disco di fortnite", 3), ("microcode", 3), ("crash", 3),
            ("frame molto irregolari", 3), ("molti stutter", 3), ("perdita di pacchetti", 3), ("rete di casa", 3),
            ("freeze di rete", 3), ("file di paging", 2), ("fps sotto il refresh", 2), ("limite gpu", 2), ("limite cpu", 2),
            ("stutter", 2), ("driver", 2), ("memoria video", 2), ("ram quasi piena", 2), ("memoria", 2), ("ping", 2),
            ("jitter", 2), ("background", 2), ("hitch", 2), ("fluidità", 2), ("in calo", 2), ("altre app", 2)
        };

        private static readonly string[] SourceOrder = { "Sistema", "Sessione", "Rete", "Log di Fortnite", "Andamento" };

        /// <summary>
        /// "Cosa non va / Cosa migliorare": controlli e analisi con esito Attenzione/Problema, rete e log di Fortnite.
        /// Elimina i doppioni (titoli simili) e ordina per gravità, poi per impatto stimato.
        /// </summary>
        public static List<Recommendation> BuildRecommendations(ReportData d)
        {
            var list = new List<Recommendation>();
            if (d == null) return list;
            // Effetto collaterale voluto: il giudizio sulla sessione decide il tono dei consigli.
            (d.PerformsWell, d.Verdict) = ComputeVerdict(d);

            foreach (var c in d.Checks.Where(c => c.Status >= CheckStatus.Warn))
                list.Add(Rec(c.Status, c.Title, c.Message, c.Hint, "Sistema"));
            foreach (var i in d.Insights.Where(i => i.Severity >= CheckStatus.Warn))
                list.Add(Rec(i.Severity, i.Title, i.Message, i.Hint, "Sessione"));
            AddNetwork(list, d);
            AddLog(list, d.Log);
            foreach (var i in d.Trend.Where(i => i.Severity >= CheckStatus.Warn))
                list.Add(Rec(i.Severity, i.Title, i.Message, i.Hint, "Andamento"));

            // Doppioni: si tiene il più grave (a parità, quello con impatto maggiore, poi il primo).
            var result = new List<Recommendation>();
            foreach (var r in list)
            {
                var dup = result.FirstOrDefault(x => SimilarTitles(x.Title, r.Title));
                if (dup == null)
                {
                    result.Add(r);
                    continue;
                }
                // Stesso controllo ripetuto (es. due monitor): si uniscono i dettagli.
                if (string.Equals(dup.Title, r.Title, StringComparison.OrdinalIgnoreCase) && dup.Source == r.Source &&
                    !dup.Problem.Contains(r.Problem, StringComparison.Ordinal))
                {
                    dup.Problem = (dup.Problem + " " + r.Problem).Trim();
                    if (r.Severity > dup.Severity) dup.Severity = r.Severity;
                    dup.Impact = Math.Max(dup.Impact, r.Impact);
                    continue;
                }
                if (r.Severity > dup.Severity || (r.Severity == dup.Severity && r.Impact > dup.Impact))
                {
                    r.Impact = Math.Max(r.Impact, dup.Impact);
                    result[result.IndexOf(dup)] = r;
                }
                else dup.Impact = Math.Max(dup.Impact, r.Impact);
            }

            // Sessione buona: i consigli su prestazioni e sistema diventano facoltativi. Restano come sono i problemi di rete
            // (non dipendono dal PC), i crash e la memoria del log, e i controlli di sistema gravi.
            if (d.PerformsWell)
            {
                foreach (var r in result)
                {
                    bool keep = r.Source == "Rete" ||
                                (r.Source == "Sistema" && r.Severity == CheckStatus.Bad) ||
                                (r.Source == "Log di Fortnite" && (r.Title.Contains("rash", StringComparison.Ordinal) || r.Title.Contains("memoria", StringComparison.OrdinalIgnoreCase) ||
                                                                   r.Title.Contains("connessione", StringComparison.OrdinalIgnoreCase)));
                    if (keep) continue;
                    r.Optional = true;
                    r.Severity = CheckStatus.Info;
                    r.Impact = 1;
                }
            }

            return result
                .Select((r, idx) => (r, idx))
                .OrderByDescending(x => (int)x.r.Severity)
                .ThenByDescending(x => x.r.Impact)
                .ThenBy(x => SourceRank(x.r.Source))
                .ThenBy(x => x.idx)
                .Select(x => x.r)
                .Take(25)
                .ToList();
        }

        /// <summary>
        /// Sessione buona = 1% low ≥ 60% della media, al massimo 2 stutter al minuto e media ≥ 95% del limite FPS
        /// (o del refresh del monitor se non c'è un limite). Restituisce anche la frase di giudizio (null senza sessione).
        /// Sessione vecchia con tratto in secondo piano non correggibile (frametime mancanti): nessun giudizio sui numeri,
        /// che sono falsati da quel tratto (stessa regola di PerfAnalyzer).
        /// </summary>
        public static (bool Good, string? Verdict) ComputeVerdict(ReportData d)
        {
            var s = d?.Session;
            var st = s?.Stats;
            if (s == null || st == null || !st.HasData || !(st.AvgFps > 0)) return (false, null);
            double target = s.FpsCap is > 0 ? s.FpsCap.Value : s.RefreshHz is > 1 ? s.RefreshHz.Value : 0;
            string game = PerfAnalyzer.GameRef(s.ProcessName);
            if (!s.FocusTracked && !s.UnfocusedEstimated && FocusFilter.DetectBackground(s.Seconds) is { } bg)
            {
                string core = target > 0 ? $" ({F0(bg.CoreMedianFps / target * 100)}% {(s.FpsCap is > 0 ? "del limite di " + F0(target) : "dei " + F0(target) + " Hz del monitor")})" : "";
                return (false, $"Nessun giudizio sugli FPS: all'inizio o alla fine la sessione è a ~30 FPS con la GPU quasi ferma (probabile gioco in secondo piano) " +
                               $"e 1% low, stutter e regolarità sono falsati da quel tratto. Nel resto della sessione {game} va a circa {F0(bg.CoreMedianFps)} FPS{core}. " +
                               "I frametime per ricalcolare non sono disponibili: registra una nuova sessione per un giudizio affidabile.");
            }
            double ratio = st.Low1Fps / st.AvgFps;
            bool reachesTarget = target <= 0 || st.AvgFps >= 0.95 * target;
            bool good = ratio >= 0.6 && st.StuttersPerMin <= 2 && reachesTarget;
            string targetText = target <= 0 ? "" :
                s.FpsCap is > 0 ? $" ({F0(st.AvgFps / target * 100)}% del limite di {F0(target)})" : $" ({F0(st.AvgFps / target * 100)}% dei {F0(target)} Hz del monitor)";
            string numbers = $"media {F0(st.AvgFps)} FPS{targetText}, 1% low {F0(st.Low1Fps)} FPS ({F0(ratio * 100)}% della media), {F1(st.StuttersPerMin)} stutter al minuto";
            string focus = s.UnfocusedSec >= 1 ? $" (esclusi {F0(s.UnfocusedSec)} s con il gioco fuori fuoco)" : "";
            if (good)
                return (true, $"Il PC fa girare bene {game}: {numbers}{focus}. Non c'è nulla da correggere per gli FPS: " +
                              "i punti qui sotto sono miglioramenti facoltativi (o riguardano la rete, che non dipende dal PC).");
            var why = new List<string>();
            if (ratio < 0.6) why.Add("i frame più lenti sono lontani dalla media");
            if (st.StuttersPerMin > 2) why.Add("ci sono parecchi stutter");
            if (!reachesTarget) why.Add(s.FpsCap is > 0 ? "la media resta sotto il limite FPS impostato" : "la media resta sotto il refresh del monitor");
            return (false, $"Prestazioni da migliorare: {numbers}{focus}; {string.Join(", ", why)}. I punti qui sotto sono in ordine di priorità.");
        }

        private static int SourceRank(string source)
        {
            int i = Array.IndexOf(SourceOrder, source);
            return i < 0 ? SourceOrder.Length : i;
        }

        private static Recommendation Rec(CheckStatus sev, string title, string problem, string? todo, string source, string? why = null, int? impact = null)
        {
            var r = new Recommendation
            {
                Severity = sev,
                Title = title ?? "",
                Problem = problem ?? "",
                WhatToDo = string.IsNullOrWhiteSpace(todo)
                    ? "Vedi i dettagli in questo report e nelle pagine Diagnostica e Prestazioni di FN Boost."
                    : todo!,
                Source = source,
                WhyItMatters = why ?? (WhyByTitle.TryGetValue(title ?? "", out var w) ? w : DefaultWhy(source, sev))
            };
            r.Impact = impact ?? EstimateImpact(r.Title, sev);
            return r;
        }

        private static string DefaultWhy(string source, CheckStatus sev) => source switch
        {
            "Sistema" => "È un'impostazione del PC che influisce su fluidità o stabilità in tutti i giochi, non solo in Fortnite.",
            "Rete" => "I problemi di rete non cambiano gli FPS ma si sentono come colpi non registrati, teletrasporti e ritardi nelle costruzioni.",
            "Log di Fortnite" => "Il gioco stesso ha registrato il problema nel suo log: è un indizio diretto, non una stima.",
            "Andamento" => "Confrontare le sessioni nel tempo aiuta a capire se una modifica ha migliorato o peggiorato le cose.",
            _ => sev == CheckStatus.Bad
                ? "È un problema evidente nei dati misurati durante il gioco."
                : "Nei dati misurati durante il gioco c'è margine di miglioramento."
        };

        public static int EstimateImpact(string title, CheckStatus sev)
        {
            var t = (title ?? "").ToLowerInvariant();
            foreach (var (needle, impact) in ImpactRules)
                if (t.Contains(needle, StringComparison.Ordinal)) return impact;
            return sev == CheckStatus.Bad ? 2 : 1;
        }

        private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
        {
            "del", "della", "dello", "dei", "degli", "delle", "nel", "nella", "nei", "con", "per", "sul", "sulla",
            "una", "uno", "the", "and", "alla", "allo", "agli", "dal", "dalla", "tra", "fra", "che", "non"
        };

        /// <summary>Due titoli parlano della stessa cosa: parole significative quasi uguali o una contenuta nell'altra.</summary>
        public static bool SimilarTitles(string a, string b)
        {
            var ta = TitleTokens(a);
            var tb = TitleTokens(b);
            if (ta.Count == 0 || tb.Count == 0) return string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
            int inter = ta.Intersect(tb).Count();
            int union = ta.Union(tb).Count();
            if (inter == Math.Min(ta.Count, tb.Count) && inter >= 1 && (ta.Count == tb.Count || inter >= 2 || Math.Min(ta.Count, tb.Count) == 1 && Math.Max(ta.Count, tb.Count) <= 3))
                return true;
            return union > 0 && (double)inter / union >= 0.6;
        }

        private static HashSet<string> TitleTokens(string? title)
        {
            var norm = RemoveDiacritics((title ?? "").ToLowerInvariant());
            var sb = new StringBuilder(norm.Length);
            foreach (var ch in norm) sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
            return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 3 && !StopWords.Contains(w)).ToHashSet(StringComparer.Ordinal);
        }

        private static string RemoveDiacritics(string s)
        {
            var nf = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(nf.Length);
            foreach (var ch in nf)
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        private static void AddNetwork(List<Recommendation> list, ReportData d)
        {
            var net = NetMetrics.From(d);
            if (!net.HasAny) return;
            bool wifi = net.ConnectionType.Contains("wi", StringComparison.OrdinalIgnoreCase) ||
                        net.ConnectionType.Contains("wireless", StringComparison.OrdinalIgnoreCase);
            bool homeIssue = false;

            if (net.GatewayAvg is > 10 || net.GatewayJitter is > 5 || net.GatewayLoss is > 0.5)
            {
                homeIssue = true;
                list.Add(Rec(net.GatewayLoss is > 2 || net.GatewayAvg is > 30 ? CheckStatus.Bad : CheckStatus.Warn,
                    "Rete di casa instabile",
                    $"Ping verso il router: media {F0(net.GatewayAvg)} ms, jitter {F1(net.GatewayJitter)} ms, perdita {F1(net.GatewayLoss ?? 0)}%. " +
                    "Un router collegato bene risponde in 1-3 ms senza perdite.",
                    wifi
                        ? "La connessione è in Wi-Fi: se puoi usa un cavo Ethernet (o almeno la banda 5 GHz vicino al router) e metti in pausa download e streaming su altri dispositivi."
                        : "Controlla cavo e porta del router, riavvia il router e metti in pausa download e streaming su altri dispositivi.",
                    "Rete",
                    "Se già il tratto PC → router è lento o perde pacchetti, ogni problema si somma a quello di Internet: è la prima cosa da sistemare ed è sotto il tuo controllo."));
            }
            if (net.LossPct is > 1)
                list.Add(Rec(net.LossPct > 3 ? CheckStatus.Bad : CheckStatus.Warn, "Perdita di pacchetti",
                    $"In media il {F1(net.LossPct)}% dei ping verso {net.TargetText} è andato perso.",
                    homeIssue ? "Prima sistema la rete di casa (vedi sopra)." :
                        "Se la perdita è solo verso il server prova un'altra regione di matchmaking; se resta, contatta il tuo provider con questo report.",
                    "Rete",
                    "I pacchetti persi diventano colpi non registrati, teletrasporti e azioni annullate."));
            if (net.PingAvg is > 80)
                list.Add(Rec(net.PingAvg > 120 ? CheckStatus.Bad : CheckStatus.Warn, "Ping alto",
                    $"Ping medio {F0(net.PingAvg)} ms verso {net.TargetText}.",
                    "Controlla che la regione di matchmaking in Fortnite sia quella più vicina (Impostazioni › Gioco › Regione) e che nessun altro dispositivo stia scaricando.",
                    "Rete",
                    "Con il ping alto vedi gli avversari in ritardo e le tue azioni arrivano al server dopo le loro."));
            if (net.Jitter is > 8)
                list.Add(Rec(net.Jitter > 20 ? CheckStatus.Bad : CheckStatus.Warn, "Ping instabile (jitter)",
                    $"Il ping varia in media di {F1(net.Jitter)} ms tra una misura e l'altra.",
                    wifi ? "Il Wi-Fi è la causa più comune: prova con un cavo Ethernet." :
                        "Metti in pausa download/streaming in casa; se il router lo permette attiva la QoS per il PC da gioco.",
                    "Rete",
                    "Un ping che salta è peggio di un ping alto ma stabile: il gioco non riesce a compensare e i movimenti diventano imprevedibili."));
            if (net.Freezes >= 3)
                list.Add(Rec(net.Freezes >= 10 ? CheckStatus.Bad : CheckStatus.Warn, "Freeze di rete",
                    $"{net.Freezes} volte il server non ha inviato pacchetti per oltre 250 ms{(net.LongestFreezeMs > 0 ? $" (il più lungo {F0(net.LongestFreezeMs)} ms)" : "")}.",
                    wifi ? "Tipico del Wi-Fi (interferenze, scansioni in background): usa un cavo se puoi." :
                        "Controlla se coincide con download, aggiornamenti o altri dispositivi in rete.",
                    "Rete",
                    "Durante un freeze il gioco non riceve aggiornamenti: gli avversari si bloccano e poi \"saltano\"."));
            if (net.OtherAppsKbps is > 5000)
                list.Add(Rec(CheckStatus.Warn, "Altre app usano la connessione",
                    $"Mentre giocavi altre applicazioni usavano in media {F0(net.OtherAppsKbps / 1000.0)} Mbit/s.",
                    "Chiudi o metti in pausa download, aggiornamenti (Steam, Windows Update, Epic), cloud e streaming durante le partite.",
                    "Rete",
                    "Il traffico di altre app può riempire la coda del router e far salire ping e jitter del gioco."));
            if (wifi && !homeIssue && (net.Jitter is > 8 || net.LossPct is > 1 || net.Freezes >= 3))
                list.Add(Rec(CheckStatus.Warn, "Connessione Wi-Fi",
                    $"Il PC è collegato in Wi-Fi{(net.WifiSignal is { } sig ? $" (segnale {sig}%)" : "")} e la rete ha mostrato instabilità.",
                    "Se possibile usa un cavo Ethernet; altrimenti avvicinati al router e usa la banda 5/6 GHz.",
                    "Rete",
                    "Il Wi-Fi aggiunge variazioni di latenza e perdite brevi che in gioco si sentono come lag."));

            // Processi in background pesanti (dai contatori PDH della sessione).
            var heavy = (d.Session?.TopProcesses ?? new List<ProcessUsage>())
                .Where(p => p.AvgCpuPct >= 10 && !p.Name.StartsWith("FortniteClient", StringComparison.OrdinalIgnoreCase))
                .Take(3).ToList();
            if (heavy.Count > 0)
                list.Add(Rec(CheckStatus.Warn, "Programmi in background pesanti",
                    "Durante la sessione: " + string.Join(", ", heavy.Select(p => $"{p.Name} (CPU media {F0(p.AvgCpuPct)}%, picco {F0(p.MaxCpuPct)}%)")) + ".",
                    "Chiudili prima di giocare o limita la loro attività (es. pausa a sincronizzazioni, registrazione, scansioni antivirus programmate).",
                    "Sessione",
                    "La CPU usata da altri programmi è tolta al gioco: abbassa soprattutto 1% low e regolarità."));
        }

        /// <summary>
        /// Consigli dal log di Fortnite. Diventano consigli SOLO le righe del periodo della sessione (log.Session):
        /// il log può coprire molte ore e più avvii del gioco, e avvisi/errori generici sono rumore normale.
        /// Senza sessione (nessun periodo) si usa tutto il log, dicendolo. Unica eccezione: i segnali di crash fuori dalla
        /// sessione compaiono come informazione, perché un crash di un avvio precedente resta utile da sapere.
        /// </summary>
        private static void AddLog(List<Recommendation> list, LogFindings? log)
        {
            if (log == null || !log.Found) return;
            var ses = log.Session;
            bool window = ses != null;
            int crash = window ? ses!.CrashMarkers : log.CrashMarkers;
            var crashKinds = window ? ses!.CrashKinds : log.CrashKinds;
            int memory = window ? ses!.MemoryWarnings : log.MemoryWarnings;
            int net = window ? ses!.NetworkIssues : log.NetworkIssues;
            var netKw = window ? ses!.NetworkKeywords : log.NetworkKeywords;
            int hitches = window ? ses!.Hitches : log.Hitches;
            int shader = window ? ses!.ShaderMessages : log.ShaderMessages;
            string where = window ? "durante la sessione" : "nel log (nessuna sessione da confrontare: tutto il log)";

            if (crash > 0)
            {
                bool gpu = crashKinds.Keys.Any(k => k.Contains("GPU", StringComparison.OrdinalIgnoreCase) ||
                                                    k.Contains("DXGI", StringComparison.OrdinalIgnoreCase) ||
                                                    k.Contains("D3D", StringComparison.OrdinalIgnoreCase));
                list.Add(Rec(CheckStatus.Bad, gpu ? "Crash della GPU nel log di Fortnite" : "Crash o errori gravi nel log di Fortnite",
                    $"Il log contiene {Plural(crash, "riga", "righe")} con segnali di crash {where}: " + string.Join(", ", crashKinds.Select(k => $"{k.Key} ×{k.Value}")) + ".",
                    gpu
                        ? "Aggiorna (o reinstalla pulito) il driver video, togli overclock/undervolt di GPU e memoria, verifica alimentatore e temperature. In Fortnite prova la modalità DirectX 12 senza ray tracing."
                        : "Verifica i file del gioco dall'Epic Games Launcher (Libreria › ⋯ › Gestisci › Verifica), togli overclock e controlla la RAM (es. Diagnostica memoria Windows).",
                    "Log di Fortnite",
                    gpu
                        ? "\"GPU crashed\" / \"Device removed\" significano che il driver video si è bloccato: di solito instabilità di driver, overclock, alimentazione o temperatura, non un'impostazione del gioco."
                        : "I crash fanno perdere partite intere: vanno risolti prima di ottimizzare gli FPS.",
                    3));
            }
            else if (window && log.CrashMarkers > 0)
            {
                list.Add(Rec(CheckStatus.Info, "Segnali di crash nel log, fuori dalla sessione",
                    $"Nel resto del log (fuori dal periodo della sessione) ci sono {Plural(log.CrashMarkers, "riga", "righe")} con segnali di crash: " +
                    string.Join(", ", log.CrashKinds.Select(k => $"{k.Key} ×{k.Value}")) + ". Durante la sessione analizzata no.",
                    "Se il gioco si è chiuso da solo in un avvio precedente, verifica i file del gioco dall'Epic Games Launcher e aggiorna il driver video; altrimenti ignoralo.",
                    "Log di Fortnite",
                    "Non riguarda la sessione misurata, ma un crash in un avvio precedente è comunque un indizio utile.", 1));
            }
            if (memory > 0)
                list.Add(Rec(CheckStatus.Warn, "Avvisi di memoria nel log di Fortnite",
                    $"{Plural(memory, "riga parla", "righe parlano")} di memoria insufficiente {where}.",
                    "Chiudi le app pesanti, lascia il file di paging gestito da Windows e abbassa la qualità delle texture.",
                    "Log di Fortnite", null, 2));
            if (net >= 5)
                list.Add(Rec(CheckStatus.Warn, "Problemi di connessione nel log di Fortnite",
                    $"{Plural(net, "riga", "righe")} di rete con timeout, disconnessioni o pacchetti fuori ordine {where} (" +
                    string.Join(", ", netKw.OrderByDescending(k => k.Value).Select(k => $"{k.Key} ×{k.Value}")) + ").",
                    "Confronta con ping e perdita misurati da FN Boost: se anche quelli sono alti il problema è la connessione, altrimenti può essere il server.",
                    "Log di Fortnite", null, 2));
            if (hitches >= 10)
                list.Add(Rec(CheckStatus.Warn, "Hitch registrati dal gioco",
                    $"Il gioco ha registrato {hitches} hitch (blocchi del thread di gioco o di caricamento) {where}.",
                    "Spesso dipendono dallo streaming dei dati: tieni Fortnite su SSD, chiudi le app in background e controlla che la RAM non sia piena.",
                    "Log di Fortnite", null, 2));
            if (shader >= 50)
                list.Add(Rec(CheckStatus.Info, "Compilazione shader in corso",
                    $"{shader} messaggi su shader/PSO {where}.",
                    "Dopo un aggiornamento del gioco o del driver è normale per qualche partita; non cancellare la cache shader senza motivo.",
                    "Log di Fortnite",
                    "Mentre gli shader vengono compilati compaiono stutter che spariscono da soli.", 1));
        }

        // ================= Metriche di riepilogo =================

        /// <summary>Numeri di rete unificati: dal riepilogo della sessione, dai campioni al secondo o dallo stato dal vivo.</summary>
        private sealed class NetMetrics
        {
            public double? PingAvg, Jitter, LossPct, GatewayAvg, GatewayJitter, GatewayLoss, OtherAppsKbps;
            public int Freezes;
            public double LongestFreezeMs;
            public string ConnectionType = "";
            public int? WifiSignal;
            public string TargetText = "il server";
            public bool HasAny => PingAvg != null || GatewayAvg != null || LossPct != null || Freezes > 0 || OtherAppsKbps != null;

            public static NetMetrics From(ReportData d)
            {
                var m = new NetMetrics();
                var s = d.Session;
                var n = s?.Network;
                var secs = s?.Seconds ?? new List<SecondSample>();
                if (n != null)
                {
                    m.PingAvg = n.Game?.AvgMs;
                    m.Jitter = n.Game?.JitterMs;
                    m.LossPct = n.Game?.LossPct;
                    m.GatewayAvg = n.Gateway?.AvgMs;
                    m.GatewayJitter = n.Gateway?.JitterMs;
                    m.GatewayLoss = n.Gateway?.LossPct;
                    m.OtherAppsKbps = n.AvgOtherAppsKbps > 0 ? n.AvgOtherAppsKbps : null;
                    m.Freezes = n.Freezes;
                    m.LongestFreezeMs = n.LongestFreezeMs;
                    m.ConnectionType = n.ConnectionType ?? "";
                    m.WifiSignal = n.WifiSignalPct;
                    m.TargetText = n.PingTargetKind == "regione"
                        ? $"la regione Epic{(string.IsNullOrEmpty(n.RegionName) ? "" : " " + n.RegionName)}"
                        : "il server di gioco";
                }
                m.PingAvg ??= Avg(secs, x => x.PingMs);
                m.Jitter ??= Avg(secs, x => x.JitterMs);
                m.LossPct ??= Avg(secs, x => x.LossPct);
                m.GatewayAvg ??= Avg(secs, x => x.GatewayPingMs);
                m.OtherAppsKbps ??= Avg(secs, x => x.OtherAppsKbps);
                if (n == null && secs.Count > 0) m.Freezes = secs.Sum(x => x.NetFreezes);

                if (s == null || (n == null && m.PingAvg == null))
                {
                    var live = d.LiveNetwork;
                    if (live is { Available: true })
                    {
                        m.PingAvg = live.Game?.AvgMs;
                        m.Jitter = live.Game?.JitterMs;
                        m.LossPct = live.Game?.LossPct;
                        m.GatewayAvg = live.Gateway?.AvgMs;
                        m.GatewayJitter = live.Gateway?.JitterMs;
                        m.GatewayLoss = live.Gateway?.LossPct;
                        m.OtherAppsKbps = live.OtherAppsKbps > 0 ? live.OtherAppsKbps : null;
                        m.Freezes = live.RecentFreezes;
                        m.ConnectionType = live.ConnectionType ?? "";
                        m.WifiSignal = live.WifiSignalPct;
                        m.TargetText = live.ServerEndpoint != null ? "il server di gioco" : "la regione Epic";
                    }
                }
                return m;
            }
        }

        private static double? Avg(IEnumerable<SecondSample> secs, Func<SecondSample, double?> sel)
        {
            var v = secs.Select(sel).Where(x => x.HasValue && double.IsFinite(x.Value)).Select(x => x!.Value).ToList();
            return v.Count == 0 ? null : v.Average();
        }

        private static double? SessionPing(PerfSession s) =>
            s.Network?.Game?.AvgMs ?? Avg(s.Seconds ?? new List<SecondSample>(), x => x.PingMs);

        /// <summary>Riepilogo di una sessione per la tabella delle sessioni precedenti.</summary>
        public static SessionSummary Summarize(PerfSession s, bool selected = false) => new()
        {
            Id = s.Id,
            StartedAt = s.StartedAt,
            Label = s.Label ?? "",
            DurationSec = s.DurationSec,
            AvgFps = s.Stats?.AvgFps ?? 0,
            Low1Fps = s.Stats?.Low1Fps ?? 0,
            Low01Fps = s.Stats?.Low01Fps ?? 0,
            StuttersPerMin = s.Stats?.StuttersPerMin ?? 0,
            PingAvgMs = SessionPing(s),
            Selected = selected
        };

        // ================= Testo per la chat =================

        public static string BuildSummaryText(ReportData d)
        {
            var sb = new StringBuilder();
            sb.Append($"FN Boost {d.AppVersion} · report diagnostico del {d.GeneratedAt.ToString("dd/MM/yyyy HH:mm", Inv)} ({d.SchemaVersion})\n");
            sb.Append("Dati misurati sul PC; nomi, account e IP privati rimossi.\n");
            foreach (var n in d.Notes.Take(3)) sb.Append("Nota: ").Append(Clip(n, 160)).Append('\n');

            if (d.System is { } sys)
            {
                sb.Append("\n[SISTEMA]\n");
                sb.Append($"CPU: {sys.Cpu} ({sys.Cores} core / {sys.Threads} thread)\n");
                sb.Append($"RAM: {F1(sys.RamTotalGb)} GB {sys.RamType}{(sys.RamConfiguredMts > 0 ? $" a {sys.RamConfiguredMts} MT/s" : "")}" +
                          $"{(sys.RamRatedMts > sys.RamConfiguredMts ? $" (nominale {sys.RamRatedMts})" : "")}{(sys.RamModules > 0 ? $", {sys.RamModules} moduli" : "")}\n");
                foreach (var g in sys.Gpus.Take(2))
                    sb.Append($"GPU: {g.Name} · driver {g.DriverVersion}{(g.DriverDate is { } dd ? $" ({dd.ToString("dd/MM/yyyy", Inv)})" : "")}\n");
                foreach (var m in sys.Displays.Take(2))
                    sb.Append($"Monitor: {DisplayText(m)}\n");
                sb.Append($"OS: {sys.Os} · piano energetico: {sys.PowerPlan} · paging: {sys.PageFile} · HVCI: {YesNo(sys.HvciRunning)}\n");
            }

            sb.Append("\n[FORTNITE]\n");
            sb.Append(FortniteOneLiner(d.Fortnite)).Append('\n');

            if (d.Session is { } s)
            {
                var st = s.Stats ?? new FrameStatsResult();
                sb.Append($"\n[SESSIONE {s.StartedAt.ToString("dd/MM/yyyy HH:mm", Inv)}{(string.IsNullOrWhiteSpace(s.Label) ? "" : " · " + Clip(s.Label, 40))} · {Duration(s.DurationSec)}]\n");
                sb.Append($"Media {F0(st.AvgFps)} FPS · 1% low {F0(st.Low1Fps)} · 0,1% low {F0(st.Low01Fps)} · min/max {F0(st.MinFps)}/{F0(st.MaxFps)} · " +
                          $"stutter {st.Stutters} ({F1(st.StuttersPerMin)}/min) · regolarità {F0(st.ConsistencyScore)}/100 · frametime medio {F1(st.AvgFrametimeMs)} ms, max {F1(st.MaxFrametimeMs)} ms\n");
                if (s.UnfocusedSec >= 1)
                    sb.Append($"Esclusi {F0(s.UnfocusedSec)} s ({s.ExcludedFrames} frame) con il gioco fuori fuoco{(s.UnfocusedEstimated ? " (stima: tratto a ~30 FPS con GPU ferma)" : "")}: " +
                              PerfAnalyzer.BackgroundThrottleText(s.ProcessName) + ".\n");
                var cpu = Avg(s.Seconds ?? new List<SecondSample>(), x => x.Fps > 0 && !x.Unfocused ? x.CpuPercent : null);
                var gpu = Avg(s.Seconds ?? new List<SecondSample>(), x => x.Fps > 0 && !x.Unfocused ? x.GpuPercent : null);
                var ram = Avg(s.Seconds ?? new List<SecondSample>(), x => x.Fps > 0 && !x.Unfocused ? x.RamPercent : null);
                sb.Append($"CPU media {F0(cpu)}% · GPU media {F0(gpu)}% · RAM {F0(ram)}%" +
                          $"{(s.FpsCap is > 0 ? $" · cap {F0(s.FpsCap)}" : "")}{(s.RefreshHz is > 0 ? $" · monitor {s.RefreshHz} Hz" : "")}" +
                          $"{(string.IsNullOrEmpty(s.RenderMode) ? "" : " · rendering " + s.RenderMode)}\n");
                if (s.TopProcesses is { Count: > 0 } tp)
                    sb.Append("Processi in background: " + string.Join(", ", tp.Take(4).Select(p => $"{p.Name} {F0(p.AvgCpuPct)}%")) + "\n");
            }
            else sb.Append("\n[SESSIONE] nessuna sessione registrata.\n");

            var net = NetMetrics.From(d);
            if (net.HasAny)
            {
                sb.Append("\n[RETE]\n");
                sb.Append($"Ping {F0(net.PingAvg)} ms verso {net.TargetText} · jitter {F1(net.Jitter)} ms · perdita {F1(net.LossPct)}% · " +
                          $"router {F1(net.GatewayAvg)} ms · freeze {net.Freezes}" +
                          $"{(string.IsNullOrEmpty(net.ConnectionType) ? "" : " · " + net.ConnectionType)}{(net.WifiSignal is { } w ? $" (segnale {w}%)" : "")}\n");
            }

            if (!string.IsNullOrEmpty(d.Verdict))
                sb.Append("\n[VERDETTO]\n").Append(d.Verdict).Append('\n');

            if (d.Recommendations.Count > 0)
            {
                sb.Append(d.PerformsWell ? "\n[MIGLIORAMENTI FACOLTATIVI]\n" : "\n[COSA NON VA / COSA MIGLIORARE]\n");
                int i = 1;
                foreach (var r in d.Recommendations.Take(8))
                    sb.Append($"{i++}. [{SevText(r)}] {r.Title}: {Clip(r.Problem, 150)} → {Clip(r.WhatToDo, 150)}\n");
            }
            else sb.Append(d.PerformsWell
                ? "\n[MIGLIORAMENTI FACOLTATIVI] nessuno: non c'è niente da cambiare.\n"
                : "\n[COSA NON VA] nessun problema evidente rilevato.\n");

            if (d.Log is { Found: true } log)
            {
                sb.Append("\n[LOG DI FORTNITE]\n");
                if (log.Session is { } ses && log.WindowStart != null)
                {
                    sb.Append($"Durante la sessione ({log.WindowStart.Value.ToString("HH:mm:ss", Inv)}–{log.WindowEnd?.ToString("HH:mm:ss", Inv)} UTC, ±60 s): " +
                              $"crash {ses.CrashMarkers} · rete {ses.NetworkIssues} · hitch {ses.Hitches} · shader {ses.ShaderMessages} · memoria {ses.MemoryWarnings} · " +
                              $"avvisi {ses.Warnings}, errori {ses.Errors}{(ses.Lines == 0 ? " (nessuna riga del log in quel periodo)" : "")}\n");
                    sb.Append($"Intero log (contesto{LogSpanText(log)}): crash {log.CrashMarkers} · rete {log.NetworkIssues} · hitch {log.Hitches} · shader {log.ShaderMessages} · " +
                              $"avvisi {log.Warnings}, errori {log.Errors}\n");
                }
                else
                    sb.Append($"Intero log{LogSpanText(log)}: crash {log.CrashMarkers} · rete {log.NetworkIssues} · hitch {log.Hitches} · shader {log.ShaderMessages} · memoria {log.MemoryWarnings} · " +
                              $"avvisi {log.Warnings}, errori {log.Errors}\n");
                sb.Append("Avvisi ed errori generici sono normali: Fortnite ne scrive migliaia anche quando funziona tutto.\n");
                foreach (var h in log.Highlights.Where(h => log.Session == null || h.InSession).Take(5))
                    sb.Append("- ").Append(Clip(h.Text, 180)).Append(h.Count > 1 ? $" (×{h.Count})" : "").Append('\n');
            }

            var text = sb.ToString();
            if (text.Length > SummaryMaxChars)
            {
                var cut = text.LastIndexOf('\n', SummaryMaxChars - 2);
                text = text.Substring(0, cut > SummaryMaxChars / 2 ? cut : SummaryMaxChars - 2) + "\n…";
            }
            return text;
        }

        private static string FortniteOneLiner(ReportFortnite f)
        {
            if (!f.Installed && !f.ConfigFound) return "Fortnite non trovato su questo PC.";
            var parts = new List<string>();
            if (f.Installed)
                parts.Add($"installato{(f.DiskType != null ? " su " + f.DiskType : "")}{(f.FreeGb is { } g ? $" ({F0(g)} GB liberi)" : "")}");
            if (f.Settings is not { } s)
            {
                parts.Add("impostazioni non lette (GameUserSettings.ini non trovato)");
                return string.Join(" · ", parts);
            }
            parts.Add("rendering " + s.RenderMode);
            if (f.RhiInUse != null) parts.Add("API usata (log) " + f.RhiInUse);
            parts.Add("limite FPS " + (s.FpsCap > 0 ? F0(s.FpsCap) : "illimitato"));
            parts.Add("VSync " + OnOff(s.VSync));
            parts.Add("Reflex " + ReflexText(s.Reflex));
            parts.Add(WindowModeText(s.WindowMode));
            if (s.ResolutionX > 0) parts.Add($"{s.ResolutionX}×{s.ResolutionY}");
            parts.Add("ray tracing " + OnOff(s.RayTracing));
            parts.Add("Nanite " + OnOff(s.Nanite));
            parts.Add("motion blur " + OnOff(s.MotionBlur));
            var line = string.Join(" · ", parts);
            if (s.PreferredRhi.Length > 0 || s.PreferredFeatureLevel.Length > 0 || f.GameBuild != null)
                line += $"\nini: PreferredRHI={(s.PreferredRhi.Length > 0 ? s.PreferredRhi : "–")} PreferredFeatureLevel={(s.PreferredFeatureLevel.Length > 0 ? s.PreferredFeatureLevel : "–")}" +
                        (f.GameBuild != null ? " · versione " + f.GameBuild : "");
            if (s.Scalability.Count > 0)
                line += "\nQualità: " + string.Join(" ", s.Scalability.Select(kv => $"{ShortSg(kv.Key)}={kv.Value}"));
            return line;
        }

        /// <summary>", 11,5 h: 06/10 22:43 → 07/10 10:18 UTC" se il log ha orari, altrimenti "".</summary>
        private static string LogSpanText(LogFindings log)
        {
            var dur = LogSpanDuration(log);
            return dur.Length == 0 ? "" : $"{dur}: {log.FirstTime!.Value.ToString("dd/MM HH:mm", Inv)} → {log.LastTime!.Value.ToString("dd/MM HH:mm", Inv)} UTC";
        }

        /// <summary>", 11,5 h" (o ", 25 min") = quanto tempo copre il log letto, "" senza orari.</summary>
        private static string LogSpanDuration(LogFindings log)
        {
            if (log.FirstTime is not { } a || log.LastTime is not { } b || b < a) return "";
            double h = (b - a).TotalHours;
            return h >= 1 ? $", {F1(h)} h" : $", {F0((b - a).TotalMinutes)} min";
        }

        // ================= HTML =================

        public static string BuildHtml(ReportData d)
        {
            var h = new StringBuilder(64 * 1024);
            var s = d.Session;
            var st = s?.Stats;
            var net = NetMetrics.From(d);

            h.Append("<!doctype html>\n<html lang=\"it\">\n<head>\n<meta charset=\"utf-8\">\n");
            h.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
            h.Append("<title>FN Boost · Report diagnostico</title>\n<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n<main>\n");

            // ---- intestazione ----
            h.Append("<header><div class=\"brand\">FN Boost</div><h1>Report diagnostico</h1>");
            h.Append($"<p class=\"muted\">Generato il {E(d.GeneratedAt.ToString("dd/MM/yyyy 'alle' HH:mm", Inv))} · FN Boost {E(d.AppVersion)} · formato {E(d.SchemaVersion)}");
            if (s != null) h.Append($" · sessione del {E(s.StartedAt.ToString("dd/MM/yyyy HH:mm", Inv))}{(string.IsNullOrWhiteSpace(s.Label) ? "" : " (" + E(s.Label) + ")")}, durata {E(Duration(s.DurationSec))}");
            h.Append("</p>");
            h.Append("<div class=\"privacy\"><b>Privacy:</b> questo report è pensato per essere condiviso. Nome utente di Windows, nome del PC, " +
                     "email, ID account Epic, token, indirizzi IP della rete locale e il tuo IP pubblico sono stati rimossi o sostituiti " +
                     "(es. &lt;utente&gt;, &lt;ip-locale&gt;). Restano gli indirizzi dei server di gioco e degli endpoint Epic. " +
                     "Le righe di chat, party e amici del log di Fortnite non vengono mai incluse.</div>");
            if (d.Notes.Count > 0)
            {
                h.Append("<ul class=\"notes\">");
                foreach (var n in d.Notes) h.Append("<li>").Append(E(n)).Append("</li>");
                h.Append("</ul>");
            }
            h.Append("</header>\n");

            // ---- riepilogo ----
            h.Append("<section><h2>Riepilogo</h2>");
            if (st == null || !st.HasData)
                h.Append("<p class=\"muted\">Nessuna sessione di gioco registrata: avvia Fortnite con FN Boost aperto (la registrazione è automatica) e rigenera il report.</p>");
            h.Append("<div class=\"tiles\">");
            if (st is { HasData: true })
            {
                Tile(h, "FPS medi", F0(st.AvgFps), "", null);
                Tile(h, "1% low", F0(st.Low1Fps), "FPS", st.AvgFps > 0 ? Grade(st.Low1Fps / st.AvgFps, 0.6, 0.45) : null);
                Tile(h, "0,1% low", F0(st.Low01Fps), "FPS", null);
                Tile(h, "Min / max", $"{F0(st.MinFps)} / {F0(st.MaxFps)}", "FPS", null);
                Tile(h, "Stutter", F1(st.StuttersPerMin), "al minuto", GradeLow(st.StuttersPerMin, 2, 10));
                Tile(h, "Regolarità", F0(st.ConsistencyScore), "/ 100", Grade(st.ConsistencyScore / 100, 0.75, 0.5));
                if (s!.UnfocusedSec >= 1)
                    Tile(h, "Fuori fuoco (esclusi)", F0(s.UnfocusedSec), s.UnfocusedEstimated ? "s · stima" : "s", CheckStatus.Info);
            }
            if (net.HasAny)
            {
                Tile(h, "Ping", F0(net.PingAvg), "ms", net.PingAvg is { } p ? GradeLow(p, 60, 120) : null);
                Tile(h, "Jitter", F1(net.Jitter), "ms", net.Jitter is { } j ? GradeLow(j, 5, 20) : null);
                Tile(h, "Perdita", F1(net.LossPct), "%", net.LossPct is { } l ? GradeLow(l, 0.5, 3) : null);
            }
            h.Append("</div></section>\n");

            // ---- cosa non va ----
            h.Append(d.PerformsWell ? "<section><h2>Verdetto e miglioramenti facoltativi</h2>" : "<section><h2>Cosa non va / Cosa migliorare</h2>");
            if (!string.IsNullOrEmpty(d.Verdict))
                h.Append($"<p class=\"verdict {(d.PerformsWell ? "s-ok" : "s-warn")}\">{E(d.Verdict)}</p>");
            if (d.Recommendations.Count == 0)
                h.Append("<p class=\"ok\">Nessun problema evidente rilevato nei dati disponibili.</p>");
            else
            {
                h.Append("<ol class=\"recs\">");
                foreach (var r in d.Recommendations)
                {
                    h.Append($"<li class=\"rec {SevClass(r.Severity)}\"><div class=\"rec-head\"><span class=\"badge {SevClass(r.Severity)}\">{E(SevText(r))}</span>");
                    h.Append($"<b>{E(r.Title)}</b><span class=\"muted small\">{E(r.Source)} · impatto {E(ImpactText(r.Impact))}</span></div>");
                    h.Append($"<dl><dt>Problema</dt><dd>{E(r.Problem)}</dd><dt>Perché conta</dt><dd>{E(r.WhyItMatters)}</dd><dt>Cosa fare</dt><dd>{E(r.WhatToDo)}</dd></dl></li>");
                }
                h.Append("</ol>");
            }
            h.Append("</section>\n");

            // ---- grafici ----
            if (s != null && (s.Seconds?.Count ?? 0) >= 2 || d.Histogram != null)
            {
                h.Append("<section><h2>Grafici</h2>");
                AppendCharts(h, d);
                h.Append("</section>\n");
            }

            // ---- analisi ----
            if (d.Insights.Count > 0 || d.Trend.Count > 0)
            {
                h.Append("<section><h2>Analisi della sessione</h2>");
                AppendInsights(h, d.Insights);
                if (d.Trend.Count > 0)
                {
                    h.Append("<h3>Andamento nel tempo</h3>");
                    AppendInsights(h, d.Trend);
                }
                h.Append("</section>\n");
            }

            // ---- rete ----
            AppendNetwork(h, d);

            // ---- processi ----
            if (s?.TopProcesses is { Count: > 0 } procs)
            {
                h.Append("<section><h2>Programmi in background durante la sessione</h2><p class=\"muted small\">Letti dai contatori di prestazioni di Windows, senza aprire i processi.</p>");
                h.Append("<table><thead><tr><th>Processo</th><th class=\"n\">CPU media</th><th class=\"n\">CPU picco</th><th class=\"n\">RAM media</th></tr></thead><tbody>");
                foreach (var p in procs)
                    h.Append($"<tr><td>{E(p.Name)}</td><td class=\"n\">{F1(p.AvgCpuPct)}%</td><td class=\"n\">{F1(p.MaxCpuPct)}%</td><td class=\"n\">{F0(p.AvgRamMb)} MB</td></tr>");
                h.Append("</tbody></table></section>\n");
            }

            // ---- sistema ----
            AppendSystem(h, d);

            // ---- Fortnite ----
            AppendFortnite(h, d.Fortnite, d.Log);

            // ---- tweak ----
            if (d.Tweaks.Count > 0)
            {
                h.Append("<section><h2>Tweak di FN Boost</h2>");
                h.Append($"<p class=\"muted small\">{d.Tweaks.Count(t => t.State == "Applied")} attivi su {d.Tweaks.Count}. Tutti reversibili.</p>");
                h.Append("<table><thead><tr><th>Tweak</th><th>Categoria</th><th>Stato</th><th>Consigliato</th></tr></thead><tbody>");
                foreach (var t in d.Tweaks)
                    h.Append($"<tr><td>{E(t.Title)} <span class=\"muted small\">{E(t.Id)}</span></td><td>{E(t.Category)}</td><td class=\"{TweakClass(t.State)}\">{E(TweakStateText(t.State))}</td><td>{(t.Recommended ? "sì" : "")}</td></tr>");
                h.Append("</tbody></table></section>\n");
            }

            // ---- sessioni precedenti ----
            if (d.PreviousSessions.Count > 0)
            {
                h.Append("<section><h2>Sessioni recenti</h2><table><thead><tr><th>Data</th><th>Etichetta</th><th class=\"n\">Durata</th><th class=\"n\">Media</th><th class=\"n\">1% low</th><th class=\"n\">0,1% low</th><th class=\"n\">Stutter/min</th><th class=\"n\">Ping</th></tr></thead><tbody>");
                foreach (var p in d.PreviousSessions)
                    h.Append($"<tr{(p.Selected ? " class=\"sel\"" : "")}><td>{E(p.StartedAt.ToString("dd/MM/yyyy HH:mm", Inv))}</td><td>{E(p.Label)}</td><td class=\"n\">{E(Duration(p.DurationSec))}</td>" +
                             $"<td class=\"n\">{F0(p.AvgFps)}</td><td class=\"n\">{F0(p.Low1Fps)}</td><td class=\"n\">{F0(p.Low01Fps)}</td><td class=\"n\">{F1(p.StuttersPerMin)}</td><td class=\"n\">{(p.PingAvgMs is { } pg ? F0(pg) + " ms" : "–")}</td></tr>");
                h.Append("</tbody></table></section>\n");
            }

            // ---- log ----
            AppendLog(h, d.Log);

            // ---- metodo ----
            h.Append("<footer><h2>Come vengono misurati i dati</h2><ul>");
            h.Append("<li><b>FPS e frametime</b>: eventi ETW <i>Present</i> di DirectX letti da Windows, lo stesso metodo di PresentMon e della Xbox Game Bar. Il frametime è il tempo tra due Present consecutivi (MsBetweenPresents). FN Boost non entra mai nel processo del gioco.</li>");
            h.Append("<li><b>Gioco fuori fuoco</b>: circa 10 volte al secondo FN Boost controlla quale finestra è in primo piano (solo GetForegroundWindow, nessun accesso al gioco). I frame presentati mentre il gioco non è in primo piano, quelli a cavallo del cambio e ~0,5 s di assestamento al ritorno sono esclusi da statistiche, stutter e grafici: in secondo piano molti giochi rallentano da soli (Fortnite si limita a ~30 FPS). Nelle sessioni vecchie l'esclusione è stimata (tratto iniziale/finale a ~30 FPS con GPU sotto il 10%).</li>");
            h.Append("<li><b>FPS medi</b> = frame totali / tempo totale. <b>1% low</b> e <b>0,1% low</b> = FPS calcolati dalla media dell'1% e dello 0,1% dei frametime più lunghi (i momenti peggiori). <b>Stutter</b> = frame molto più lunghi della mediana dei frame vicini.</li>");
            h.Append("<li><b>CPU, GPU, RAM, processi</b>: contatori di prestazioni di Windows (PDH), campionati una volta al secondo.</li>");
            h.Append("<li><b>Ping</b>: ping ICMP inviato da FN Boost al server di gioco (o all'endpoint Epic della regione se il server non risponde), al router e a Internet. Può differire di qualche millisecondo dal ping mostrato in gioco, che è misurato a livello di applicazione. Jitter = variazione media tra ping consecutivi.</li>");
            h.Append("<li><b>Traffico del gioco</b>: eventi ETW Kernel-Network (solo dimensione e indirizzi dei pacchetti UDP, mai il contenuto).</li>");
            h.Append("<li><b>Log di Fortnite</b>: lettura del file FortniteGame.log che il gioco scrive per l'utente; chat, party e amici esclusi. Gli orari del log sono in UTC: si considera il periodo della sessione ± 60 s, il resto è contesto.</li>");
            h.Append("<li>Le analisi indicano correlazioni e cause probabili, non certezze: patch del gioco, mappa e modalità cambiano molto i risultati.</li>");
            h.Append("</ul><p class=\"muted small\">FN Boost · report generato localmente, nessun dato è stato inviato a server esterni.</p></footer>\n");

            h.Append("</main>\n<script>window.addEventListener('beforeprint',function(){document.querySelectorAll('details').forEach(function(x){x.open=true;});});</script>\n");
            h.Append("</body>\n</html>\n");
            // Tabelle scorrevoli in orizzontale sugli schermi stretti (il testo dei dati è già con escaping: nessun "<table" spurio).
            return h.ToString().Replace("<table", "<div class=\"tw\"><table").Replace("</table>", "</table></div>");
        }

        private static void AppendCharts(StringBuilder h, ReportData d)
        {
            var s = d.Session;
            var secs = s?.Seconds?.Where(x => double.IsFinite(x.T)).OrderBy(x => x.T).ToList() ?? new List<SecondSample>();
            if (secs.Count >= 2)
            {
                var xs = secs.Select(x => x.T).ToArray();
                var refs = new List<SvgChart.Ref>();
                if (s!.FpsCap is > 0) refs.Add(new SvgChart.Ref(s.FpsCap.Value, $"cap {F0(s.FpsCap)}", Warn));
                if (s.RefreshHz is > 0) refs.Add(new SvgChart.Ref(s.RefreshHz.Value, $"{s.RefreshHz} Hz", Accent2));
                // Secondi con il gioco fuori fuoco: buco nella linea degli FPS e banda grigia su tutti i grafici.
                var bands = UnfocusedBands(secs);
                string bandNote = bands.Count > 0 ? " Le bande grigie sono i secondi con il gioco fuori fuoco, esclusi dalle statistiche." : "";
                Chart(h, "FPS nel tempo", "FPS al secondo e 1% low di ogni secondo." + bandNote, xs, new[]
                {
                    new SvgChart.Series("FPS", Accent, secs.Select(x => x.Unfocused ? null : (double?)x.Fps).ToArray()),
                    new SvgChart.Series("1% low", Bad, secs.Select(x => x.Unfocused ? null : (double?)x.Low1Fps).ToArray(), 1.2)
                }, refs, "FPS", null, bands);

                if (secs.Any(x => x.PingMs.HasValue || x.GatewayPingMs.HasValue))
                    Chart(h, "Ping e jitter", "Ping verso il server/regione, jitter e ping verso il router (ms).", xs, new[]
                    {
                        new SvgChart.Series("Ping", Accent2, secs.Select(x => x.PingMs).ToArray()),
                        new SvgChart.Series("Jitter", Warn, secs.Select(x => x.JitterMs).ToArray(), 1.2),
                        new SvgChart.Series("Router", Ok, secs.Select(x => x.GatewayPingMs).ToArray(), 1.2)
                    }, new List<SvgChart.Ref>(), "ms");

                if (secs.Any(x => x.CpuPercent > 0 || x.GpuPercent.HasValue))
                    Chart(h, "CPU e GPU", "Utilizzo medio di CPU (tutti i core) e GPU (%)." + bandNote, xs, new[]
                    {
                        new SvgChart.Series("CPU", Accent2, secs.Select(x => (double?)x.CpuPercent).ToArray()),
                        new SvgChart.Series("GPU", Accent, secs.Select(x => x.GpuPercent).ToArray())
                    }, new List<SvgChart.Ref>(), "%", 100, bands);
            }
            if (d.Histogram is { Counts.Count: > 0 } hist)
            {
                h.Append("<figure><figcaption><b>Distribuzione dei frametime</b><span class=\"muted small\"> · quanti frame per durata (ms); più la montagna è stretta, più il gioco è regolare. " +
                         $"Mediana {F1(hist.MedianMs)} ms, 99° percentile {F1(hist.P99Ms)} ms{(s?.ExcludedFrames > 0 ? "; solo frame con il gioco in primo piano" : "")}.</span></figcaption>");
                h.Append(SvgChart.Bars(hist.EdgesMs, hist.Counts, Accent));
                h.Append("</figure>");
            }
        }

        /// <summary>Tratti consecutivi di secondi Unfocused come intervalli [da, a] in secondi (per le bande dei grafici).</summary>
        public static List<(double From, double To)> UnfocusedBands(IReadOnlyList<SecondSample> secs)
        {
            var bands = new List<(double, double)>();
            int i = 0;
            while (i < secs.Count)
            {
                if (!secs[i].Unfocused)
                {
                    i++;
                    continue;
                }
                int start = i;
                while (i < secs.Count && secs[i].Unfocused) i++;
                // Il campione T copre il secondo che finisce in T+1: la banda va da T del primo a T+1 dell'ultimo.
                bands.Add((secs[start].T, secs[i - 1].T + 1));
            }
            return bands;
        }

        private static void Chart(StringBuilder h, string title, string caption, double[] xs, IList<SvgChart.Series> series, IList<SvgChart.Ref> refs, string unit,
            double? yMax = null, IList<(double From, double To)>? bands = null)
        {
            h.Append($"<figure><figcaption><b>{E(title)}</b><span class=\"muted small\"> · {E(caption)}</span></figcaption>");
            h.Append(SvgChart.Line(xs, series, refs, yMax, bands));
            h.Append("<div class=\"legend\">");
            foreach (var se in series.Where(x => x.Y.Any(v => v.HasValue)))
                h.Append($"<span><i style=\"background:{se.Color}\"></i>{E(se.Name)}</span>");
            foreach (var r in refs) h.Append($"<span><i class=\"dash\" style=\"border-color:{r.Color}\"></i>{E(r.Label)}</span>");
            if (bands is { Count: > 0 }) h.Append("<span><i class=\"band\"></i>gioco fuori fuoco</span>");
            h.Append($"<span class=\"muted\">asse Y: {E(unit)} · asse X: tempo</span></div></figure>");
        }

        private static void AppendInsights(StringBuilder h, List<PerfInsight> list)
        {
            h.Append("<ul class=\"insights\">");
            foreach (var i in list)
                h.Append($"<li class=\"{SevClass(i.Severity)}\"><span class=\"badge {SevClass(i.Severity)}\">{E(SevText(i.Severity))}</span><b>{E(i.Title)}</b>" +
                         $"<div>{E(i.Message)}</div>{(string.IsNullOrWhiteSpace(i.Hint) ? "" : $"<div class=\"muted\">{E(i.Hint)}</div>")}</li>");
            h.Append("</ul>");
        }

        private static void AppendNetwork(StringBuilder h, ReportData d)
        {
            var n = d.Session?.Network;
            var live = d.LiveNetwork;
            if (n == null && live == null) return;
            h.Append("<section><h2>Rete</h2>");
            if (n != null)
            {
                h.Append("<table class=\"kv\"><tbody>");
                Row(h, "Server di gioco", n.ServerEndpoints.Count > 0 ? string.Join(", ", n.ServerEndpoints) : "non rilevato");
                Row(h, "Regione Epic", n.RegionName ?? "–");
                Row(h, "Regione con il ping migliore", BestRegionText(n.BestRegionName, n.BestRegionPingMs));
                Row(h, "Ping misurato verso", n.PingTargetKind == "regione" ? "endpoint Epic della regione (il server non risponde al ping)" : n.PingTargetKind == "server" ? "server di gioco" : n.PingTargetKind);
                Row(h, "Connessione", $"{n.ConnectionType}{(n.LinkSpeedMbps is { } ls ? $" · {F0(ls)} Mbit/s" : "")}{(n.WifiSignalPct is { } w ? $" · segnale {w}%" : "")}");
                Row(h, "Pacchetti al secondo", $"in {F0(n.AvgPacketsInPerSec)} · out {F0(n.AvgPacketsOutPerSec)}");
                Row(h, "Banda del gioco", $"in {F0(n.AvgGameKbpsIn)} kbit/s · out {F0(n.AvgGameKbpsOut)} kbit/s");
                Row(h, "Altre app", $"media {F0(n.AvgOtherAppsKbps)} kbit/s · picco {F0(n.MaxOtherAppsKbps)} kbit/s");
                Row(h, "Freeze di rete", $"{n.Freezes}{(n.LongestFreezeMs > 0 ? $" (il più lungo {F0(n.LongestFreezeMs)} ms)" : "")}");
                h.Append("</tbody></table>");
                PingTable(h, new[] { n.Game, n.Region, n.Gateway, n.Internet });
            }
            else if (live != null)
            {
                h.Append("<p class=\"muted small\">Nessun dato di rete nella sessione: questo è lo stato dal vivo al momento del report.</p>");
                h.Append("<table class=\"kv\"><tbody>");
                Row(h, "Stato", live.StatusText);
                Row(h, "Server di gioco", live.ServerEndpoint ?? "nessuna partita in corso");
                Row(h, "Regione Epic", live.RegionName ?? "–");
                Row(h, "Regione con il ping migliore", BestRegionText(live.BestRegionName, live.BestRegionPingMs));
                Row(h, "Connessione", $"{live.ConnectionType}{(string.IsNullOrEmpty(live.AdapterName) ? "" : " · " + live.AdapterName)}{(live.LinkSpeedMbps is { } ls ? $" · {F0(ls)} Mbit/s" : "")}{(live.WifiSignalPct is { } w ? $" · segnale {w}%" : "")}");
                Row(h, "Freeze recenti (60 s)", live.RecentFreezes.ToString(Inv));
                h.Append("</tbody></table>");
                PingTable(h, new[] { live.Game, live.Region, live.Gateway, live.Internet });
            }
            h.Append("</section>\n");
        }

        // Regione Epic con il ping più basso (misurata dal motore di rete), "–" se non disponibile.
        private static string BestRegionText(string? name, double? ms) =>
            string.IsNullOrEmpty(name) ? "–" : ms is { } v ? $"{name} ({F0(v)} ms)" : name!;

        private static void PingTable(StringBuilder h, IEnumerable<PingStats?> pings)
        {
            var list = pings.Where(p => p != null).Select(p => p!).ToList();
            if (list.Count == 0) return;
            h.Append("<table><thead><tr><th>Bersaglio</th><th>Host</th><th class=\"n\">Media</th><th class=\"n\">Min</th><th class=\"n\">Max</th><th class=\"n\">P95</th><th class=\"n\">Jitter</th><th class=\"n\">Perdita</th></tr></thead><tbody>");
            foreach (var p in list)
                h.Append($"<tr><td>{E(p.Target)}</td><td>{E(p.Host)}</td><td class=\"n\">{Ms(p.AvgMs)}</td><td class=\"n\">{Ms(p.MinMs)}</td><td class=\"n\">{Ms(p.MaxMs)}</td>" +
                         $"<td class=\"n\">{Ms(p.P95Ms)}</td><td class=\"n\">{Ms(p.JitterMs)}</td><td class=\"n\">{F1(p.LossPct)}% <span class=\"muted small\">({p.Received}/{p.Sent})</span></td></tr>");
            h.Append("</tbody></table>");
        }

        private static void AppendSystem(StringBuilder h, ReportData d)
        {
            h.Append("<section><h2>Sistema</h2>");
            if (d.System is not { } sys) h.Append("<p class=\"muted\">Informazioni di sistema non disponibili.</p>");
            else
            {
                h.Append("<table class=\"kv\"><tbody>");
                Row(h, "Sistema operativo", sys.Os);
                Row(h, "CPU", $"{sys.Cpu} · {sys.Cores} core · {sys.Threads} thread");
                Row(h, "RAM", $"{F1(sys.RamTotalGb)} GB {sys.RamType}{(sys.RamConfiguredMts > 0 ? $" · {sys.RamConfiguredMts} MT/s" : "")}{(sys.RamRatedMts > 0 ? $" (nominale {sys.RamRatedMts})" : "")}{(sys.RamModules > 0 ? $" · {sys.RamModules} moduli" : "")}");
                foreach (var g in sys.Gpus)
                    Row(h, "GPU", $"{g.Name} · driver {g.DriverVersion}{(g.DriverDate is { } dd ? " del " + dd.ToString("dd/MM/yyyy", Inv) : "")}");
                foreach (var m in sys.Displays) Row(h, "Monitor", DisplayText(m));
                Row(h, "Scheda madre", sys.Board);
                Row(h, "BIOS", $"{sys.BiosVersion}{(sys.BiosDate is { } bd ? " del " + bd.ToString("dd/MM/yyyy", Inv) : "")}");
                Row(h, "Secure Boot / TPM", $"Secure Boot {YesNo(sys.SecureBoot)} · TPM {YesNo(sys.Tpm)}");
                Row(h, "VBS / HVCI", $"VBS {(sys.VbsStatus switch { 2 => "in esecuzione", 1 => "abilitato", 0 => "disattivo", _ => "?" })} · integrità memoria {YesNo(sys.HvciRunning)}");
                Row(h, "File di paging", $"{sys.PageFile}{(sys.PageFileAllocatedMb is { } mb ? $" · {F1(mb / 1024.0)} GB allocati" : "")} · memoria impegnata {F1(sys.CommitUsedGb)} / {F1(sys.CommitLimitGb)} GB");
                Row(h, "Piano energetico", sys.PowerPlan);
                h.Append("</tbody></table>");
            }
            if (d.Checks.Count > 0)
            {
                h.Append("<h3>Controlli</h3><table><thead><tr><th>Esito</th><th>Controllo</th><th>Dettagli</th></tr></thead><tbody>");
                foreach (var c in d.Checks.OrderByDescending(c => (int)c.Status))
                    h.Append($"<tr><td><span class=\"badge {SevClass(c.Status)}\">{E(SevText(c.Status))}</span></td><td>{E(c.Title)}</td><td>{E(c.Message)}" +
                             $"{(string.IsNullOrWhiteSpace(c.Hint) ? "" : $"<div class=\"muted small\">{E(c.Hint)}</div>")}</td></tr>");
                h.Append("</tbody></table>");
            }
            h.Append("</section>\n");
        }

        private static void AppendFortnite(StringBuilder h, ReportFortnite f, LogFindings? log)
        {
            h.Append("<section><h2>Fortnite</h2>");
            h.Append("<table class=\"kv\"><tbody>");
            Row(h, "Installazione", f.Installed ? $"trovata{(f.DiskType != null ? " · " + f.DiskType : "")}{(f.FreeGb is { } g ? $" · {F0(g)} GB liberi" : "")}" : "non trovata");
            if (f.GameBuild != null) Row(h, "Versione del gioco (dal log)", f.GameBuild);
            if (f.Settings is { } s)
            {
                Row(h, "Modalità di rendering", s.RenderMode);
                if (s.PreferredRhi.Length > 0 || s.PreferredFeatureLevel.Length > 0)
                    Row(h, "Preferenza nel file ini", $"PreferredRHI={(s.PreferredRhi.Length > 0 ? s.PreferredRhi : "–")} · PreferredFeatureLevel={(s.PreferredFeatureLevel.Length > 0 ? s.PreferredFeatureLevel : "–")}");
            }
            if (f.RhiInUse != null) Row(h, "API grafica usata (dal log)", RhiText(f.RhiInUse));
            else if (log is { Found: true }) Row(h, "API grafica usata (dal log)", "non trovata nel log");
            if (f.Settings is { } st)
            {
                Row(h, "Limite FPS", st.FpsCap > 0 ? F0(st.FpsCap) : "illimitato");
                Row(h, "VSync", OnOff(st.VSync));
                Row(h, "NVIDIA Reflex", ReflexText(st.Reflex));
                Row(h, "Modalità finestra", WindowModeText(st.WindowMode));
                Row(h, "Risoluzione", st.ResolutionX > 0 ? $"{st.ResolutionX}×{st.ResolutionY}" : "–");
                Row(h, "Ray tracing", OnOff(st.RayTracing));
                Row(h, "Nanite", OnOff(st.Nanite));
                Row(h, "Motion blur", OnOff(st.MotionBlur));
                foreach (var kv in st.Scalability) Row(h, "Qualità · " + ShortSg(kv.Key), kv.Value);
            }
            else Row(h, "Impostazioni", f.ConfigFound ? "non leggibili" : "GameUserSettings.ini non trovato (avvia Fortnite almeno una volta)");
            h.Append("</tbody></table></section>\n");
        }

        /// <summary>"D3D11 · ES3_1" → con la spiegazione (modalità Prestazioni = Direct3D 11 a feature level ridotto).</summary>
        public static string RhiText(string rhi)
        {
            if (rhi.Contains("ES3_1", StringComparison.OrdinalIgnoreCase))
                return rhi + " (Direct3D 11 a feature level ridotto: è la modalità Prestazioni)";
            if (rhi.StartsWith("D3D12", StringComparison.OrdinalIgnoreCase)) return rhi + " (DirectX 12)";
            return rhi;
        }

        private static void AppendLog(StringBuilder h, LogFindings? log)
        {
            h.Append("<section><h2>Log di Fortnite</h2>");
            if (log == null || !log.Found)
            {
                h.Append($"<p class=\"muted\">{E(log?.Note ?? "Log di Fortnite non analizzato.")}</p></section>\n");
                return;
            }
            h.Append("<p class=\"muted small\">Fortnite scrive migliaia di avvisi ed errori anche quando funziona tutto: sono rumore normale. " +
                     "Contano i segnali specifici (crash, rete, hitch, memoria) e solo quelli del periodo della sessione diventano consigli; il resto del log è contesto.</p>");
            h.Append("<table class=\"kv\"><tbody>");
            Row(h, "File", string.Join(", ", log.Sources) + (log.Truncated ? " (solo la parte più recente)" : ""));
            if (log.FirstTime != null)
                Row(h, "Periodo del log", $"{log.FirstTime.Value.ToString("dd/MM/yyyy HH:mm:ss", Inv)} → {log.LastTime?.ToString("dd/MM/yyyy HH:mm:ss", Inv)} UTC{LogSpanDuration(log)}");
            if (log.WindowStart != null)
                Row(h, "Periodo della sessione", $"{log.WindowStart.Value.ToString("dd/MM/yyyy HH:mm:ss", Inv)} → {log.WindowEnd?.ToString("HH:mm:ss", Inv)} UTC (inizio e fine della sessione ± 60 s; gli orari del log di Unreal sono in UTC)");
            if (log.GameBuild != null) Row(h, "Versione del gioco", log.GameBuild);
            if (log.RhiInUse != null) Row(h, "API grafica usata", RhiText(log.RhiInUse));
            Row(h, "Righe", $"{log.TotalLines} lette · {log.ParsedLines} riconosciute · {log.SkippedPrivateLines} saltate per privacy (chat, party, amici)");
            Row(h, "Server visti", log.ServerAddresses.Count == 0 ? "–" : string.Join(", ", log.ServerAddresses));
            if (log.Note != null) Row(h, "Nota", log.Note);
            h.Append("</tbody></table>");

            var ses = log.Session;
            h.Append("<table><thead><tr><th>Segnale</th>");
            if (ses != null) h.Append("<th class=\"n\">Durante la sessione</th>");
            h.Append("<th class=\"n\">Intero log (contesto)</th></tr></thead><tbody>");
            void Cmp(string label, Func<LogCounts, string> sesVal, string all)
            {
                h.Append("<tr><td>").Append(E(label)).Append("</td>");
                if (ses != null) h.Append("<td class=\"n\">").Append(E(sesVal(ses))).Append("</td>");
                h.Append("<td class=\"n\">").Append(E(all)).Append("</td></tr>");
            }
            if (ses != null) Cmp("Righe nel periodo", c => c.Lines == 0 ? "0 (il log non copre la sessione)" : c.Lines.ToString(Inv), log.TotalLines.ToString(Inv));
            Cmp("Segnali di crash", c => c.CrashMarkers == 0 ? "nessuno" : string.Join(", ", c.CrashKinds.Select(k => $"{k.Key} ×{k.Value}")),
                log.CrashMarkers == 0 ? "nessuno" : string.Join(", ", log.CrashKinds.Select(k => $"{k.Key} ×{k.Value}")));
            Cmp("Problemi di rete", c => c.NetworkIssues == 0 ? "nessuno" : $"{c.NetworkIssues} ({string.Join(", ", c.NetworkKeywords.Select(k => $"{k.Key} ×{k.Value}"))})",
                log.NetworkIssues == 0 ? "nessuno" : $"{log.NetworkIssues} ({string.Join(", ", log.NetworkKeywords.Select(k => $"{k.Key} ×{k.Value}"))})");
            Cmp("Hitch", c => c.Hitches.ToString(Inv), log.Hitches.ToString(Inv));
            Cmp("Messaggi shader/PSO", c => c.ShaderMessages.ToString(Inv), log.ShaderMessages.ToString(Inv));
            Cmp("Avvisi di memoria", c => c.MemoryWarnings.ToString(Inv), log.MemoryWarnings.ToString(Inv));
            Cmp("Avvisi / errori (rumore normale)", c => $"{c.Warnings} / {c.Errors}", $"{log.Warnings} / {log.Errors}");
            h.Append("</tbody></table>");

            if (log.GpuInfo.Count > 0)
            {
                h.Append("<details><summary>Scheda video vista dal gioco (RHI)</summary><pre>");
                foreach (var g in log.GpuInfo) h.Append(E(g)).Append('\n');
                h.Append("</pre></details>");
            }
            if (log.TopCategories.Count > 0)
            {
                h.Append("<details><summary>Categorie con più avvisi ed errori (intero log)</summary><table><thead><tr><th>Categoria</th><th class=\"n\">Avvisi</th><th class=\"n\">Errori</th></tr></thead><tbody>");
                foreach (var c in log.TopCategories)
                    h.Append($"<tr><td>{E(c.Category)}</td><td class=\"n\">{c.Warnings}</td><td class=\"n\">{c.Errors}</td></tr>");
                h.Append("</tbody></table></details>");
            }
            if (log.Highlights.Count > 0)
            {
                h.Append($"<details><summary>Righe più rilevanti ({log.Highlights.Count}{(ses != null ? ", prima quelle della sessione" : "")})</summary><pre>");
                foreach (var hl in log.Highlights)
                    h.Append('[').Append(E(hl.Kind)).Append(hl.Count > 1 ? $" ×{hl.Count}" : "")
                     .Append(ses != null ? (hl.InSession ? " · sessione" : " · fuori sessione") : "").Append("] ").Append(E(hl.Text)).Append('\n');
                h.Append("</pre></details>");
            }
            h.Append("</section>\n");
        }

        // ---- piccoli helper HTML ----

        private static void Tile(StringBuilder h, string label, string value, string unit, CheckStatus? grade)
        {
            h.Append($"<div class=\"tile{(grade is { } g ? " " + SevClass(g) : "")}\"><div class=\"tl\">{E(label)}</div><div class=\"tv\">{E(value)}<small> {E(unit)}</small></div></div>");
        }

        private static void Row(StringBuilder h, string k, string? v) =>
            h.Append("<tr><th>").Append(E(k)).Append("</th><td>").Append(E(v ?? "")).Append("</td></tr>");

        private static CheckStatus Grade(double ratio, double ok, double warn) =>
            ratio >= ok ? CheckStatus.Ok : ratio >= warn ? CheckStatus.Warn : CheckStatus.Bad;

        private static CheckStatus GradeLow(double v, double ok, double warn) =>
            v <= ok ? CheckStatus.Ok : v <= warn ? CheckStatus.Warn : CheckStatus.Bad;

        /// <summary>Escape HTML di tutto il testo (anche negli attributi).</summary>
        public static string E(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 16);
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&#39;"); break;
                    default: sb.Append(ch); break;
                }
            }
            return sb.ToString();
        }

        private const string Accent = "#7C5CFF", Accent2 = "#00D1FF", Ok = "#3FD07A", Warn = "#F2B84B", Bad = "#FF5D6C";

        private const string Css = @":root{--bg:#0D1017;--surface:#151A24;--surface2:#1C2230;--line:#262E3E;--text:#E8EDF5;--muted:#8D98AD;--accent:#7C5CFF;--accent2:#00D1FF;--ok:#3FD07A;--warn:#F2B84B;--bad:#FF5D6C}
*{box-sizing:border-box}
html{-webkit-text-size-adjust:100%}
body{margin:0;background:var(--bg);color:var(--text);font:15px/1.5 'Segoe UI Variable Text','Segoe UI',system-ui,-apple-system,sans-serif}
main{max-width:1080px;margin:0 auto;padding:24px 16px 48px}
h1{margin:4px 0 6px;font-size:28px}
h2{font-size:20px;margin:0 0 12px;padding-bottom:6px;border-bottom:1px solid var(--line)}
h3{font-size:16px;margin:18px 0 8px}
header,section,footer{background:var(--surface);border:1px solid var(--line);border-radius:14px;padding:18px 20px;margin:0 0 16px}
.brand{font-weight:700;letter-spacing:.08em;text-transform:uppercase;font-size:12px;background:linear-gradient(90deg,var(--accent),var(--accent2));-webkit-background-clip:text;background-clip:text;color:transparent}
.muted{color:var(--muted)}.small{font-size:12.5px}.ok{color:var(--ok)}
.privacy{margin-top:12px;padding:10px 12px;border-radius:10px;background:var(--surface2);border-left:3px solid var(--accent2);font-size:13.5px}
.notes{margin:12px 0 0;padding-left:20px;color:var(--warn);font-size:13.5px}
.tiles{display:grid;grid-template-columns:repeat(auto-fill,minmax(140px,1fr));gap:10px}
.tile{background:var(--surface2);border:1px solid var(--line);border-radius:12px;padding:10px 12px;border-top:3px solid var(--accent)}
.tile.s-ok{border-top-color:var(--ok)}.tile.s-warn{border-top-color:var(--warn)}.tile.s-bad{border-top-color:var(--bad)}.tile.s-info{border-top-color:var(--accent2)}
.tl{color:var(--muted);font-size:12.5px}.tv{font-size:24px;font-weight:650;font-variant-numeric:tabular-nums}.tv small{font-size:12px;color:var(--muted);font-weight:400}
.recs{margin:0;padding-left:22px}
.rec{margin:0 0 12px;padding:10px 12px;background:var(--surface2);border-radius:10px;border-left:3px solid var(--line)}
.rec.s-bad{border-left-color:var(--bad)}.rec.s-warn{border-left-color:var(--warn)}.rec.s-info{border-left-color:var(--accent2)}
.rec-head{display:flex;flex-wrap:wrap;gap:8px;align-items:center;margin-bottom:6px}
dl{margin:0;display:grid;grid-template-columns:max-content 1fr;gap:4px 12px}dt{color:var(--muted);font-size:13px}dd{margin:0}
.badge{display:inline-block;font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:.04em;padding:2px 7px;border-radius:999px;margin-right:6px;background:var(--line);color:var(--text)}
.badge.s-ok{background:rgba(63,208,122,.18);color:var(--ok)}.badge.s-warn{background:rgba(242,184,75,.18);color:var(--warn)}.badge.s-bad{background:rgba(255,93,108,.18);color:var(--bad)}.badge.s-info{background:rgba(0,209,255,.15);color:var(--accent2)}
.insights{list-style:none;margin:0;padding:0}.insights li{padding:8px 0;border-bottom:1px solid var(--line)}.insights li:last-child{border-bottom:0}
.tw{overflow-x:auto;-webkit-overflow-scrolling:touch;margin:6px 0 10px}
table{width:100%;border-collapse:collapse;font-size:13.5px}
th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line);vertical-align:top}
thead th{color:var(--muted);font-weight:600}
.kv th{width:32%;color:var(--muted);font-weight:500}
td.n,th.n{text-align:right;font-variant-numeric:tabular-nums;white-space:nowrap}
tr.sel td{background:rgba(124,92,255,.12)}
td.t-on{color:var(--ok)}td.t-part{color:var(--warn)}td.t-na{color:var(--muted)}
figure{margin:0 0 18px}figcaption{margin-bottom:6px}
svg{width:100%;height:auto;display:block;background:var(--surface2);border-radius:10px}
svg text{fill:var(--muted);font:11px 'Segoe UI',system-ui,sans-serif}
svg .grid{stroke:var(--line);stroke-width:1}
.legend{display:flex;flex-wrap:wrap;gap:6px 14px;font-size:12.5px;margin-top:6px}
.legend i{display:inline-block;width:12px;height:3px;border-radius:2px;margin-right:6px;vertical-align:middle}
.legend i.dash{height:0;border-top:2px dashed;background:none}
.legend i.band{height:10px;width:14px;background:rgba(141,152,173,.28)}
.verdict{margin:0 0 12px;padding:10px 12px;border-radius:10px;background:var(--surface2);border-left:3px solid var(--line)}
.verdict.s-ok{border-left-color:var(--ok)}.verdict.s-warn{border-left-color:var(--warn)}
details{margin:8px 0;background:var(--surface2);border-radius:10px;padding:8px 12px}
summary{cursor:pointer;font-weight:600}
pre{white-space:pre-wrap;word-break:break-word;font:12px/1.45 Consolas,'Cascadia Mono',monospace;margin:8px 0 0}
footer ul{padding-left:20px}
@media (max-width:600px){dl{grid-template-columns:1fr}.kv th{width:40%}h1{font-size:23px}}
@media print{:root{--bg:#fff;--surface:#fff;--surface2:#f4f5f8;--line:#d5d9e2;--text:#111;--muted:#555}body{font-size:12px}header,section,footer{break-inside:avoid-page;border-color:#ccc}main{max-width:none;padding:0}.brand{color:#7C5CFF}}
";

        // ---- formattazione ----

        private static string F0(double? v) => v is { } x && double.IsFinite(x) ? Math.Round(x).ToString("0", Inv) : "–";
        private static string F1(double? v) => v is { } x && double.IsFinite(x) ? x.ToString("0.#", Inv).Replace('.', ',') : "–";
        private static string Ms(double? v) => v is { } x && double.IsFinite(x) ? F1(x) + " ms" : "–";
        private static string YesNo(bool? b) => b == null ? "?" : b.Value ? "sì" : "no";
        private static string OnOff(bool b) => b ? "attivo" : "disattivo";
        private static string Plural(int n, string one, string many) => $"{n.ToString(Inv)} {(n == 1 ? one : many)}";
        private static string Clip(string? s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        public static string Duration(double sec) =>
            !double.IsFinite(sec) || sec < 0 ? "–" :
            sec >= 3600 ? TimeSpan.FromSeconds(sec).ToString(@"h\:mm\:ss", Inv) : TimeSpan.FromSeconds(sec).ToString(@"m\:ss", Inv);

        public static string ReflexText(int r) => r switch { 1 => "attivo", 2 => "attivo + boost", _ => "disattivo" };
        public static string WindowModeText(int m) => m switch { 0 => "schermo intero", 1 => "finestra a schermo intero", 2 => "finestra", _ => $"modalità {m}" };
        private static string ShortSg(string key) => key.Replace("sg.", "").Replace("Quality", "");

        private static string DisplayText(ReportDisplay m) =>
            $"{m.Width}×{m.Height} @ {m.CurrentHz} Hz{(m.MaxHz > m.CurrentHz ? $" (supporta {m.MaxHz} Hz)" : "")}{(m.Primary ? " · principale" : "")}";

        public static string SevText(CheckStatus s) => s switch
        {
            CheckStatus.Bad => "Problema",
            CheckStatus.Warn => "Attenzione",
            CheckStatus.Info => "Info",
            _ => "OK"
        };

        /// <summary>Come SevText, ma "Facoltativo" per i miglioramenti facoltativi.</summary>
        public static string SevText(Recommendation r) => r.Optional ? "Facoltativo" : SevText(r.Severity);

        private static string SevClass(CheckStatus s) => s switch
        {
            CheckStatus.Bad => "s-bad",
            CheckStatus.Warn => "s-warn",
            CheckStatus.Info => "s-info",
            _ => "s-ok"
        };

        private static string ImpactText(int i) => i >= 3 ? "alto" : i == 2 ? "medio" : "basso";

        private static string TweakStateText(string s) => s switch
        {
            "Applied" => "attivo",
            "NotApplied" => "non attivo",
            "Partial" => "parziale",
            "NotApplicable" => "non applicabile",
            _ => "sconosciuto"
        };

        private static string TweakClass(string s) => s switch { "Applied" => "t-on", "Partial" => "t-part", "NotApplicable" => "t-na", _ => "" };

        internal static double NiceStep(double raw)
        {
            if (!(raw > 0) || !double.IsFinite(raw)) return 1;
            double p = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double f = raw / p;
            return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * p;
        }
    }

    /// <summary>Grafici SVG minimali (linee e barre), con decimazione min/max che conserva i picchi.</summary>
    public static class SvgChart
    {
        public sealed record Series(string Name, string Color, double?[] Y, double Width = 1.8);
        public sealed record Ref(double Value, string Label, string Color);

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public const int MaxPoints = 600;
        private const int W = 920, H = 260, L = 48, R = 14, T = 14, B = 26;

        private static string N(double v) => v.ToString("0.#", Inv);

        /// <summary>Grafico a linee: xs in secondi; i null interrompono la linea; bands = tratti [da, a] (secondi) in grigio.</summary>
        public static string Line(IReadOnlyList<double> xs, IList<Series> series, IList<Ref>? refs = null, double? yMaxFixed = null,
            IList<(double From, double To)>? bands = null)
        {
            refs ??= new List<Ref>();
            var sb = new StringBuilder();
            sb.Append($"<svg viewBox=\"0 0 {W} {H}\" role=\"img\" xmlns=\"http://www.w3.org/2000/svg\">");
            int n = xs.Count;
            if (n < 2)
            {
                sb.Append($"<text x=\"{W / 2}\" y=\"{H / 2}\" text-anchor=\"middle\">Nessun dato</text></svg>");
                return sb.ToString();
            }
            double x0 = xs[0], x1 = xs[n - 1];
            if (!(x1 > x0)) x1 = x0 + 1;
            double yMax = yMaxFixed ?? 0;
            if (yMaxFixed == null)
            {
                foreach (var s in series)
                    foreach (var v in s.Y)
                        if (v is { } y && double.IsFinite(y)) yMax = Math.Max(yMax, y);
                foreach (var r in refs) if (double.IsFinite(r.Value)) yMax = Math.Max(yMax, r.Value);
                yMax = yMax <= 0 ? 1 : yMax * 1.08;
            }
            double step = ReportBuilder.NiceStep(yMax / 5);
            yMax = Math.Ceiling(yMax / step) * step;

            double X(double x) => L + (x - x0) / (x1 - x0) * (W - L - R);
            double Y(double y) => T + (1 - Math.Clamp(y / yMax, 0, 1.02)) * (H - T - B);

            // griglia e asse Y
            for (double v = 0; v <= yMax + step / 2; v += step)
            {
                var y = Y(v);
                sb.Append($"<line class=\"grid\" x1=\"{L}\" x2=\"{W - R}\" y1=\"{N(y)}\" y2=\"{N(y)}\"/>");
                sb.Append($"<text x=\"{L - 6}\" y=\"{N(y + 4)}\" text-anchor=\"end\">{N(v)}</text>");
            }
            // bande (es. gioco fuori fuoco), sotto a tutto il resto
            foreach (var (from, to) in bands ?? Array.Empty<(double, double)>())
            {
                double a = Math.Clamp(from, x0, x1), b = Math.Clamp(to, x0, x1);
                if (!(b > a)) continue;
                sb.Append($"<rect x=\"{N(X(a))}\" y=\"{T}\" width=\"{N(Math.Max(1, X(b) - X(a)))}\" height=\"{H - T - B}\" fill=\"#8D98AD\" opacity=\".18\"><title>gioco fuori fuoco</title></rect>");
            }
            // asse X (tempo)
            double span = x1 - x0;
            double xStep = new[] { 10.0, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200 }.FirstOrDefault(s => span / s <= 8);
            if (xStep <= 0) xStep = Math.Ceiling(span / 8 / 3600) * 3600;
            for (double v = Math.Ceiling(x0 / xStep) * xStep; v <= x1 + 0.001; v += xStep)
                sb.Append($"<text x=\"{N(X(v))}\" y=\"{H - 8}\" text-anchor=\"middle\">{TimeLabel(v)}</text>");

            // riferimenti (cap, refresh)
            foreach (var r in refs.Where(r => double.IsFinite(r.Value) && r.Value <= yMax))
            {
                var y = Y(r.Value);
                sb.Append($"<line x1=\"{L}\" x2=\"{W - R}\" y1=\"{N(y)}\" y2=\"{N(y)}\" stroke=\"{r.Color}\" stroke-width=\"1\" stroke-dasharray=\"5 4\" opacity=\".8\"/>");
                sb.Append($"<text x=\"{W - R - 4}\" y=\"{N(y - 4)}\" text-anchor=\"end\" style=\"fill:{r.Color}\">{ReportBuilder.E(r.Label)}</text>");
            }

            foreach (var s in series)
            {
                var path = Path(xs, s.Y, X, Y);
                if (path.Length == 0) continue;
                sb.Append($"<path d=\"{path}\" fill=\"none\" stroke=\"{s.Color}\" stroke-width=\"{N(s.Width)}\" stroke-linejoin=\"round\" vector-effect=\"non-scaling-stroke\"/>");
            }
            sb.Append("</svg>");
            return sb.ToString();
        }

        /// <summary>Indici dei punti da disegnare: tutti se pochi, altrimenti min e max di ogni gruppo (i picchi restano visibili).</summary>
        public static List<int> Decimate(IReadOnlyList<double?> y, int maxPoints = MaxPoints)
        {
            var idx = new List<int>();
            int n = y.Count;
            if (n <= maxPoints)
            {
                for (int i = 0; i < n; i++) idx.Add(i);
                return idx;
            }
            int buckets = Math.Max(1, maxPoints / 2);
            for (int b = 0; b < buckets; b++)
            {
                int from = (int)((long)b * n / buckets), to = (int)((long)(b + 1) * n / buckets);
                int iMin = -1, iMax = -1, iGap = -1;
                for (int i = from; i < to; i++)
                {
                    if (y[i] is not { } v || !double.IsFinite(v))
                    {
                        if (iGap < 0) iGap = i;
                        continue;
                    }
                    if (iMin < 0 || v < y[iMin]!.Value) iMin = i;
                    if (iMax < 0 || v > y[iMax]!.Value) iMax = i;
                }
                // Un valore mancante nel gruppo interrompe la linea (Path salta i null).
                foreach (var i in new[] { iMin, iMax, iGap }.Where(i => i >= 0).Distinct().OrderBy(i => i))
                    idx.Add(i);
            }
            return idx;
        }

        private static string Path(IReadOnlyList<double> xs, double?[] ys, Func<double, double> X, Func<double, double> Y)
        {
            var sb = new StringBuilder();
            bool pen = false;
            foreach (var i in Decimate(ys))
            {
                if (i >= xs.Count) break;
                if (ys[i] is not { } v || !double.IsFinite(v) || !double.IsFinite(xs[i]))
                {
                    pen = false;
                    continue;
                }
                sb.Append(pen ? 'L' : 'M').Append(N(X(xs[i]))).Append(',').Append(N(Y(v)));
                pen = true;
            }
            return sb.ToString();
        }

        /// <summary>Istogramma: Counts[i] tra Edges[i] e Edges[i+1].</summary>
        public static string Bars(IReadOnlyList<double> edges, IReadOnlyList<int> counts, string color)
        {
            var sb = new StringBuilder();
            sb.Append($"<svg viewBox=\"0 0 {W} {H}\" role=\"img\" xmlns=\"http://www.w3.org/2000/svg\">");
            int n = counts.Count;
            int max = n == 0 ? 0 : counts.Max();
            if (n == 0 || max <= 0 || edges.Count < n + 1)
            {
                sb.Append($"<text x=\"{W / 2}\" y=\"{H / 2}\" text-anchor=\"middle\">Nessun dato</text></svg>");
                return sb.ToString();
            }
            double bw = (double)(W - L - R) / n;
            double total = counts.Sum();
            // asse Y in percentuale dei frame
            double maxPct = max / total * 100;
            double step = ReportBuilder.NiceStep(maxPct / 4);
            double top = Math.Ceiling(maxPct / step) * step;
            double Y(double pct) => T + (1 - pct / top) * (H - T - B);
            for (double v = 0; v <= top + step / 2; v += step)
            {
                sb.Append($"<line class=\"grid\" x1=\"{L}\" x2=\"{W - R}\" y1=\"{N(Y(v))}\" y2=\"{N(Y(v))}\"/>");
                sb.Append($"<text x=\"{L - 6}\" y=\"{N(Y(v) + 4)}\" text-anchor=\"end\">{N(v)}%</text>");
            }
            int labelEvery = Math.Max(1, (int)Math.Ceiling(n / 10.0));
            for (int i = 0; i < n; i++)
            {
                double pct = counts[i] / total * 100;
                double x = L + i * bw;
                double y = Y(pct);
                if (counts[i] > 0)
                    sb.Append($"<rect x=\"{N(x + 1)}\" y=\"{N(y)}\" width=\"{N(Math.Max(1, bw - 2))}\" height=\"{N(H - B - y)}\" fill=\"{color}\" rx=\"2\"><title>{N(edges[i])}–{N(edges[i + 1])} ms: {counts[i]} frame</title></rect>");
                if (i % labelEvery == 0)
                    sb.Append($"<text x=\"{N(x)}\" y=\"{H - 8}\" text-anchor=\"middle\">{N(edges[i])}</text>");
            }
            sb.Append($"<text x=\"{W - R}\" y=\"{H - 8}\" text-anchor=\"end\">ms</text>");
            sb.Append("</svg>");
            return sb.ToString();
        }

        private static string TimeLabel(double sec)
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, sec));
            return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss", Inv) : ts.ToString(@"m\:ss", Inv);
        }
    }
}
