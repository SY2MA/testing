using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

// Questo file non dipende da WPF né da API di Windows: viene compilato anche dal progetto di test su Linux.

namespace FNBoost.Report
{
    /// <summary>
    /// Cosa nascondere nel report: identità del PC/utente e indirizzi IP ammessi (server di gioco, endpoint Epic).
    /// </summary>
    public sealed class SanitizeContext
    {
        private readonly HashSet<string> _allowed = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _identifiers = new();

        /// <summary>Nome utente di Windows (Environment.UserName).</summary>
        public string? UserName { get; set; }
        /// <summary>Nome del PC (Environment.MachineName).</summary>
        public string? MachineName { get; set; }

        /// <summary>Altri identificativi da nascondere come "&lt;utente&gt;" (es. nome della cartella del profilo).</summary>
        public IReadOnlyList<string> ExtraIdentifiers => _identifiers;

        /// <summary>IP pubblici che possono restare in chiaro (normalizzati).</summary>
        public IReadOnlyCollection<string> AllowedIps => _allowed;

        public SanitizeContext() { }

        public SanitizeContext(string? userName, string? machineName, IEnumerable<string>? allowedEndpoints = null)
        {
            UserName = userName;
            MachineName = machineName;
            if (allowedEndpoints != null) AllowRange(allowedEndpoints);
        }

        public SanitizeContext AddIdentifier(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !_identifiers.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
                _identifiers.Add(value.Trim());
            return this;
        }

        /// <summary>Ammette un IP o un endpoint "ip:porta" / "[ipv6]:porta". I valori non validi vengono ignorati.</summary>
        public SanitizeContext Allow(string? endpoint)
        {
            var ip = Sanitizer.ExtractIp(endpoint);
            if (ip != null) _allowed.Add(ip);
            return this;
        }

        public SanitizeContext AllowRange(IEnumerable<string?> endpoints)
        {
            foreach (var e in endpoints) Allow(e);
            return this;
        }

        public bool IsAllowed(IPAddress ip) => _allowed.Contains(Sanitizer.Normalize(ip));

        /// <summary>Copia con in più gli endpoint indicati (il contesto originale non cambia).</summary>
        public SanitizeContext With(IEnumerable<string?> endpoints)
        {
            var c = new SanitizeContext(UserName, MachineName);
            foreach (var a in _allowed) c._allowed.Add(a);
            foreach (var i in _identifiers) c._identifiers.Add(i);
            return c.AllowRange(endpoints);
        }
    }

    /// <summary>
    /// Rimuove dai testi del report tutto ciò che identifica la persona o la sua rete: percorsi del profilo,
    /// nome utente e del PC, email, id account Epic, token nelle URL, IP locali e IP pubblici non ammessi.
    /// Deterministico: lo stesso testo con lo stesso contesto dà sempre lo stesso risultato.
    /// </summary>
    public static class Sanitizer
    {
        public const string LocalIpToken = "<ip-locale>";
        public const string IpToken = "<ip>";
        public const string UserToken = "<utente>";
        public const string PcToken = "<pc>";
        public const string EmailToken = "<email>";
        public const string IdToken = "<id>";
        public const string SecretToken = "<rimosso>";
        public const string ProfileToken = "%USERPROFILE%";

        /// <summary>
        /// Nomi utente "generici" che non identificano nessuno: sostituirli ovunque rovinerebbe il testo
        /// (es. "user", "admin"). Restano solo nei percorsi del profilo, che vengono comunque nascosti.
        /// </summary>
        private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "user", "users", "utente", "admin", "administrator", "amministratore", "owner", "guest", "pc", "gamer",
            "gaming", "player", "home", "default", "public", "windows", "fortnite", "system", "test"
        };

        private const RegexOptions Opt = RegexOptions.CultureInvariant | RegexOptions.Compiled;

        // C:\Users\<nome>  (anche con \\ come nel JSON o con /). Esclude le cartelle di sistema.
        private static readonly Regex ProfilePath = new(
            @"\b[A-Za-z]:(?:\\{1,2}|/)(?:Users|Documents and Settings)(?:\\{1,2}|/)" +
            @"(?!(?:Public|Default|Default User|All Users)(?:\\|/|$|[\s""']))" +
            @"(?:[^\\/:*?""<>|\r\n]{1,64}?(?=\\|/)|[^\\/:*?""<>|\s]+)",
            Opt | RegexOptions.IgnoreCase);

        private static readonly Regex Email = new(
            @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", Opt);

        // Valori nelle query string: ?code=…&access_token=…
        private static readonly Regex QueryToken = new(
            @"(?<![\w])(access_token|refresh_token|id_token|exchange_code|code|token|auth|password|pwd|accountId|account_id|sessionId|session_id)=([^&\s""'<>]+)",
            Opt | RegexOptions.IgnoreCase);

        // Campi "etichetta: valore" che contengono identità (id account, nomi visualizzati).
        private static readonly Regex LabeledIdentity = new(
            @"(?<![\w])(accountId|account_id|account id|displayName|display name|epicUserName|userName|user name|playerName|player name)(\s*[:=]\s*)(""[^""]*""|'[^']*'|[^\s,;&""'<>\]\)]+)",
            Opt | RegexOptions.IgnoreCase);

        private static readonly Regex Bearer = new(@"\b(Bearer|Basic)\s+[A-Za-z0-9\-._~+/]{8,}=*", Opt | RegexOptions.IgnoreCase);

        private static readonly Regex Url = new(@"https?://[^\s""'<>]+", Opt | RegexOptions.IgnoreCase);
        private static readonly Regex Guid = new(@"[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}", Opt);

        // Id account Epic: 32 cifre esadecimali.
        private static readonly Regex Hex32 = new(@"(?<![0-9A-Fa-f])[0-9A-Fa-f]{32}(?![0-9A-Fa-f])", Opt);

        // IPv4 con ottetti validi, non attaccato a lettere/cifre/punti (evita i numeri di versione come 1.2.3.4.5).
        private static readonly Regex Ipv4 = new(
            @"(?<![\w.])(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?:\.(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}(?!\.?\d)(?![\w])", Opt);

        // Candidati IPv6 (validati poi con IPAddress.TryParse).
        private static readonly Regex Ipv6 = new(
            @"(?<![0-9A-Za-z:.])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?:%[0-9A-Za-z]+)?(?![0-9A-Za-z:])", Opt);

        // Nomi predefiniti dei PC Windows (DESKTOP-ABC1234, LAPTOP-…).
        private static readonly Regex DefaultPcName = new(@"\b(?:DESKTOP|LAPTOP)-[A-Z0-9]{6,8}\b", Opt | RegexOptions.IgnoreCase);

        /// <summary>Applica tutte le regole. Testo null → stringa vuota.</summary>
        public static string Sanitize(string? text, SanitizeContext ctx)
        {
            if (string.IsNullOrEmpty(text)) return "";
            ctx ??= new SanitizeContext();
            var s = ScrubProfiles(text, ctx);
            s = Email.Replace(s, EmailToken);
            s = Bearer.Replace(s, m => m.Groups[1].Value + " " + SecretToken);
            s = QueryToken.Replace(s, m => m.Groups[1].Value + "=" + SecretToken);
            s = LabeledIdentity.Replace(s, m => m.Groups[1].Value + m.Groups[2].Value + SecretToken);
            s = Url.Replace(s, m => Guid.Replace(m.Value, IdToken));
            s = Hex32.Replace(s, IdToken);
            s = Ipv4.Replace(s, m => ReplaceIp(m.Value, ctx));
            s = Ipv6.Replace(s, m => ReplaceIp(m.Value, ctx));
            s = ScrubNames(s, ctx);
            return s;
        }

        /// <summary>
        /// Passaggio "leggero" per i file finiti: solo percorsi del profilo, email, nome utente e nome del PC.
        /// Non tocca numeri e IP (i campi strutturati come le versioni dei driver restano intatti).
        /// </summary>
        public static string ScrubIdentity(string? text, SanitizeContext ctx)
        {
            if (string.IsNullOrEmpty(text)) return "";
            ctx ??= new SanitizeContext();
            var s = ScrubProfiles(text, ctx);
            s = Email.Replace(s, EmailToken);
            return ScrubNames(s, ctx);
        }

        /// <summary>Vero se nel testo compare ancora il nome utente, il nome del PC o un identificativo extra (parola intera).</summary>
        public static bool ContainsIdentity(string? text, SanitizeContext ctx)
        {
            if (string.IsNullOrEmpty(text) || ctx == null) return false;
            foreach (var name in Names(ctx))
                if (WordRegex(name).IsMatch(text)) return true;
            return false;
        }

        /// <summary>Nome utente/PC sostituibile senza rovinare il testo (almeno 3 caratteri, non generico).</summary>
        public static bool IsMaskable(string? name) =>
            !string.IsNullOrWhiteSpace(name) && name.Trim().Length >= 3 && !GenericNames.Contains(name.Trim());

        /// <summary>Estrae l'IP normalizzato da "ip", "ip:porta" o "[ipv6]:porta"; null se non è un IP.</summary>
        public static string? ExtractIp(string? endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return null;
            var e = endpoint.Trim();
            if (e.StartsWith("[", StringComparison.Ordinal))
            {
                int close = e.IndexOf(']');
                if (close > 1) e = e.Substring(1, close - 1);
            }
            else if (e.Count(c => c == ':') == 1)
            {
                e = e.Substring(0, e.IndexOf(':'));
            }
            return IPAddress.TryParse(e, out var ip) ? Normalize(ip) : null;
        }

        public static string Normalize(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0) ip = new IPAddress(ip.GetAddressBytes());
            return ip.ToString();
        }

        /// <summary>IP privati/locali: 10/8, 172.16/12, 192.168/16, 127/8, 169.254/16, 100.64/10 (CGNAT), IPv6 link-local, ULA e ::1.</summary>
        public static bool IsLocal(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            var b = ip.GetAddressBytes();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                return b[0] == 10 || b[0] == 127 ||
                       (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                       (b[0] == 192 && b[1] == 168) ||
                       (b[0] == 169 && b[1] == 254) ||
                       (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
            }
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return IPAddress.IPv6Loopback.Equals(ip) ||
                       (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) || // fe80::/10
                       (b[0] & 0xFE) == 0xFC;                      // fc00::/7
            }
            return false;
        }

        /// <summary>IP pubblico instradabile (IPv4 non riservato, IPv6 globale 2000::/3).</summary>
        public static bool IsPublic(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IsLocal(ip)) return false;
            var b = ip.GetAddressBytes();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
                return b[0] != 0 && b[0] < 224; // 0/8, multicast e riservati restano com'erano
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                return (b[0] & 0xE0) == 0x20;
            return false;
        }

        // ---- interni ----

        private static string ReplaceIp(string value, SanitizeContext ctx)
        {
            var raw = value;
            int pct = raw.IndexOf('%');
            if (pct > 0) raw = raw.Substring(0, pct);
            if (raw.Contains(':') && !raw.Contains("::") && raw.Count(c => c == ':') != 7) return value; // es. orari "21:09:08"
            if (!IPAddress.TryParse(raw, out var ip)) return value;
            if (IsLocal(ip)) return LocalIpToken;
            if (!IsPublic(ip)) return value;
            return ctx.IsAllowed(ip) ? value : IpToken;
        }

        private static IEnumerable<(string Name, string Token)> NamesWithTokens(SanitizeContext ctx)
        {
            // Prima i più lunghi: "mario_000" va sostituito prima di "mario".
            var list = new List<(string, string)>();
            if (IsMaskable(ctx.UserName)) list.Add((ctx.UserName!.Trim(), UserToken));
            foreach (var id in ctx.ExtraIdentifiers)
                if (IsMaskable(id)) list.Add((id, UserToken));
            if (IsMaskable(ctx.MachineName)) list.Add((ctx.MachineName!.Trim(), PcToken));
            return list.OrderByDescending(x => x.Item1.Length);
        }

        private static IEnumerable<string> Names(SanitizeContext ctx) => NamesWithTokens(ctx).Select(x => x.Name);

        private static string ScrubNames(string s, SanitizeContext ctx)
        {
            foreach (var (name, token) in NamesWithTokens(ctx))
                s = WordRegex(name).Replace(s, token);
            s = DefaultPcName.Replace(s, PcToken);
            return s;
        }

        /// <summary>
        /// Percorsi del profilo. Prima i profili con i nomi noti (anche con spazi: "C:\Users\Mario Rossi" a fine
        /// percorso), poi la regola generica, che a fine percorso si ferma al primo spazio e lascerebbe il cognome.
        /// </summary>
        private static string ScrubProfiles(string s, SanitizeContext ctx)
        {
            foreach (var name in Names(ctx))
                s = KnownProfileRegex(name).Replace(s, ProfileToken);
            return ProfilePath.Replace(s, ProfileToken);
        }

        private static readonly Dictionary<string, Regex> ProfileCache = new(StringComparer.OrdinalIgnoreCase);

        private static Regex KnownProfileRegex(string name)
        {
            lock (ProfileCache)
            {
                if (!ProfileCache.TryGetValue(name, out var rx))
                {
                    rx = new Regex(@"\b[A-Za-z]:(?:\\{1,2}|/)(?:Users|Documents and Settings)(?:\\{1,2}|/)" +
                                   Regex.Escape(name) +
                                   // Solo se la cartella finisce qui: "C:\Users\Mario Rossi\..." (con un separatore
                                   // più avanti) resta alla regola generica, che prende la cartella intera.
                                   @"(?![\p{L}\p{N}])(?![^\\/:*?""<>|\r\n]{0,64}?(?:\\|/))",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (ProfileCache.Count > 64) ProfileCache.Clear();
                    ProfileCache[name] = rx;
                }
                return rx;
            }
        }

        private static readonly Dictionary<string, Regex> WordCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Il nome come parola intera (non attaccato ad altre lettere o cifre), senza distinzione di maiuscole.</summary>
        private static Regex WordRegex(string name)
        {
            lock (WordCache)
            {
                if (!WordCache.TryGetValue(name, out var rx))
                {
                    rx = new Regex(@"(?<![\p{L}\p{N}])" + Regex.Escape(name) + @"(?![\p{L}\p{N}])",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (WordCache.Count > 64) WordCache.Clear();
                    WordCache[name] = rx;
                }
                return rx;
            }
        }
    }
}
