using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using FNBoost.Core;
using FNBoost.Perf;

namespace FNBoost.Report
{
    /// <summary>
    /// Raccoglie i dati dall'app in esecuzione e crea il report diagnostico da condividere
    /// (ZIP con HTML, JSON, riassunto, frametime e log ripuliti). Tutto il lavoro pesante gira in background.
    /// </summary>
    public static class ReportService
    {
        /// <summary>Endpoint ufficiali Epic per il ping (articolo di supporto "Fortnite latency and ping troubleshooting").</summary>
        public static readonly string[] EpicPingHosts =
        {
            "ping-nae.ds.on.epicgames.com", "ping-nac.ds.on.epicgames.com", "ping-naw.ds.on.epicgames.com",
            "ping-eu.ds.on.epicgames.com", "ping-oce.ds.on.epicgames.com", "ping-br.ds.on.epicgames.com",
            "ping-asia.ds.on.epicgames.com", "ping-me.ds.on.epicgames.com"
        };

        private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

        /// <summary>Documenti\FN Boost\Report</summary>
        public static string DefaultFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FN Boost", "Report");

        /// <summary>
        /// Crea lo ZIP e restituisce il percorso completo. session = sessione selezionata (null = l'ultima salvata, se c'è).
        /// Cartella predefinita: <see cref="DefaultFolder"/> (creata se manca).
        /// </summary>
        public static async Task<string> ExportZipAsync(PerfSession? session, string? destinationFolder = null)
        {
            var input = CaptureInputs(session);
            try
            {
                return await Task.Run(async () =>
                {
                    var bundle = await CollectAsync(input, loadFrametimes: true).ConfigureAwait(false);
                    var folder = string.IsNullOrWhiteSpace(destinationFolder) ? DefaultFolder : destinationFolder!;
                    Directory.CreateDirectory(folder);
                    var path = WriteZip(bundle, folder);
                    Log.Info($"Report diagnostico creato: {Path.GetFileName(path)} ({new FileInfo(path).Length / 1024.0:0} KB).");
                    return path;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Creazione del report diagnostico non riuscita", ex);
                throw;
            }
        }

        /// <summary>Riassunto di testo (al massimo ~4000 caratteri) da incollare in una chat con un assistente.</summary>
        public static async Task<string> BuildSummaryTextAsync(PerfSession? session)
        {
            var input = CaptureInputs(session);
            try
            {
                return await Task.Run(async () =>
                {
                    var bundle = await CollectAsync(input, loadFrametimes: false).ConfigureAwait(false);
                    return Guard(ReportBuilder.BuildSummaryText(bundle.Data), bundle.Context, "summary");
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Creazione del riassunto del report non riuscita", ex);
                throw;
            }
        }

        // ================= Raccolta =================

        /// <summary>Quello che va letto sul thread chiamante (di solito la UI) prima di passare in background.</summary>
        private sealed class Inputs
        {
            public PerfSession? Session;
            public PerfService? Perf;
            public NetworkSnapshot? LiveNet;
            public List<Tweak> Tweaks = new();
            public double StutterFactor = 2.5;
            public double StutterMinMs = 12;
        }

        private sealed class Bundle
        {
            public ReportData Data = new();
            public SanitizeContext Context = new();
            public float[]? Frametimes;
            /// <summary>Frame esclusi dalle statistiche (gioco fuori fuoco), per la colonna "focused" del CSV.</summary>
            public bool[]? ExcludedMask;
            public string FnBoostLog = "";
            public string LogHighlights = "";
        }

        private static Inputs CaptureInputs(PerfSession? session)
        {
            var i = new Inputs { Session = session };
            try
            {
                i.Perf = App.Perf;
                i.LiveNet = i.Perf?.Live?.Net;
                if (i.Perf?.Settings is { } ps)
                {
                    i.StutterFactor = ps.StutterFactor;
                    i.StutterMinMs = ps.StutterMinMs;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Report: stato del modulo Prestazioni non leggibile: " + ex.Message);
            }
            try
            {
                if (App.Tweaks?.Tweaks is { } t) i.Tweaks = t.ToList();
            }
            catch (Exception ex)
            {
                Log.Warn("Report: elenco tweak non leggibile: " + ex.Message);
            }
            return i;
        }

        private static async Task<Bundle> CollectAsync(Inputs input, bool loadFrametimes)
        {
            var b = new Bundle();
            var d = b.Data;
            d.GeneratedAt = DateTime.Now;
            d.AppVersion = App.Version;

            // ---- sessioni ----
            PerfSessionStore? store = input.Perf?.Store;
            if (input.Perf == null)
            {
                d.Notes.Add("Il modulo Prestazioni non è attivo in questa esecuzione di FN Boost (vedi il registro): " +
                            "sono incluse solo le sessioni già salvate, senza lo stato dal vivo.");
                try
                {
                    store = new PerfSessionStore(PerfSessionStore.DefaultDirectory, 100);
                }
                catch (Exception ex)
                {
                    Log.Warn("Report: archivio sessioni non leggibile: " + ex.Message);
                }
            }
            IReadOnlyList<PerfSession> history = Array.Empty<PerfSession>();
            try
            {
                history = store?.List() ?? Array.Empty<PerfSession>();
            }
            catch (Exception ex)
            {
                Log.Warn("Report: elenco sessioni non leggibile: " + ex.Message);
            }
            var session = input.Session ?? history.FirstOrDefault();
            if (session == null) d.Notes.Add("Nessuna sessione di gioco registrata: il report contiene solo sistema, impostazioni e log.");

            float[]? ft = null;
            if (session != null && store != null)
            {
                try
                {
                    ft = store.LoadFrametimes(session.Id);
                }
                catch (Exception ex)
                {
                    Log.Warn("Report: frametime non leggibili: " + ex.Message);
                }
                if (ft == null || ft.Length < 2)
                {
                    ft = null;
                    d.Notes.Add("I frametime della sessione non sono disponibili: niente istogramma né frametimes.csv.");
                }
            }

            // ---- gioco fuori fuoco ----
            // Sessioni registrate prima della misura del primo piano: se all'inizio/alla fine c'è un tratto a ~30 FPS con la
            // GPU quasi ferma (Fortnite in secondo piano), statistiche, grafici e analisi usano una copia corretta.
            if (session != null && ft != null)
            {
                try
                {
                    var repaired = FocusFilter.RepairLegacy(session, ft, input.StutterFactor > 1 ? input.StutterFactor : 2.5, Math.Max(0, input.StutterMinMs));
                    if (repaired != null)
                    {
                        session = repaired;
                        d.Notes.Add($"Sessione registrata prima che FN Boost controllasse il primo piano: {repaired.UnfocusedSec:0} s a ~30 FPS con la GPU quasi ferma " +
                                    "all'inizio/alla fine (probabile gioco in secondo piano) sono stati esclusi dalle statistiche. È una stima.");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("Report: correzione del tratto in secondo piano non riuscita: " + ex.Message);
                }
            }
            if (session is { UnfocusedSec: >= 1, UnfocusedEstimated: false })
                d.Notes.Add($"{session.UnfocusedSec:0} s con il gioco non in primo piano (Fortnite in secondo piano scende da solo a ~30 FPS) " +
                            "sono esclusi da statistiche, stutter e grafici; nel file frametimes.csv quei frame hanno focused = 0.");
            var excludedMask = ft != null ? FocusFilter.MaskFromRanges(ft.Length, session?.ExcludedRanges) : null;

            // ---- contesto privacy ----
            var ctx = new SanitizeContext(Environment.UserName, Environment.MachineName);
            try
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(profile)) ctx.AddIdentifier(Path.GetFileName(profile.TrimEnd('\\', '/')));
                if (!string.Equals(Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                    ctx.AddIdentifier(Environment.UserDomainName);
            }
            catch { /* identificativi extra non disponibili */ }
            ctx.Allow("1.1.1.1");
            if (session?.Network?.ServerEndpoints != null) ctx.AllowRange(session.Network.ServerEndpoints);
            if (input.LiveNet?.ServerEndpoint != null) ctx.Allow(input.LiveNet.ServerEndpoint);
            foreach (var p in new[] { session?.Network?.Game, session?.Network?.Region, input.LiveNet?.Game, input.LiveNet?.Region })
                if (p != null && !string.IsNullOrEmpty(p.Host)) AllowIfPublic(ctx, p.Host);
            foreach (var ip in await ResolveEpicEndpointsAsync().ConfigureAwait(false)) ctx.Allow(ip);
            b.Context = ctx;

            // ---- sistema e tweak ----
            try
            {
                var snap = SystemDiagnostics.Collect(input.Tweaks);
                d.System = MapSystem(snap);
                d.Checks = snap.Checks.Select(c => new ReportCheck { Title = c.Title, Status = c.Status, Message = c.Message, Hint = c.Hint }).ToList();
                d.Fortnite.Installed = snap.FortniteDir != null;
                d.Fortnite.DiskType = snap.FortniteDiskType;
                d.Fortnite.FreeGb = snap.FortniteDriveFreeGb;
            }
            catch (Exception ex)
            {
                Log.Warn("Report: diagnostica di sistema non riuscita: " + ex.Message);
                d.Notes.Add("La diagnostica di sistema non è riuscita: le informazioni sull'hardware sono incomplete.");
            }
            d.Tweaks = input.Tweaks.Select(t => new ReportTweak
            {
                Id = t.Id,
                Title = t.Title,
                Category = t.Category,
                State = t.State.ToString(),
                Recommended = t.Recommended,
                Impact = t.Impact.ToString()
            }).ToList();

            // ---- Fortnite ----
            try
            {
                d.Fortnite.ConfigFound = FortniteConfigService.ConfigExists;
                if (d.Fortnite.ConfigFound) d.Fortnite.Settings = MapSettings(FortniteSettings.Read(FortniteLocator.ConfigFile));
            }
            catch (Exception ex)
            {
                Log.Warn("Report: impostazioni di Fortnite non leggibili: " + ex.Message);
            }

            // ---- sessione e analisi ----
            if (session != null)
            {
                d.Session = session;
                try
                {
                    d.Insights = PerfAnalyzer.Analyze(session, history, ft);
                }
                catch (Exception ex)
                {
                    Log.Warn("Report: analisi della sessione non riuscita: " + ex.Message);
                }
                if (ft != null) d.Histogram = ReportBuilder.BuildHistogram(FocusFilter.Keep(ft, excludedMask));
            }
            try
            {
                d.Trend = PerfAnalyzer.Trend(history);
            }
            catch (Exception ex)
            {
                Log.Warn("Report: andamento non calcolabile: " + ex.Message);
            }
            // La sessione analizzata compare con i suoi numeri effettivi (anche se corretta per il tratto in secondo piano).
            var recent = history.Take(10).Select(h => session != null && h.Id == session.Id ? session : h).ToList();
            if (session != null && recent.All(h => h.Id != session.Id)) recent.Insert(0, session);
            d.PreviousSessions = recent.Select(h => ReportBuilder.Summarize(h, session != null && h.Id == session.Id)).ToList();

            if (session?.Network == null && input.LiveNet != null) d.LiveNetwork = input.LiveNet;
            if (session?.Network == null && input.LiveNet == null && input.Perf != null)
                d.Notes.Add("Nessun dato di rete: la misura di rete è disattivata o la sessione è stata registrata prima di questa funzione.");

            // ---- log di Fortnite ----
            // Solo le righe del periodo della sessione (± 60 s) diventano consigli; il resto del log è contesto.
            // Gli orari del log di Unreal sono in UTC, StartedAt è l'ora locale: SessionWindowUtc converte.
            DateTime? wStart = null, wEnd = null;
            if (session != null && session.StartedAt != default)
                (wStart, wEnd) = FortniteLogAnalyzer.SessionWindowUtc(session.StartedAt, session.DurationSec);
            d.Log = FortniteLogAnalyzer.AnalyzeDirectory(Path.Combine(FortniteLocator.SavedDir, "Logs"), ctx, true, wStart, wEnd);
            d.Fortnite.RhiInUse = d.Log?.RhiInUse;
            d.Fortnite.GameBuild = d.Log?.GameBuild;

            // ---- privacy e raccomandazioni ----
            ReportBuilder.SanitizeInPlace(d, ctx);
            d.Recommendations = ReportBuilder.BuildRecommendations(d);

            b.Frametimes = loadFrametimes ? ft : null;
            b.ExcludedMask = loadFrametimes ? excludedMask : null;
            b.LogHighlights = BuildLogHighlightsText(d.Log);
            b.FnBoostLog = string.Join(Environment.NewLine, Log.Tail(300).Select(l => Sanitizer.Sanitize(l, ctx)));
            return b;
        }

        private static void AllowIfPublic(SanitizeContext ctx, string host)
        {
            var ip = Sanitizer.ExtractIp(host);
            if (ip != null && IPAddress.TryParse(ip, out var a) && Sanitizer.IsPublic(a)) ctx.Allow(ip);
        }

        private static async Task<List<string>> ResolveEpicEndpointsAsync()
        {
            var result = new List<string>();
            try
            {
                var tasks = EpicPingHosts.Select(h => Dns.GetHostAddressesAsync(h)).ToList();
                var all = Task.WhenAll(tasks);
                await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
                foreach (var t in tasks.Where(t => t.Status == TaskStatus.RanToCompletion))
                    result.AddRange(t.Result.Select(Sanitizer.Normalize));
                // Le eccezioni dei task non completati non devono restare "non osservate".
                foreach (var t in tasks) _ = t.ContinueWith(x => _ = x.Exception, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Risoluzione endpoint Epic: " + ex.Message);
            }
            return result;
        }

        private static ReportSystem MapSystem(SystemSnapshot s)
        {
            string pageFile;
            if (s.PagingFiles.Length == 0) pageFile = "disattivato";
            else if (PageFileTweak.IsSystemManaged(s.PagingFiles)) pageFile = "gestito da Windows";
            else pageFile = PageFileTweak.FixedMaxMb(s.PagingFiles) is { } mb ? $"fisso, massimo {mb / 1024.0:0.#} GB" : string.Join("; ", s.PagingFiles);

            return new ReportSystem
            {
                Os = s.Os,
                Cpu = s.Cpu,
                Cores = s.Cores,
                Threads = s.Threads,
                RamTotalGb = Math.Round(s.RamTotalGb, 1),
                RamType = s.RamType,
                RamConfiguredMts = s.RamConfiguredMts,
                RamRatedMts = s.RamRatedMts,
                RamModules = s.RamModules,
                Gpus = s.Gpus.Select(g => new ReportGpu { Name = g.Name, Vendor = g.Vendor, DriverVersion = g.DriverVersion, DriverDate = g.DriverDate }).ToList(),
                Displays = s.Displays.Select(m => new ReportDisplay { Width = m.Width, Height = m.Height, CurrentHz = m.CurrentHz, MaxHz = m.MaxHz, Primary = m.Primary }).ToList(),
                Board = s.Board,
                BiosVersion = s.BiosVersion,
                BiosDate = s.BiosDate,
                SecureBoot = s.SecureBoot,
                Tpm = s.Tpm,
                VbsStatus = s.VbsStatus,
                HvciRunning = s.HvciRunning,
                PageFile = pageFile,
                PageFileAllocatedMb = s.PageFileAllocatedMb,
                CommitUsedGb = Math.Round(s.CommitUsedGb, 1),
                CommitLimitGb = Math.Round(s.CommitLimitGb, 1),
                PowerPlan = s.PowerPlan
            };
        }

        private static FortniteSettingsSummary MapSettings(FortniteSettings s) => new()
        {
            RenderMode = s.RenderMode switch
            {
                RenderMode.Performance => "Prestazioni",
                RenderMode.DirectX12 => "DirectX 12",
                _ => string.IsNullOrWhiteSpace(s.RawRhi.Trim('/')) ? "predefinita" : s.RawRhi
            },
            FpsCap = s.FrameRateLimit,
            VSync = s.VSync,
            Reflex = s.Reflex,
            WindowMode = s.WindowMode,
            ResolutionX = s.ResolutionX,
            ResolutionY = s.ResolutionY,
            Scalability = new Dictionary<string, string>(s.Scalability),
            RayTracing = s.RayTracing,
            Nanite = s.Nanite,
            MotionBlur = s.MotionBlur,
            PreferredRhi = s.PreferredRhi,
            PreferredFeatureLevel = s.PreferredFeatureLevel
        };

        // ================= ZIP =================

        private static string WriteZip(Bundle b, string folder)
        {
            var d = b.Data;
            var ctx = b.Context;
            var baseName = $"FNBoost-Report-{d.GeneratedAt:yyyyMMdd-HHmmss}";
            var path = Path.Combine(folder, baseName + ".zip");
            for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{baseName}-{n}.zip");

            var tmp = path + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    AddText(zip, "report.html", Guard(ReportBuilder.BuildHtml(d), ctx, "report.html"));
                    AddText(zip, "report.json", Guard(ReportBuilder.BuildJson(d), ctx, "report.json"));
                    AddText(zip, "summary.txt", Guard(ReportBuilder.BuildSummaryText(d), ctx, "summary.txt"));
                    if (b.Frametimes is { Length: >= 2 } ft)
                    {
                        // Solo numeri: nessun dato personale possibile.
                        var entry = zip.CreateEntry("frametimes.csv", CompressionLevel.Optimal);
                        using var w = new StreamWriter(entry.Open(), Utf8, 1 << 16);
                        ReportBuilder.WriteFrametimesCsv(w, ft, b.ExcludedMask);
                    }
                    AddText(zip, "fortnite-log-highlights.txt", Guard(b.LogHighlights, ctx, "fortnite-log-highlights.txt"));
                    AddText(zip, "fnboost-log.txt", Guard(b.FnBoostLog, ctx, "fnboost-log.txt"));
                    AddText(zip, "README.txt", Guard(Readme(d, b.Frametimes != null), ctx, "README.txt"));
                }
                File.Move(tmp, path);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
                throw;
            }
            return path;
        }

        private static void AddText(ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(entry.Open(), Utf8);
            w.Write(content.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        }

        /// <summary>
        /// Ultima rete di sicurezza: il nome utente (e il nome del PC) non devono comparire in nessun file.
        /// Se succede, è un bug dei filtri: si registra e si rimuove comunque.
        /// </summary>
        private static string Guard(string text, SanitizeContext ctx, string what)
        {
            if (!Sanitizer.ContainsIdentity(text, ctx)) return text;
            Debug.Fail("Identità rimasta nel report: " + what);
            Log.Warn($"Report: rimossi dati personali rimasti in {what}.");
            return Sanitizer.ScrubIdentity(text, ctx);
        }

        private static string BuildLogHighlightsText(LogFindings? log)
        {
            var sb = new StringBuilder();
            sb.AppendLine("FN Boost · righe rilevanti del log di Fortnite (ripulite da nomi, account e IP privati; chat/party/amici esclusi)");
            sb.AppendLine();
            if (log == null || !log.Found)
            {
                sb.AppendLine(log?.Note ?? "Log di Fortnite non analizzato.");
                return sb.ToString();
            }
            sb.AppendLine($"File: {string.Join(", ", log.Sources)}{(log.Truncated ? " (solo la parte più recente)" : "")}");
            if (log.FirstTime != null) sb.AppendLine($"Periodo del log (UTC): {log.FirstTime:yyyy-MM-dd HH:mm:ss} → {log.LastTime:yyyy-MM-dd HH:mm:ss}");
            if (log.GameBuild != null) sb.AppendLine("Versione del gioco: " + log.GameBuild);
            if (log.RhiInUse != null) sb.AppendLine("API grafica usata dal gioco: " + log.RhiInUse);
            if (log.Session is { } ses && log.WindowStart != null)
            {
                sb.AppendLine($"Sessione analizzata (UTC, ±60 s): {log.WindowStart:yyyy-MM-dd HH:mm:ss} → {log.WindowEnd:HH:mm:ss}");
                sb.AppendLine($"Durante la sessione: righe {ses.Lines} · avvisi {ses.Warnings} · errori {ses.Errors} · crash {ses.CrashMarkers} · rete {ses.NetworkIssues} · hitch {ses.Hitches} · shader/PSO {ses.ShaderMessages} · memoria {ses.MemoryWarnings}");
                if (ses.Lines == 0) sb.AppendLine("  (il log letto non contiene righe di quel periodo: la sessione è più vecchia del log o il gioco è stato riavviato)");
            }
            sb.AppendLine($"Intero log (contesto): righe {log.TotalLines} · avvisi {log.Warnings} · errori {log.Errors} · crash {log.CrashMarkers} · rete {log.NetworkIssues} · hitch {log.Hitches} · shader/PSO {log.ShaderMessages} · memoria {log.MemoryWarnings} · saltate per privacy {log.SkippedPrivateLines}");
            sb.AppendLine("Nota: Fortnite scrive migliaia di avvisi ed errori anche quando funziona tutto: contano solo i segnali specifici (crash, rete, hitch) del periodo della sessione.");
            if (log.ServerAddresses.Count > 0) sb.AppendLine("Server visti: " + string.Join(", ", log.ServerAddresses));
            if (log.Note != null) sb.AppendLine("Nota: " + log.Note);
            if (log.TopCategories.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Categorie con più avvisi/errori (intero log):");
                foreach (var c in log.TopCategories) sb.AppendLine($"  {c.Category}: {c.Warnings} avvisi, {c.Errors} errori");
            }
            if (log.GpuInfo.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Scheda video vista dal gioco:");
                foreach (var g in log.GpuInfo) sb.AppendLine("  " + g);
            }
            sb.AppendLine();
            sb.AppendLine(log.Session != null
                ? "Righe più rilevanti (prima quelle della sessione, poi il resto del log come contesto):"
                : "Righe più rilevanti (dalla più importante):");
            sb.Append(FortniteLogAnalyzer.FormatHighlights(log));
            return sb.ToString();
        }

        private static string Readme(ReportData d, bool hasFrametimes)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"FN Boost {d.AppVersion} · Report diagnostico del {d.GeneratedAt:dd/MM/yyyy HH:mm}");
            sb.AppendLine();
            sb.AppendLine("COSA CONTIENE");
            sb.AppendLine("  report.html                  Il report da leggere: apri con un browser (anche da telefono). Riepilogo, \"Cosa non va / Cosa migliorare\", grafici, sistema, impostazioni, rete, log.");
            sb.AppendLine("  report.json                  Gli stessi dati in formato strutturato (schema " + d.SchemaVersion + "), comodo per un assistente AI o per il supporto.");
            sb.AppendLine("  summary.txt                  Riassunto breve da incollare direttamente in una chat.");
            if (hasFrametimes)
                sb.AppendLine("  frametimes.csv               Tutti i frametime della sessione (index, time_ms, frametime_ms, fps, focused) per analisi dettagliate.\n" +
                              "                               focused = 0: gioco non in primo piano (Fortnite scende da solo a ~30 FPS), escluso dalle statistiche.");
            sb.AppendLine("  fortnite-log-highlights.txt  Le righe più significative del log di Fortnite (errori, rete, hitch, shader, crash).");
            sb.AppendLine("  fnboost-log.txt              Le ultime righe del registro di FN Boost (tweak applicati, errori dell'app).");
            sb.AppendLine();
            sb.AppendLine("PRIVACY");
            sb.AppendLine("  Prima di essere scritti, tutti i file sono stati ripuliti: nome utente di Windows e percorsi del profilo,");
            sb.AppendLine("  nome del PC, email, ID account Epic, token nelle URL, indirizzi IP della rete di casa e il tuo IP pubblico");
            sb.AppendLine("  sono sostituiti da segnaposto (es. <utente>, <pc>, <ip-locale>, <ip>). Le righe di chat, party e amici del log");
            sb.AppendLine("  di Fortnite non vengono mai incluse. Restano gli indirizzi dei server di gioco e degli endpoint Epic,");
            sb.AppendLine("  utili per capire problemi di connessione. Il report è stato creato sul tuo PC e non è stato inviato a nessuno.");
            sb.AppendLine("  Se vuoi, puoi controllare tu stesso il contenuto: sono normali file di testo.");
            sb.AppendLine();
            sb.AppendLine("COME CONDIVIDERLO");
            sb.AppendLine("  - Con un assistente AI: incolla il contenuto di summary.txt e, se puoi allegare file, aggiungi report.json");
            sb.AppendLine("    (o tutto lo ZIP). Chiedi ad esempio: \"Cosa limita le prestazioni di Fortnite sul mio PC e cosa mi conviene cambiare?\".");
            sb.AppendLine("  - Con una persona del supporto o un amico: invia lo ZIP così com'è; report.html si apre con un doppio clic.");
            sb.AppendLine();
            sb.AppendLine("COME SONO MISURATI I DATI");
            sb.AppendLine("  FPS e frametime arrivano dagli eventi ETW Present di Windows (come PresentMon e Xbox Game Bar), senza toccare il gioco.");
            sb.AppendLine("  I frame presentati mentre il gioco non era la finestra in primo piano (più ~0,5 s di assestamento al ritorno)");
            sb.AppendLine("  non contano nelle statistiche: in secondo piano Fortnite si limita da solo a ~30 FPS.");
            sb.AppendLine("  Il log di Fortnite è analizzato per il periodo della sessione (±60 s, orari UTC); il resto del log è solo contesto.");
            sb.AppendLine("  Il ping è un ping ICMP inviato da FN Boost: può differire di qualche ms da quello mostrato in Fortnite.");
            return sb.ToString();
        }
    }
}
