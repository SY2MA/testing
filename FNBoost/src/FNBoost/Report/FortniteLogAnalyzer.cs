using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// Questo file non dipende da WPF né da API di Windows: viene compilato anche dal progetto di test su Linux.

namespace FNBoost.Report
{
    /// <summary>Conteggio di avvisi ed errori di una categoria del log.</summary>
    public sealed class LogCategoryCount
    {
        public string Category { get; set; } = "";
        public int Warnings { get; set; }
        public int Errors { get; set; }
        public int Total => Warnings + Errors;
    }

    /// <summary>Una riga significativa del log (già ripulita dai dati personali).</summary>
    public sealed class LogHighlight
    {
        /// <summary>"crash", "memoria", "errore", "rete", "hitch", "gpu", "shader", "avviso".</summary>
        public string Kind { get; set; } = "";
        public string Text { get; set; } = "";
        /// <summary>Quante righe uguali (a parte i numeri) sono state raggruppate in questa.</summary>
        public int Count { get; set; } = 1;
    }

    /// <summary>Risultato dell'analisi del log di Fortnite (FortniteGame.log).</summary>
    public sealed class LogFindings
    {
        public bool Found { get; set; }
        /// <summary>Nomi dei file letti (solo nome, mai il percorso).</summary>
        public List<string> Sources { get; set; } = new();
        /// <summary>Spiegazione quando il log manca o non è leggibile.</summary>
        public string? Note { get; set; }
        /// <summary>Il file era più grande del limite: sono state analizzate solo le righe più recenti.</summary>
        public bool Truncated { get; set; }
        public int TotalLines { get; set; }
        /// <summary>Righe nel formato Unreal riconosciuto ("Categoria: Livello: messaggio").</summary>
        public int ParsedLines { get; set; }
        /// <summary>Righe di chat/party/amici saltate per privacy.</summary>
        public int SkippedPrivateLines { get; set; }
        public DateTime? FirstTime { get; set; }
        public DateTime? LastTime { get; set; }
        public string? GameBuild { get; set; }
        public int Warnings { get; set; }
        public int Errors { get; set; }
        /// <summary>Prime 15 categorie per numero di avvisi + errori.</summary>
        public List<LogCategoryCount> TopCategories { get; set; } = new();
        /// <summary>Righe di rete (LogNet, LogNetTraffic, LogOnlineGame, LogMatchmaking*) con parole chiave di problemi.</summary>
        public int NetworkIssues { get; set; }
        public Dictionary<string, int> NetworkKeywords { get; set; } = new();
        /// <summary>Server di gioco (IP:porta) visti nelle righe LogNet.</summary>
        public List<string> ServerAddresses { get; set; } = new();
        public int Hitches { get; set; }
        public int ShaderMessages { get; set; }
        /// <summary>Righe informative su GPU/RHI (scheda, driver, feature level).</summary>
        public List<string> GpuInfo { get; set; } = new();
        public int CrashMarkers { get; set; }
        public Dictionary<string, int> CrashKinds { get; set; } = new();
        public int MemoryWarnings { get; set; }
        /// <summary>Fino a 80 righe, dalla più rilevante, ciascuna al massimo 220 caratteri.</summary>
        public List<LogHighlight> Highlights { get; set; } = new();
    }

    /// <summary>
    /// Analizza il log che Fortnite scrive per l'utente (%LOCALAPPDATA%\FortniteGame\Saved\Logs).
    /// Solo lettura di un file su disco, condiviso con il gioco: nessun accesso al processo.
    /// Le righe di chat, party e amici vengono saltate del tutto; le altre passano dal <see cref="Sanitizer"/>.
    /// </summary>
    public static class FortniteLogAnalyzer
    {
        public const int MaxHighlights = 80;
        public const int MaxHighlightChars = 220;
        public const long MaxBytes = 40L * 1024 * 1024;
        public const int MaxLines = 400_000;

        private const RegexOptions Opt = RegexOptions.CultureInvariant | RegexOptions.Compiled;

        // [2026.10.06-21.09.08:123][ 42]LogNet: Warning: messaggio
        private static readonly Regex Timed = new(
            @"^\[(\d{4})\.(\d{2})\.(\d{2})-(\d{2})\.(\d{2})\.(\d{2}):(\d{3})\]\[\s*\d+\]([A-Za-z][A-Za-z0-9_]*):\s?(.*)$", Opt);
        // Righe iniziali senza orario: "LogInit: Display: ..."
        private static readonly Regex Untimed = new(@"^(Log[A-Za-z0-9_]*):\s?(.*)$", Opt);
        private static readonly Regex Verbosity = new(@"^(Display|Log|Warning|Error|Fatal|Verbose|VeryVerbose):\s?(.*)$", Opt);

        private static readonly Regex Endpoint = new(@"(?<![\w.])(\d{1,3}(?:\.\d{1,3}){3}):(\d{2,5})(?!\d)", Opt);
        private static readonly Regex Pso = new(@"\bPSO|PipelineState|ShaderCompile", Opt);
        private static readonly Regex Digits = new(@"\d+", Opt);

        /// <summary>Categorie con chat, party, amici, presenza: righe saltate per non includere nomi di giocatori o messaggi.</summary>
        private static readonly string[] PrivateCategoryParts =
            { "Chat", "Party", "Xmpp", "Friend", "Presence", "Social", "Whisper" };

        private static readonly string[] NetCategories = { "LogNet", "LogNetTraffic", "LogOnlineGame" };

        private static readonly (string Key, string Needle, bool IgnoreCase)[] NetKeywords =
        {
            ("timeout", "timeout", true),
            ("connection lost", "Connection lost", true),
            ("packet loss", "packet loss", true),
            ("saturated", "Saturated", true),
            ("out of order", "Out of order", true),
            // Non "UNetConnection::Close:": Unreal lo scrive a ogni uscita normale da una partita.
            ("network failure", "NetworkFailure", true),
            ("pending connection failure", "PendingConnectionFailure", true)
        };

        private static readonly string[] CrashNeedles =
            { "Fatal error", "Assertion failed", "Unhandled Exception", "GPU crashed", "DXGI_ERROR_DEVICE_REMOVED", "D3D Device Lost" };

        private static readonly string[] MemoryNeedles = { "Out of memory", "OutOfMemory", "low memory" };

        private static readonly string[] RhiCategories = { "LogD3D12RHI", "LogD3D11RHI", "LogRHI" };
        private static readonly string[] RhiNeedles = { "adapter", "driver", "feature level", "Using Default RHI", "Chosen" };

        // ================= Lettura file =================

        /// <summary>
        /// Analizza FortniteGame.log e (se c'è) il backup più recente della sessione precedente,
        /// utile quando il gioco è andato in crash. Non lancia eccezioni: in caso di problemi lo dice in Note.
        /// </summary>
        public static LogFindings AnalyzeDirectory(string? logsDir, SanitizeContext ctx, bool includeBackup = true)
        {
            try
            {
                if (string.IsNullOrEmpty(logsDir) || !Directory.Exists(logsDir))
                    return new LogFindings { Note = "Cartella dei log di Fortnite non trovata (il gioco non è mai stato avviato su questo account Windows?)." };

                var main = Path.Combine(logsDir, "FortniteGame.log");
                string? backup = null;
                if (includeBackup)
                {
                    backup = new DirectoryInfo(logsDir).GetFiles("FortniteGame-backup-*.log")
                        .OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.FullName).FirstOrDefault();
                }

                var lines = new List<string>();
                var sources = new List<string>();
                bool truncated = false;
                var notes = new List<string>();

                // Budget: 80% al log corrente, 20% al backup (in ordine cronologico: prima il backup).
                if (backup != null)
                {
                    var (b, t, err) = ReadTail(backup, MaxBytes / 5, MaxLines / 5);
                    if (err == null)
                    {
                        lines.AddRange(b);
                        sources.Add(Path.GetFileName(backup));
                        truncated |= t;
                    }
                    else notes.Add($"{Path.GetFileName(backup)}: {err}");
                }
                if (File.Exists(main))
                {
                    var (m, t, err) = ReadTail(main, MaxBytes - MaxBytes / 5, MaxLines - MaxLines / 5);
                    if (err == null)
                    {
                        lines.AddRange(m);
                        sources.Add(Path.GetFileName(main));
                        truncated |= t;
                    }
                    else notes.Add($"FortniteGame.log: {err}");
                }
                else notes.Add("FortniteGame.log non trovato.");

                if (sources.Count == 0)
                    return new LogFindings { Note = string.Join(" ", notes) };

                var f = Analyze(lines, ctx);
                f.Sources = sources;
                f.Truncated = truncated;
                if (notes.Count > 0) f.Note = string.Join(" ", notes);
                return f;
            }
            catch (Exception ex)
            {
                return new LogFindings { Note = "Lettura del log di Fortnite non riuscita: " + Sanitizer.Sanitize(ex.Message, ctx) };
            }
        }

        /// <summary>
        /// Legge le ultime righe di un file anche se il gioco lo sta scrivendo (FileShare.ReadWrite | Delete).
        /// Oltre maxBytes legge solo la coda; tiene al massimo maxLines righe.
        /// </summary>
        public static (List<string> Lines, bool Truncated, string? Error) ReadTail(string path, long maxBytes = MaxBytes, int maxLines = MaxLines)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                bool truncated = false;
                long len = fs.Length;
                if (len > maxBytes)
                {
                    fs.Seek(len - maxBytes, SeekOrigin.Begin);
                    truncated = true;
                }
                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: !truncated);
                if (truncated) reader.ReadLine(); // riga tagliata a metà
                var queue = new Queue<string>();
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    queue.Enqueue(line);
                    if (queue.Count > maxLines)
                    {
                        queue.Dequeue();
                        truncated = true;
                    }
                }
                return (queue.ToList(), truncated, null);
            }
            catch (FileNotFoundException)
            {
                return (new List<string>(), false, "file non trovato");
            }
            catch (IOException ex)
            {
                return (new List<string>(), false, "file non leggibile in questo momento (" + ex.GetType().Name + ")");
            }
            catch (UnauthorizedAccessException)
            {
                return (new List<string>(), false, "accesso negato");
            }
        }

        // ================= Analisi =================

        private sealed class Candidate
        {
            public int Score;
            public int Order;
            public string Kind = "";
            public string Text = "";
            public int Count;
        }

        public static LogFindings Analyze(IEnumerable<string> lines, SanitizeContext ctx)
        {
            ctx ??= new SanitizeContext();
            var f = new LogFindings { Found = true };
            var cats = new Dictionary<string, LogCategoryCount>(StringComparer.Ordinal);
            var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
            var servers = new List<string>();
            var gpu = new List<string>();
            bool skipContinuation = false;
            int order = 0;

            foreach (var raw in lines ?? Array.Empty<string>())
            {
                if (raw == null) continue;
                f.TotalLines++;
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;

                string? category = null;
                string verbosity = "";
                string message = line;
                DateTime? time = null;

                var m = Timed.Match(line);
                if (m.Success)
                {
                    time = TryTime(m);
                    category = m.Groups[8].Value;
                    message = m.Groups[9].Value;
                }
                else
                {
                    var u = Untimed.Match(line);
                    if (u.Success)
                    {
                        category = u.Groups[1].Value;
                        message = u.Groups[2].Value;
                    }
                }
                if (category != null)
                {
                    var v = Verbosity.Match(message);
                    if (v.Success)
                    {
                        verbosity = v.Groups[1].Value;
                        message = v.Groups[2].Value;
                    }
                }

                // Privacy: chat, party, amici → riga saltata (e anche le righe di continuazione che la seguono).
                if (category != null)
                {
                    skipContinuation = IsPrivateCategory(category);
                    if (skipContinuation)
                    {
                        f.SkippedPrivateLines++;
                        continue;
                    }
                    f.ParsedLines++;
                }
                else if (skipContinuation)
                {
                    f.SkippedPrivateLines++;
                    continue;
                }

                if (time != null)
                {
                    f.FirstTime ??= time;
                    f.LastTime = time;
                }

                bool isWarn = verbosity == "Warning";
                bool isErr = verbosity == "Error" || verbosity == "Fatal";
                if (category != null && (isWarn || isErr))
                {
                    if (!cats.TryGetValue(category, out var c)) cats[category] = c = new LogCategoryCount { Category = category };
                    if (isWarn) { c.Warnings++; f.Warnings++; }
                    else { c.Errors++; f.Errors++; }
                }

                if (f.GameBuild == null && category == "LogInit" &&
                    (message.StartsWith("Build: ", StringComparison.Ordinal) || message.StartsWith("Engine Version: ", StringComparison.Ordinal)))
                    f.GameBuild = Truncate(message.Substring(message.IndexOf(':') + 1).Trim(), 120);

                // ---- classificazione ----
                string? kind = null;
                int score = 0;

                // Una riga di crash conta una volta, ma registra tutti i tipi che nomina (es. "Fatal error" + "GPU crashed").
                bool crashLine = false;
                foreach (var needle in CrashNeedles)
                {
                    if (!line.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
                    f.CrashKinds[needle] = f.CrashKinds.TryGetValue(needle, out var n) ? n + 1 : 1;
                    crashLine = true;
                }
                if (crashLine)
                {
                    f.CrashMarkers++;
                    kind = "crash"; score = 100;
                }

                if (MemoryNeedles.Any(n => line.Contains(n, StringComparison.OrdinalIgnoreCase)))
                {
                    f.MemoryWarnings++;
                    if (kind == null) { kind = "memoria"; score = 80; }
                }

                if (category != null && IsNetCategory(category))
                {
                    bool any = false;
                    foreach (var (kw, needle, ic) in NetKeywords)
                    {
                        if (!message.Contains(needle, ic ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
                        f.NetworkKeywords[kw] = f.NetworkKeywords.TryGetValue(kw, out var n) ? n + 1 : 1;
                        any = true;
                    }
                    if (any)
                    {
                        f.NetworkIssues++;
                        if (kind == null) { kind = "rete"; score = 55; }
                    }
                    if (category == "LogNet") CollectServers(message, servers);
                }

                if (line.Contains("hitch", StringComparison.OrdinalIgnoreCase))
                {
                    f.Hitches++;
                    if (kind == null) { kind = "hitch"; score = 45; }
                }

                if (Pso.IsMatch(line) || line.Contains("shader", StringComparison.OrdinalIgnoreCase))
                {
                    f.ShaderMessages++;
                    if (kind == null && (isWarn || isErr)) { kind = "shader"; score = 30; }
                }

                if (category != null && RhiCategories.Contains(category) &&
                    RhiNeedles.Any(n => message.Contains(n, StringComparison.OrdinalIgnoreCase)))
                {
                    if (gpu.Count < 20)
                    {
                        var g = Truncate(Sanitizer.Sanitize($"{category}: {message}", ctx), MaxHighlightChars);
                        if (!gpu.Contains(g)) gpu.Add(g);
                    }
                    if (kind == null) { kind = "gpu"; score = 35; }
                }

                if (kind == null && isErr) { kind = "errore"; score = 60; }
                if (kind == null && isWarn) { kind = "avviso"; score = 10; }
                if (kind != null && isErr && score < 60) score = 60;
                if (kind == null) continue;

                // Raggruppa righe uguali a parte i numeri (es. 500 hitch identici).
                var key = kind + "|" + category + "|" + Digits.Replace(message, "#");
                if (candidates.TryGetValue(key, out var existing))
                {
                    existing.Count++;
                    continue;
                }
                if (candidates.Count >= 5000 && score < 45) continue; // limite di memoria sui log enormi
                var shown = time is { } t ? $"[{t:yyyy.MM.dd HH:mm:ss}] " : "";
                shown += category != null
                    ? $"{category}: {(verbosity.Length > 0 && verbosity != "Display" && verbosity != "Log" ? verbosity + ": " : "")}{message}"
                    : line.Trim();
                candidates[key] = new Candidate { Score = score, Order = order++, Kind = kind, Text = shown, Count = 1 };
            }

            f.TopCategories = cats.Values
                .OrderByDescending(c => c.Total).ThenBy(c => c.Category, StringComparer.Ordinal)
                .Take(15).ToList();
            f.ServerAddresses = servers;
            f.GpuInfo = gpu;

            // I server di gioco visti nel log possono restare in chiaro nelle righe evidenziate.
            var hctx = ctx.With(servers);
            f.Highlights = candidates.Values
                .OrderByDescending(c => c.Score).ThenByDescending(c => c.Count > 1 ? 1 : 0).ThenBy(c => c.Order)
                .Take(MaxHighlights)
                .Select(c => new LogHighlight
                {
                    Kind = c.Kind,
                    Count = c.Count,
                    Text = Truncate(Sanitizer.Sanitize(c.Text, hctx), MaxHighlightChars)
                })
                .ToList();
            if (f.TotalLines == 0) f.Note = "Il log è vuoto.";
            return f;
        }

        /// <summary>Testo leggibile delle righe evidenziate (per fortnite-log-highlights.txt).</summary>
        public static string FormatHighlights(LogFindings f)
        {
            var sb = new StringBuilder();
            foreach (var h in f.Highlights)
                sb.Append('[').Append(h.Kind).Append(h.Count > 1 ? $" ×{h.Count}" : "").Append("] ").AppendLine(h.Text);
            return sb.ToString();
        }

        // ---- interni ----

        public static bool IsPrivateCategory(string category) =>
            PrivateCategoryParts.Any(p => category.Contains(p, StringComparison.OrdinalIgnoreCase));

        private static bool IsNetCategory(string category) =>
            NetCategories.Contains(category, StringComparer.Ordinal) ||
            category.StartsWith("LogMatchmaking", StringComparison.Ordinal);

        private static void CollectServers(string message, List<string> servers)
        {
            // Le righe che parlano dell'indirizzo locale (bind, local) non sono server.
            if (message.Contains("local", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("bind", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("client address", StringComparison.OrdinalIgnoreCase)) return;
            foreach (Match m in Endpoint.Matches(message))
            {
                if (servers.Count >= 10) return;
                if (!System.Net.IPAddress.TryParse(m.Groups[1].Value, out var ip) || !Sanitizer.IsPublic(ip)) continue;
                if (!int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port <= 0 || port > 65535) continue;
                var ep = $"{Sanitizer.Normalize(ip)}:{port}";
                if (!servers.Contains(ep)) servers.Add(ep);
            }
        }

        private static DateTime? TryTime(Match m)
        {
            try
            {
                int G(int i) => int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
                return new DateTime(G(1), G(2), G(3), G(4), G(5), G(6), G(7), DateTimeKind.Unspecified);
            }
            catch
            {
                return null;
            }
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }
}
