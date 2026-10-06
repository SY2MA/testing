using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using FNBoost.Core;
using FNBoost.Perf;
using FNBoost.Report;

namespace FNBoost.Tests
{
    /// <summary>Test del report diagnostico: privacy, analisi del log di Fortnite, raccomandazioni e file generati.</summary>
    internal static class ReportTests
    {
        private const string User = "mario";
        private const string Pc = "GAMING-RIG";

        public static void RunAll()
        {
            Console.WriteLine("Sanitizer");
            T.Run("percorsi del profilo (\\, \\\\ e /), cartelle di sistema intatte", SanitizePaths);
            T.Run("nome utente e nome del PC come parole intere, nomi generici ignorati", SanitizeNames);
            T.Run("email, id Epic a 32 cifre, token nelle URL, GUID, Bearer, nomi visualizzati", SanitizeIdsAndTokens);
            T.Run("IPv4 locali, pubblici e ammessi; porte conservate", SanitizeIpv4);
            T.Run("IPv6, orari, versioni e nomi C++ non scambiati per IP", SanitizeIpv6AndFalsePositives);
            T.Run("deterministico e idempotente; ContainsIdentity / ScrubIdentity", SanitizeDeterministic);
            T.Run("profilo con spazi a fine percorso (nome e cognome)", SanitizeProfileWithSpaces);

            Console.WriteLine("FortniteLogAnalyzer");
            T.Run("conteggi, categorie, server, hitch, shader, crash, memoria", LogCounts);
            T.Run("chat/party/amici saltati, righe evidenziate ripulite e ordinate", LogPrivacyAndHighlights);
            T.Run("chiusure normali della connessione non contano, i fallimenti sì", LogNetCloseVsFailure);
            T.Run("formati sconosciuti e input vuoto", LogRobustness);
            T.Run("lettura della coda del file, backup, cartella mancante", LogFiles);

            Console.WriteLine("ReportBuilder");
            T.Run("raccomandazioni: ordine per gravità e impatto, doppioni uniti", RecommendationsOrderDedupe);
            T.Run("titoli simili", SimilarTitlesCases);
            T.Run("SanitizeInPlace non modifica la sessione originale", SanitizeInPlaceCopies);
            T.Run("BuildJson: JSON valido, camelCase, enum come stringhe, NaN", JsonOutput);
            T.Run("BuildHtml: sezioni, grafici SVG, escaping, nessun nome utente", HtmlOutput);
            T.Run("BuildSummaryText: sezioni e limite di 4000 caratteri", SummaryOutput);
            T.Run("istogramma, CSV dei frametime, decimazione dei grafici", FrametimeHelpers);
        }

        private static SanitizeContext Ctx() =>
            new SanitizeContext(User, Pc, new[] { "1.1.1.1", "34.1.2.3:7777" }).AddIdentifier("mario_000");

        // ================= Sanitizer =================

        private static void SanitizePaths()
        {
            var c = Ctx();
            T.Equal(@"%USERPROFILE%\AppData\Local\FortniteGame\Saved",
                Sanitizer.Sanitize(@"C:\Users\mario\AppData\Local\FortniteGame\Saved", c), "percorso Windows");
            T.Equal(@"apro %USERPROFILE%\\Documents\\x.ini ora",
                Sanitizer.Sanitize(@"apro C:\\Users\\Mario Rossi\\Documents\\x.ini ora", c), "percorso con spazi e \\\\ (JSON)");
            T.Equal("%USERPROFILE%/Desktop", Sanitizer.Sanitize("c:/Users/someone/Desktop", c), "barre in avanti");
            T.Equal("cartella %USERPROFILE% e basta", Sanitizer.Sanitize(@"cartella D:\Users\luigi e basta", c), "profilo a fine percorso");
            T.Equal(@"C:\Users\Public\Documents", Sanitizer.Sanitize(@"C:\Users\Public\Documents", c), "Public intatta");
            T.Equal(@"C:\Users\Default\NTUSER.DAT", Sanitizer.Sanitize(@"C:\Users\Default\NTUSER.DAT", c), "Default intatta");
            T.Equal(@"D:\Epic Games\Fortnite", Sanitizer.Sanitize(@"D:\Epic Games\Fortnite", c), "percorso di gioco intatto");
        }

        private static void SanitizeNames()
        {
            var c = Ctx();
            T.Equal("Utente <utente> su <pc>", Sanitizer.Sanitize("Utente Mario su gaming-rig", c), "utente e PC");
            T.Equal("marioKart resta", Sanitizer.Sanitize("marioKart resta", c), "parola più lunga intatta");
            T.Equal("file <utente>.txt e <utente>", Sanitizer.Sanitize("file mario_000.txt e mario", c), "identificativo extra prima del nome");
            T.Equal("PC <pc> trovato", Sanitizer.Sanitize("PC DESKTOP-AB12CD3 trovato", c), "nome PC predefinito");
            var generic = new SanitizeContext("User", "PC");
            T.Equal("user profile on PC", Sanitizer.Sanitize("user profile on PC", generic), "nomi generici non sostituiti");
            T.True(!Sanitizer.IsMaskable("al"), "nomi di 2 lettere non mascherabili");
            T.True(Sanitizer.IsMaskable("Luca"), "nome normale mascherabile");
        }

        private static void SanitizeIdsAndTokens()
        {
            var c = Ctx();
            T.Equal("scrivi a <email> subito", Sanitizer.Sanitize("scrivi a mario.rossi+fn@gmail.com subito", c), "email");
            T.Equal("account <id> ok", Sanitizer.Sanitize("account 0123456789abcdef0123456789ABCDEF ok", c), "id Epic 32 hex");
            T.Equal("hash 0123456789abcdef0123456789abcdef0", Sanitizer.Sanitize("hash 0123456789abcdef0123456789abcdef0", c), "33 hex non è un id Epic");
            var url = Sanitizer.Sanitize("GET https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token?code=abc123&access_token=eg1~xyz.ABC&grant=1", c);
            T.Contains(url, "code=<rimosso>", "code");
            T.Contains(url, "access_token=<rimosso>", "access_token");
            T.Contains(url, "grant=1", "altri parametri intatti");
            T.True(!url.Contains("abc123") && !url.Contains("eg1~xyz"), "valori rimossi");
            T.Equal("https://x.epicgames.com/p/<id>/stats", Sanitizer.Sanitize("https://x.epicgames.com/p/3f2504e0-4f89-11d3-9a0c-0305e82c3301/stats", c), "GUID nella URL");
            T.Equal("guid fuori 3f2504e0-4f89-11d3-9a0c-0305e82c3301", Sanitizer.Sanitize("guid fuori 3f2504e0-4f89-11d3-9a0c-0305e82c3301", c), "GUID fuori dalle URL intatto");
            T.Equal("Authorization: bearer <rimosso>", Sanitizer.Sanitize("Authorization: bearer eg1~abcdefghijklmnop", c), "Bearer");
            T.Equal("DisplayName: <rimosso>, ok", Sanitizer.Sanitize("DisplayName: xXSniperXx, ok", c), "nome visualizzato");
            T.Equal("AccountId=<rimosso> x", Sanitizer.Sanitize("AccountId=deadbeef x", c), "accountId=");
            T.Equal("Error code: 404", Sanitizer.Sanitize("Error code: 404", c), "\"code:\" con i due punti non toccato");
        }

        private static void SanitizeIpv4()
        {
            var c = Ctx();
            foreach (var ip in new[] { "192.168.1.10", "10.0.0.5", "172.16.0.1", "172.31.255.254", "127.0.0.1", "169.254.3.3", "100.64.0.1" })
                T.Equal($"da {Sanitizer.LocalIpToken} ok", Sanitizer.Sanitize($"da {ip} ok", c), "locale " + ip);
            T.Equal("ip <ip> e <ip>", Sanitizer.Sanitize("ip 172.32.0.1 e 8.8.8.8", c), "pubblici non ammessi");
            T.Equal("dns 1.1.1.1", Sanitizer.Sanitize("dns 1.1.1.1", c), "1.1.1.1 ammesso");
            T.Equal("server 34.1.2.3:7777 e <ip>:7777", Sanitizer.Sanitize("server 34.1.2.3:7777 e 34.1.2.4:7777", c), "server ammesso con porta, altro no");
            T.Equal("mask 255.255.255.0 any 0.0.0.0", Sanitizer.Sanitize("mask 255.255.255.0 any 0.0.0.0", c), "maschere e 0.0.0.0 intatti");
            T.Equal("RemoteAddr: <ip-locale>:5000,", Sanitizer.Sanitize("RemoteAddr: 192.168.0.2:5000,", c), "locale con porta");
            T.True(c.With(new[] { "52.1.1.1:9000" }).IsAllowed(System.Net.IPAddress.Parse("52.1.1.1")), "With aggiunge endpoint");
            T.True(!c.IsAllowed(System.Net.IPAddress.Parse("52.1.1.1")), "With non modifica l'originale");
        }

        private static void SanitizeIpv6AndFalsePositives()
        {
            var c = Ctx();
            T.Equal($"ll {Sanitizer.LocalIpToken} ula {Sanitizer.LocalIpToken} lo {Sanitizer.LocalIpToken}",
                Sanitizer.Sanitize("ll fe80::1c2b:3cff:fe4d:5e6f%12 ula fd12:3456:789a::1 lo ::1", c), "IPv6 locali");
            T.Equal("pub <ip>", Sanitizer.Sanitize("pub 2001:4860:4860::8888", c), "IPv6 pubblico");
            T.Equal("[<ip>]:7777", Sanitizer.Sanitize("[2a01:4f8:1:2::3]:7777", c), "IPv6 tra parentesi");
            var allowed6 = new SanitizeContext(User, Pc, new[] { "[2a01:4f8:1:2::3]:7777" });
            T.Equal("[2a01:4f8:1:2::3]:7777", Sanitizer.Sanitize("[2a01:4f8:1:2::3]:7777", allowed6), "IPv6 ammesso");
            foreach (var keep in new[]
                     {
                         "[2026.10.06-21.09.08:123][ 42]LogNet: ok", "alle 21:09:08", "versione 5.4.3.2.1", "UNetDriver::TickFlush",
                         "Bad::Add", "driver 32.0.15.6094", "AMD 31.0.24033.1003", "mac aa:bb:cc:dd:ee:ff", "v1.2.3.4", "a :: b"
                     })
                T.Equal(keep, Sanitizer.Sanitize(keep, c), "intatto: " + keep);
        }

        private static void SanitizeDeterministic()
        {
            var c = Ctx();
            const string text = @"mario@x.it C:\Users\mario\a 192.168.1.1 8.8.8.8 0123456789abcdef0123456789abcdef GAMING-RIG https://a.b/c?code=1";
            var a = Sanitizer.Sanitize(text, c);
            T.Equal(a, Sanitizer.Sanitize(text, Ctx()), "stesso risultato");
            T.Equal(a, Sanitizer.Sanitize(a, c), "idempotente");
            T.True(!Sanitizer.ContainsIdentity(a, c), "nessuna identità rimasta");
            T.True(Sanitizer.ContainsIdentity("ciao Mario!", c), "ContainsIdentity trova il nome");
            T.True(!Sanitizer.ContainsIdentity("%USERPROFILE% marioKart", c), "ContainsIdentity rispetta i confini di parola");
            T.Equal("ciao <utente>, IP 8.8.8.8", Sanitizer.ScrubIdentity("ciao Mario, IP 8.8.8.8", c), "ScrubIdentity non tocca gli IP");
            T.Equal("", Sanitizer.Sanitize(null, c), "null → vuoto");
        }

        private static void SanitizeProfileWithSpaces()
        {
            var c = new SanitizeContext("Mario Rossi", Pc, Array.Empty<string>());
            const string msg = "Access to the path 'C:\\Users\\Mario Rossi' is denied.";
            var a = Sanitizer.Sanitize(msg, c);
            T.Equal("Access to the path '%USERPROFILE%' is denied.", a, "Sanitize: cartella intera");
            T.True(!a.Contains("Rossi", StringComparison.Ordinal), "nessun cognome");
            var b = Sanitizer.ScrubIdentity(msg, c);
            T.Equal("Access to the path '%USERPROFILE%' is denied.", b, "ScrubIdentity: cartella intera");
            T.Equal("USERPROFILE=%USERPROFILE%", Sanitizer.Sanitize("USERPROFILE=C:/Users/mario rossi", c), "a fine riga, barre in avanti");
            T.Equal(@"%USERPROFILE%\AppData", Sanitizer.Sanitize(@"C:\Users\Mario Rossi\AppData", c), "seguito da altre cartelle");

            // Nome breve noto (cartella "Mario Rossi" ma utente "mario"): la regola generica prende la cartella intera.
            T.Equal(@"apro %USERPROFILE%\Documents ora", Sanitizer.Sanitize(@"apro C:\Users\Mario Rossi\Documents ora", Ctx()), "prefisso del nome");
        }

        // ================= FortniteLogAnalyzer =================

        private static string L(int sec, string body, int frame = 1) =>
            $"[2026.10.06-21.{10 + sec / 60:00}.{sec % 60:00}:{sec * 7 % 1000:000}][{frame,3}]{body}";

        private static List<string> SampleLog() => new()
        {
            "Log file open, 10/06/26 23:09:08",
            "LogInit: Display: Build: ++Fortnite+Release-32.10-CL-38371014",
            "LogInit: Command Line: -epicusername=\"xXSniperXx\" -epicuserid=0123456789abcdef0123456789abcdef",
            L(0, "LogD3D12RHI: Found D3D12 adapter 0: NVIDIA GeForce RTX 4070 (VendorId: 10de, DeviceId: 2786)"),
            L(1, "LogD3D12RHI: Display: Chosen D3D12 Adapter Id = 0"),
            L(1, "LogRHI: Display: Using Default RHI: D3D12"),
            L(2, @"LogInit: Display: Loading C:\Users\mario\AppData\Local\FortniteGame\Saved\Config\WindowsClient\GameUserSettings.ini"),
            L(3, "LogNet: Created socket for bind address: 0.0.0.0 on port 0 local 192.168.1.20:5000"),
            L(4, "LogNet: Browse: 34.1.2.3:7777//Game/Maps/Frontend"),
            L(5, "LogNet: Warning: UNetConnection::Close: [UNetConnection] RemoteAddr: 34.1.2.3:7777, Name: IpConnection_1"),
            L(6, "LogNet: Warning: Connection TIMEOUT! RemoteAddr: 34.1.2.3:7777"),
            L(7, "LogNetTraffic: Warning: Received out of order packet seq 1234"),
            L(8, "LogMatchmakingServiceClient: Error: Matchmaking timeout from 52.9.9.9:443"),
            L(9, "LogFortChat: Display: [Squad] PlayerOne: ciao mario!"),
            L(10, "LogParty: Display: Party member xXSniperXx joined"),
            "    continuation of party line with SecretFriend",
            L(11, "LogXmpp: Verbose: presence from FriendlyGuy"),
            L(12, "LogStreaming: Warning: Detected hitch of 120.5ms during level streaming"),
            L(13, "LogStreaming: Warning: Detected hitch of 95.1ms during level streaming"),
            L(14, "LogRenderer: Warning: PSO cache miss for material M_Foo"),
            L(15, "LogShaderCompilers: Display: ShaderCompile worker started"),
            L(16, "LogWindows: Error: Fatal error: [File:D3D12Util.cpp] GPU crashed or D3D Device Removed. DXGI_ERROR_DEVICE_REMOVED"),
            L(17, "LogMemory: Warning: Low memory warning, available 512 MB"),
            L(18, "LogOnline: Warning: Login failed accountId=0123456789abcdef0123456789abcdef mail mario@example.com host DESKTOP-AB12CD3"),
            L(19, "LogFoo: Error: something at 192.168.1.5 " + new string('x', 400)),
            L(20, "LogHttp: Warning: Request to 8.8.8.8 failed"),
            "garbage line without any known format",
            ""
        };

        private static void LogCounts()
        {
            var f = FortniteLogAnalyzer.Analyze(SampleLog(), Ctx());
            T.True(f.Found, "Found");
            T.Equal(28, f.TotalLines, "TotalLines");
            T.Equal(4, f.SkippedPrivateLines, "righe private saltate (chat, party, continuazione, xmpp)");
            T.Equal(9, f.Warnings, "Warnings");
            T.Equal(3, f.Errors, "Errors");
            T.Equal(new DateTime(2026, 10, 6, 21, 10, 0, 0), f.FirstTime, "FirstTime");
            T.Equal(new DateTime(2026, 10, 6, 21, 10, 20, 140), f.LastTime, "LastTime");
            T.Equal("++Fortnite+Release-32.10-CL-38371014", f.GameBuild, "GameBuild");
            T.Equal("LogNet", f.TopCategories[0].Category, "prima categoria");
            T.Equal(2, f.TopCategories[0].Warnings, "LogNet warnings");
            T.Equal(2, f.TopCategories.First(c => c.Category == "LogStreaming").Warnings, "LogStreaming");
            T.True(f.TopCategories.Count <= 15, "max 15 categorie");
            T.Equal(3, f.NetworkIssues, "righe di rete con problemi (non la chiusura normale della connessione)");
            T.Equal(2, f.NetworkKeywords["timeout"], "timeout (TIMEOUT e timeout)");
            T.True(!f.NetworkKeywords.ContainsKey("close"), "UNetConnection::Close: non è un problema di rete");
            T.Equal(1, f.NetworkKeywords["out of order"], "out of order");
            T.Equal("34.1.2.3:7777", string.Join(",", f.ServerAddresses), "solo il server (non bind/local, non matchmaking)");
            T.Equal(2, f.Hitches, "hitch");
            T.True(f.ShaderMessages >= 2, "shader/PSO");
            T.Equal(1, f.CrashMarkers, "riga di crash");
            T.True(f.CrashKinds.ContainsKey("Fatal error") && f.CrashKinds.ContainsKey("GPU crashed") &&
                   f.CrashKinds.ContainsKey("DXGI_ERROR_DEVICE_REMOVED"), "tutti i tipi di crash della riga");
            T.Equal(1, f.MemoryWarnings, "memoria");
            T.True(f.GpuInfo.Count >= 2, "righe GPU/RHI");
            T.True(f.GpuInfo.Any(g => g.Contains("RTX 4070")), "scheda video nel log");
        }

        private static void LogNetCloseVsFailure()
        {
            var lines = new List<string>();
            for (int i = 0; i < 8; i++)
                lines.Add(L(i, $"LogNet: UNetConnection::Close: [UNetConnection] RemoteAddr: 34.1.2.3:7777, Name: IpConnection_{i}, Driver: GameNetDriver"));
            var ok = FortniteLogAnalyzer.Analyze(lines, Ctx());
            T.Equal(0, ok.NetworkIssues, "8 uscite normali dalle partite → nessun problema di rete");

            lines.Add(L(20, "LogNet: Warning: Network Failure: GameNetDriver[NetworkFailure]: ConnectionLost"));
            lines.Add(L(21, "LogNet: Warning: Network Failure: PendingNetDriver[PendingConnectionFailure]: errore"));
            var bad = FortniteLogAnalyzer.Analyze(lines, Ctx());
            T.Equal(2, bad.NetworkIssues, "fallimenti di rete contati");
            T.Equal(1, bad.NetworkKeywords["network failure"], "NetworkFailure");
            T.Equal(1, bad.NetworkKeywords["pending connection failure"], "PendingConnectionFailure");
        }

        private static void LogPrivacyAndHighlights()
        {
            var f = FortniteLogAnalyzer.Analyze(SampleLog(), Ctx());
            var all = string.Join("\n", f.Highlights.Select(h => h.Text)) + "\n" + string.Join("\n", f.GpuInfo);
            foreach (var bad in new[] { "PlayerOne", "xXSniperXx", "SecretFriend", "FriendlyGuy", "ciao", "0123456789abcdef", "mario", "Mario", "192.168.1.5", "8.8.8.8", "DESKTOP-AB12CD3" })
                T.True(!all.Contains(bad, StringComparison.Ordinal), "non deve comparire: " + bad);
            T.Contains(all, "<email>", "email sostituita");
            T.Contains(all, Sanitizer.LocalIpToken, "IP locale sostituito");
            T.Contains(all, "34.1.2.3:7777", "server di gioco visibile");
            T.True(f.Highlights.Count <= FortniteLogAnalyzer.MaxHighlights, "max 80");
            T.True(f.Highlights.All(h => h.Text.Length <= FortniteLogAnalyzer.MaxHighlightChars), "max 220 caratteri");
            T.Equal("crash", f.Highlights[0].Kind, "prima la riga di crash");
            T.Equal("memoria", f.Highlights[1].Kind, "poi la memoria");
            var hitch = f.Highlights.Single(h => h.Kind == "hitch");
            T.Equal(2, hitch.Count, "hitch identici raggruppati");
            T.True(f.Highlights.Any(h => h.Text.EndsWith("…")), "riga lunga troncata");
            var formatted = FortniteLogAnalyzer.FormatHighlights(f);
            T.Contains(formatted, "[hitch ×2]", "formato del file di testo");
            T.True(FortniteLogAnalyzer.IsPrivateCategory("LogFortChatManager") && FortniteLogAnalyzer.IsPrivateCategory("LogFriendsService") &&
                   !FortniteLogAnalyzer.IsPrivateCategory("LogNet"), "categorie private");
        }

        private static void LogRobustness()
        {
            var empty = FortniteLogAnalyzer.Analyze(Array.Empty<string>(), Ctx());
            T.True(empty.Found && empty.TotalLines == 0 && empty.Note != null, "vuoto");
            var weird = FortniteLogAnalyzer.Analyze(new[]
            {
                "\u0000\u0001 binary junk", "[not a date][x]LogNet: Warning: ???", "[2026.99.99-99.99.99:999][  1]LogNet: Error: data impossibile",
                "Assertion failed: x != nullptr", "  at Foo() in C:\\Users\\mario\\src\\a.cpp"
            }, Ctx());
            T.Equal(5, weird.TotalLines, "righe contate");
            T.Equal(1, weird.CrashMarkers, "assert senza categoria");
            T.True(weird.FirstTime == null, "data non valida ignorata");
            T.Equal(1, weird.Errors, "errore con data non valida contato");
            T.True(weird.Highlights.All(h => !h.Text.Contains("mario")), "anche le righe senza formato sono ripulite");
        }

        private static void LogFiles()
        {
            var dir = Path.Combine(Path.GetTempPath(), "fnboost-logtest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var main = Path.Combine(dir, "FortniteGame.log");
                var sb = new StringBuilder();
                for (int i = 0; i < 1000; i++) sb.Append(L(i % 3600, $"LogTemp: Warning: riga {i}")).Append('\n');
                File.WriteAllText(main, sb.ToString());

                var (lines, truncated, err) = FortniteLogAnalyzer.ReadTail(main, 4096, 100_000);
                T.True(err == null && truncated, "troncato per byte");
                T.True(lines.Count > 10 && lines.Count < 100, "solo la coda");
                T.True(lines[0].StartsWith("[2026."), "prima riga intera (quella tagliata è scartata)");
                T.Contains(lines[^1], "riga 999", "ultima riga");

                var (lines2, trunc2, _) = FortniteLogAnalyzer.ReadTail(main, long.MaxValue, 50);
                T.Equal(50, lines2.Count, "limite righe");
                T.True(trunc2, "troncato per righe");

                // File aperto in scrittura da un altro "processo" (come il gioco): deve restare leggibile.
                using (var writer = new FileStream(main, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                {
                    var (l3, _, e3) = FortniteLogAnalyzer.ReadTail(main);
                    T.True(e3 == null && l3.Count == 1000, "lettura condivisa");
                }

                File.WriteAllText(Path.Combine(dir, "FortniteGame-backup-2026.10.05-20.00.00.log"),
                    L(1, "LogWindows: Error: Unhandled Exception: EXCEPTION_ACCESS_VIOLATION") + "\n");
                var f = FortniteLogAnalyzer.AnalyzeDirectory(dir, Ctx());
                T.Equal(2, f.Sources.Count, "log + backup");
                T.Equal("FortniteGame-backup-2026.10.05-20.00.00.log", f.Sources[0], "prima il backup");
                T.Equal(1, f.CrashMarkers, "crash dal backup");
                T.True(f.Sources.All(s => !s.Contains(Path.DirectorySeparatorChar)), "solo nomi di file");

                var missing = FortniteLogAnalyzer.AnalyzeDirectory(Path.Combine(dir, "nope"), Ctx());
                T.True(!missing.Found && missing.Note != null, "cartella mancante");
                var none = FortniteLogAnalyzer.AnalyzeDirectory(null, Ctx());
                T.True(!none.Found, "null");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { /* temp */ }
            }
        }

        // ================= ReportBuilder =================

        private static void RecommendationsOrderDedupe()
        {
            var d = new ReportData
            {
                Checks =
                {
                    new ReportCheck { Title = "Piano energetico", Status = CheckStatus.Info, Message = "Bilanciato" },
                    new ReportCheck { Title = "Spazio libero", Status = CheckStatus.Warn, Message = "Solo 10 GB", Hint = "Libera spazio" },
                    new ReportCheck { Title = "Frequenza del monitor", Status = CheckStatus.Bad, Message = "DISPLAY1: 60 Hz su 144." },
                    new ReportCheck { Title = "Frequenza del monitor", Status = CheckStatus.Bad, Message = "DISPLAY2: 60 Hz su 165." },
                    new ReportCheck { Title = "Velocità RAM (XMP)", Status = CheckStatus.Warn, Message = "4800 MT/s", Hint = "XMP" },
                    new ReportCheck { Title = "Ping alto", Status = CheckStatus.Warn, Message = "doppione finto", Hint = "x" },
                    new ReportCheck { Title = "Fortnite", Status = CheckStatus.Ok, Message = "ok" }
                },
                Insights =
                {
                    new PerfInsight { Severity = CheckStatus.Bad, Title = "Molti stutter", Message = "20/min", Hint = "chiudi app" },
                    new PerfInsight { Severity = CheckStatus.Warn, Title = "Limite GPU", Message = "GPU 99%", Hint = "abbassa" },
                    new PerfInsight { Severity = CheckStatus.Ok, Title = "Gioco fluido", Message = "ok" }
                },
                Session = new PerfSession
                {
                    Id = "s1",
                    Network = new NetworkSummary { Game = new PingStats { AvgMs = 140, JitterMs = 2, LossPct = 0 }, PingTargetKind = "server" }
                },
                Log = new LogFindings { Found = true, CrashMarkers = 2, CrashKinds = { ["GPU crashed"] = 2 }, Hitches = 3 }
            };
            var recs = ReportBuilder.BuildRecommendations(d);
            T.True(recs.All(r => r.Severity >= CheckStatus.Warn), "solo Attenzione/Problema (nessun Info qui)");
            T.True(recs.Count(r => r.Title == "Frequenza del monitor") == 1, "controllo ripetuto unito");
            var mon = recs.Single(r => r.Title == "Frequenza del monitor");
            T.True(mon.Problem.Contains("DISPLAY1") && mon.Problem.Contains("DISPLAY2"), "dettagli dei due monitor uniti");
            var ping = recs.Where(r => r.Title.StartsWith("Ping alto")).ToList();
            T.Equal(1, ping.Count, "Ping alto non duplicato");
            T.Equal(CheckStatus.Bad, ping[0].Severity, "tenuto il più grave (rete, 140 ms)");
            T.Equal("Rete", ping[0].Source, "fonte della versione più grave");
            for (int i = 1; i < recs.Count; i++)
            {
                T.True(recs[i - 1].Severity >= recs[i].Severity, "ordine per gravità");
                if (recs[i - 1].Severity == recs[i].Severity) T.True(recs[i - 1].Impact >= recs[i].Impact, "a parità di gravità, per impatto");
            }
            T.True(recs.Take(3).All(r => r.Severity == CheckStatus.Bad), "i Problemi prima");
            T.True(recs.All(r => r.Problem.Length > 0 && r.WhyItMatters.Length > 0 && r.WhatToDo.Length > 0), "Problema / Perché conta / Cosa fare compilati");
            T.True(recs.Any(r => r.Source == "Log di Fortnite" && r.Title.Contains("GPU")), "crash GPU dal log");
            T.True(!recs.Any(r => r.Title.Contains("Hitch")), "pochi hitch: nessuna raccomandazione");
            T.True(recs.First(r => r.Title == "Velocità RAM (XMP)").Impact == 3, "XMP impatto alto");
        }

        private static void SimilarTitlesCases()
        {
            T.True(ReportBuilder.SimilarTitles("Molti stutter", "molti   stutter!"), "uguali a parte maiuscole/punteggiatura");
            T.True(ReportBuilder.SimilarTitles("Ping alto", "Ping alto verso il server"), "uno contenuto nell'altro");
            T.True(ReportBuilder.SimilarTitles("Velocità RAM", "Velocita RAM (XMP)"), "accenti ignorati");
            T.True(!ReportBuilder.SimilarTitles("RAM quasi piena", "Memoria video quasi piena"), "RAM ≠ VRAM");
            T.True(!ReportBuilder.SimilarTitles("Frequenza del monitor", "FPS sotto il refresh del monitor"), "monitor ≠ refresh");
            T.True(!ReportBuilder.SimilarTitles("Limite GPU", "Probabile limite CPU"), "GPU ≠ CPU");
            T.True(!ReportBuilder.SimilarTitles("Qualche stutter", "Molti stutter"), "titoli diversi della stessa analisi");
            T.True(!ReportBuilder.SimilarTitles("Fortnite", "Crash della GPU nel log di Fortnite"), "parola singola generica");
        }

        private static PerfSession SampleSession()
        {
            var secs = new List<SecondSample>();
            for (int i = 0; i < 900; i++)
                secs.Add(new SecondSample
                {
                    T = i,
                    Fps = 200 + (i % 50 == 0 ? -120 : 0),
                    Low1Fps = 150,
                    CpuPercent = 40,
                    GpuPercent = i < 5 ? null : 97,
                    RamPercent = 60,
                    PingMs = i % 30 == 0 ? null : 25 + i % 7,
                    JitterMs = 3,
                    GatewayPingMs = 2,
                    LossPct = 0
                });
            return new PerfSession
            {
                Id = "20261006-210908-ab12",
                StartedAt = new DateTime(2026, 10, 6, 21, 9, 8),
                DurationSec = 900,
                ProcessName = "FortniteClient-Win64-Shipping",
                Label = "<script>alert('x')</script> di Mario",
                Notes = @"note in C:\Users\mario\Desktop",
                Stats = new FrameStatsResult
                {
                    Frames = 180000, DurationSec = 900, AvgFps = 200, Low1Fps = 90, Low01Fps = 40, MinFps = double.NaN, MaxFps = 400,
                    AvgFrametimeMs = 5, MaxFrametimeMs = 60, Stutters = 300, StuttersPerMin = 20, ConsistencyScore = 55
                },
                Seconds = secs,
                FpsCap = 240,
                RefreshHz = 165,
                RenderMode = "DirectX 12",
                Network = new NetworkSummary
                {
                    ServerEndpoints = { "34.1.2.3:7777" },
                    RegionName = "Europa",
                    PingTargetKind = "server",
                    Game = new PingStats { Target = "Server di gioco", Host = "34.1.2.3", AvgMs = 28, JitterMs = 3, LossPct = 0, Sent = 900, Received = 900 },
                    Gateway = new PingStats { Target = "Router", Host = "192.168.1.1", AvgMs = 18, JitterMs = 9, LossPct = 1, Sent = 900, Received = 891 },
                    ConnectionType = "Wi-Fi",
                    WifiSignalPct = 54,
                    Freezes = 4,
                    LongestFreezeMs = 600
                },
                TopProcesses = { new ProcessUsage { Name = "chrome", AvgCpuPct = 12, MaxCpuPct = 40, AvgRamMb = 1800 } }
            };
        }

        private static ReportData SampleReport()
        {
            var session = SampleSession();
            var ft = new float[20000];
            for (int i = 0; i < ft.Length; i++) ft[i] = i % 500 == 0 ? 40f : 5f;
            var d = new ReportData
            {
                GeneratedAt = new DateTime(2026, 10, 6, 22, 0, 0),
                AppVersion = "1.0.0",
                Notes = { @"Nota per Mario in C:\Users\mario" },
                System = new ReportSystem
                {
                    Os = "Windows 11 Pro 24H2 (build 26100.2033)",
                    Cpu = "Intel(R) Core(TM) i5-13600K <script>",
                    Cores = 14, Threads = 20, RamTotalGb = 32, RamType = "DDR5", RamConfiguredMts = 4800, RamRatedMts = 6000, RamModules = 2,
                    Gpus = { new ReportGpu { Name = "NVIDIA GeForce RTX 4070", Vendor = "NVIDIA", DriverVersion = "32.0.15.6094", DriverDate = new DateTime(2026, 9, 1) } },
                    Displays = { new ReportDisplay { Width = 2560, Height = 1440, CurrentHz = 60, MaxHz = 165, Primary = true } },
                    Board = "Gigabyte Z790", BiosVersion = "F12", PageFile = @"C:\pagefile.sys 0 0", PowerPlan = "Bilanciato"
                },
                Checks =
                {
                    new ReportCheck { Title = "Frequenza del monitor", Status = CheckStatus.Bad, Message = @"\\.\DISPLAY1: impostato a 60 Hz ma supporta 165 Hz.", Hint = "Impostazioni › Schermo" },
                    new ReportCheck { Title = "Fortnite", Status = CheckStatus.Ok, Message = @"Installato in C:\Users\mario\Games\Fortnite (SSD NVMe)." }
                },
                Tweaks = { new ReportTweak { Id = "game-mode", Title = "Modalità Gioco", Category = "Windows", State = "Applied", Recommended = true } },
                Fortnite = new ReportFortnite
                {
                    Installed = true, DiskType = "SSD NVMe", FreeGb = 120, ConfigFound = true,
                    Settings = new FortniteSettingsSummary
                    {
                        RenderMode = "DirectX 12", FpsCap = 240, Reflex = 2, WindowMode = 0, ResolutionX = 2560, ResolutionY = 1440,
                        Scalability = { ["sg.ViewDistanceQuality"] = "2", ["sg.ShadowQuality"] = "0" }
                    }
                },
                Session = session,
                Histogram = ReportBuilder.BuildHistogram(ft),
                Insights = PerfAnalyzer.Analyze(session, new[] { session }, ft),
                Trend = PerfAnalyzer.Trend(new[] { session }),
                PreviousSessions = { ReportBuilder.Summarize(session, true) },
                Log = FortniteLogAnalyzer.Analyze(SampleLog(), Ctx())
            };
            return d;
        }

        private static void SanitizeInPlaceCopies()
        {
            var d = SampleReport();
            var original = d.Session!;
            var originalGateway = original.Network!.Gateway!;
            ReportBuilder.SanitizeInPlace(d, Ctx());
            T.True(!ReferenceEquals(original, d.Session), "sessione copiata");
            T.Contains(original.Label, "Mario", "originale intatto");
            T.Equal("192.168.1.1", originalGateway.Host, "host originale intatto");
            T.Equal(Sanitizer.LocalIpToken, d.Session!.Network!.Gateway!.Host, "host del router nascosto");
            T.Equal("34.1.2.3", d.Session.Network.Game!.Host, "server di gioco visibile");
            T.Contains(d.Session.Label, "<utente>", "etichetta ripulita");
            T.Contains(d.Session.Notes, "%USERPROFILE%", "note ripulite");
            T.Equal(900, d.Session.Seconds.Count, "campioni copiati");
            T.Contains(d.Checks[1].Message, "%USERPROFILE%", "messaggio del controllo ripulito");
        }

        private static ReportData FinalReport()
        {
            var d = SampleReport();
            ReportBuilder.SanitizeInPlace(d, Ctx());
            d.Recommendations = ReportBuilder.BuildRecommendations(d);
            return d;
        }

        private static void JsonOutput()
        {
            var d = FinalReport();
            var json = ReportBuilder.BuildJson(d);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            T.Equal("fnboost-report/1", root.GetProperty("schemaVersion").GetString(), "schemaVersion");
            T.Equal("1.0.0", root.GetProperty("appVersion").GetString(), "appVersion");
            foreach (var p in new[] { "system", "checks", "tweaks", "fortnite", "session", "insights", "trend", "previousSessions", "log", "recommendations", "histogram", "notes" })
                T.True(root.TryGetProperty(p, out _), "sezione " + p);
            T.Equal("Bad", root.GetProperty("checks")[0].GetProperty("status").GetString(), "enum come stringa");
            T.Equal("NaN", root.GetProperty("session").GetProperty("stats").GetProperty("minFps").GetString(), "NaN come stringa");
            T.Equal(900, root.GetProperty("session").GetProperty("seconds").GetArrayLength(), "campioni al secondo");
            T.True(root.GetProperty("recommendations").GetArrayLength() > 0, "raccomandazioni presenti");
            T.True(root.GetProperty("recommendations")[0].TryGetProperty("whyItMatters", out _), "camelCase");
            T.True(root.GetProperty("fortnite").GetProperty("settings").GetProperty("scalability").TryGetProperty("sg.ShadowQuality", out _), "chiavi di qualità intatte");
            T.True(!Sanitizer.ContainsIdentity(json, Ctx()), "nessun nome utente/PC nel JSON");
            T.True(!json.Contains("192.168.1.1", StringComparison.Ordinal), "nessun IP locale");
            T.Contains(json, "Modalità", "accenti leggibili");
            T.True(d.Recommendations.Any(r => r.Title == "Crash della GPU nel log di Fortnite"), "crash GPU riconosciuto dal log");
            T.True(!json.Contains("\"gpuPercent\": null", StringComparison.Ordinal), "null omessi");
        }

        private static void HtmlOutput()
        {
            var d = FinalReport();
            var html = ReportBuilder.BuildHtml(d);
            T.True(html.StartsWith("<!doctype html>"), "doctype");
            foreach (var s in new[]
                     {
                         "<h2>Riepilogo</h2>", "<h2>Cosa non va / Cosa migliorare</h2>", "<h2>Grafici</h2>", "<h2>Rete</h2>", "<h2>Sistema</h2>",
                         "<h2>Fortnite</h2>", "<h2>Tweak di FN Boost</h2>", "<h2>Sessioni recenti</h2>", "<h2>Log di Fortnite</h2>",
                         "Come vengono misurati i dati", "Privacy:", "Perché conta", "Cosa fare", "FPS nel tempo", "Distribuzione dei frametime",
                         "Ping e jitter", "CPU e GPU", "#0D1017", "<details>", "MsBetweenPresents", "chrome"
                     })
                T.Contains(html, s, "sezione/contenuto " + s);
            T.True(html.Split("<svg").Length - 1 >= 4, "4 grafici SVG");
            T.True(!html.Contains("<script>alert", StringComparison.OrdinalIgnoreCase), "nessuno script iniettato");
            T.Contains(html, "&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;", "etichetta con escaping");
            T.Contains(html, "i5-13600K &lt;script&gt;", "CPU con escaping");
            T.True(!Sanitizer.ContainsIdentity(html, Ctx()), "nessun nome utente/PC nell'HTML");
            T.True(!html.Contains("192.168.", StringComparison.Ordinal), "nessun IP locale");
            T.True(html.Count(ch => ch == '<') > 100, "pagina non vuota");
            T.Equal(html.Split("<section").Length, html.Split("</section>").Length, "sezioni bilanciate");

            // Report minimo: niente sessione, niente sistema, niente log.
            var empty = new ReportData { GeneratedAt = DateTime.Now, AppVersion = "1.0.0", Notes = { "Modulo Prestazioni non attivo" } };
            empty.Recommendations = ReportBuilder.BuildRecommendations(empty);
            var eh = ReportBuilder.BuildHtml(empty);
            T.Contains(eh, "Nessuna sessione di gioco registrata", "degrado senza sessione");
            T.Contains(eh, "Modulo Prestazioni non attivo", "nota mostrata");
            T.Contains(eh, "Nessun problema evidente", "nessuna raccomandazione");
            using var _ = JsonDocument.Parse(ReportBuilder.BuildJson(empty));
            T.True(ReportBuilder.BuildSummaryText(empty).Contains("nessuna sessione"), "riassunto senza sessione");
        }

        private static void SummaryOutput()
        {
            var d = FinalReport();
            var text = ReportBuilder.BuildSummaryText(d);
            T.True(text.Length <= ReportBuilder.SummaryMaxChars, $"lunghezza {text.Length} <= 4000");
            foreach (var s in new[] { "FN Boost 1.0.0", "[SISTEMA]", "[FORTNITE]", "[SESSIONE", "[RETE]", "[COSA NON VA / COSA MIGLIORARE]", "[LOG DI FORTNITE]", "RTX 4070", "Reflex attivo + boost", "1% low 90" })
                T.Contains(text, s, "riassunto: " + s);
            T.True(!Sanitizer.ContainsIdentity(text, Ctx()), "nessun nome utente");
            T.True(text.Split('\n').Count(l => l.Length > 0 && char.IsDigit(l[0]) && l.Contains(". [")) <= 8, "max 8 raccomandazioni");

            // Tanto testo: deve comunque restare nel limite.
            for (int i = 0; i < 30; i++)
                d.Recommendations.Add(new Recommendation { Severity = CheckStatus.Warn, Title = "Titolo " + i, Problem = new string('p', 400), WhatToDo = new string('w', 400) });
            d.Notes.AddRange(Enumerable.Repeat(new string('n', 500), 5));
            for (int i = 0; i < 10; i++) d.System!.Gpus.Add(new ReportGpu { Name = new string('g', 300) });
            var big = ReportBuilder.BuildSummaryText(d);
            T.True(big.Length <= ReportBuilder.SummaryMaxChars, $"lunghezza con molto testo {big.Length} <= 4000");
        }

        private static void FrametimeHelpers()
        {
            var ft = new float[10000];
            for (int i = 0; i < ft.Length; i++) ft[i] = i % 100 == 0 ? 25f : 5f;
            var h = ReportBuilder.BuildHistogram(ft)!;
            T.Equal(10000, h.Total, "Total");
            T.Equal(10000, h.Counts.Sum(), "somma dei conteggi");
            T.Equal(h.Counts.Count + 1, h.EdgesMs.Count, "bordi = colonne + 1");
            T.Near(5, h.MedianMs, 1e-6, "mediana");
            T.True(ReportBuilder.BuildHistogram(new float[] { float.NaN }) == null, "nessun dato valido");

            var sw = new StringWriter();
            ReportBuilder.WriteFrametimesCsv(sw, new[] { 5f, float.NaN, 10f });
            var csv = sw.ToString().Split('\n');
            T.Equal("index,time_ms,frametime_ms,fps", csv[0], "intestazione");
            T.Equal("0,5,5,200", csv[1], "riga 1");
            T.Equal("1,5,,", csv[2], "frame non valido");
            T.Equal("2,15,10,100", csv[3], "riga 3 (punto decimale invariante)");

            var y = new double?[10000];
            for (int i = 0; i < y.Length; i++) y[i] = 100;
            y[5003] = 5;
            y[7000] = null;
            var idx = SvgChart.Decimate(y);
            T.True(idx.Count <= SvgChart.MaxPoints + 10, "punti ridotti");
            T.True(idx.Contains(5003), "il picco resta");
            T.True(idx.Contains(7000), "il buco resta (interrompe la linea)");
            T.True(idx.SequenceEqual(idx.OrderBy(i => i)), "indici in ordine");
            T.Equal(50, SvgChart.Decimate(y.Take(50).ToArray()).Count, "pochi punti: tutti");
            var svg = SvgChart.Line(Enumerable.Range(0, 3).Select(i => (double)i).ToArray(), new[] { new SvgChart.Series("x", "#fff", new double?[] { 1, null, 3 }) });
            T.Contains(svg, "<path d=\"M", "linea");
            T.True(svg.Split('M').Length - 1 >= 2, "linea interrotta dal null");
        }
    }
}
