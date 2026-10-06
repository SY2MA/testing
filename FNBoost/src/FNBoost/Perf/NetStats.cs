using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

// Questo file non dipende da WPF né da API di Windows: viene compilato anche dal progetto di test su Linux.
// Contiene la parte "pura" della misura di rete e dei processi: statistiche del ping, decodifica degli
// eventi Kernel-Network, aggregazione al secondo del traffico del gioco e rilevamento dei freeze.

namespace FNBoost.Perf
{
    /// <summary>Statistiche di rete: ping, jitter, perdita, percentili, correlazione freeze/stutter.</summary>
    public static class NetStats
    {
        /// <summary>
        /// Jitter = media della differenza assoluta tra RTT consecutivi (ms). I ping persi (null) vengono saltati:
        /// si confrontano i ping riusciti uno dopo l'altro. null se ci sono meno di 2 ping riusciti.
        /// </summary>
        public static double? Jitter(IEnumerable<double?> rtts)
        {
            double sum = 0;
            int n = 0;
            double? prev = null;
            foreach (var r in rtts ?? Array.Empty<double?>())
            {
                if (r is not { } v || !double.IsFinite(v) || v < 0) continue;
                if (prev is { } p)
                {
                    sum += Math.Abs(v - p);
                    n++;
                }
                prev = v;
            }
            return n > 0 ? sum / n : null;
        }

        /// <summary>
        /// Variante "livellata" alla RFC 3550 (§6.4.1): J += (|D| − J) / 16. Reagisce più lentamente ai singoli picchi.
        /// </summary>
        public static double? JitterRfc3550(IEnumerable<double?> rtts)
        {
            double j = 0;
            bool any = false;
            double? prev = null;
            foreach (var r in rtts ?? Array.Empty<double?>())
            {
                if (r is not { } v || !double.IsFinite(v) || v < 0) continue;
                if (prev is { } p)
                {
                    j += (Math.Abs(v - p) - j) / 16.0;
                    any = true;
                }
                prev = v;
            }
            return any ? j : null;
        }

        /// <summary>Percentile p (0-100) con interpolazione lineare tra i ranghi. NaN se la lista è vuota.</summary>
        public static double Percentile(IEnumerable<double> values, double p)
        {
            var sorted = (values ?? Array.Empty<double>()).Where(double.IsFinite).OrderBy(v => v).ToArray();
            return PercentileSorted(sorted, p);
        }

        /// <summary>Come <see cref="Percentile"/> ma su un array già ordinato.</summary>
        public static double PercentileSorted(IReadOnlyList<double> sorted, double p)
        {
            int n = sorted.Count;
            if (n == 0) return double.NaN;
            if (n == 1) return sorted[0];
            double rank = Math.Clamp(p, 0, 100) / 100.0 * (n - 1);
            int lo = (int)Math.Floor(rank);
            int hi = Math.Min(lo + 1, n - 1);
            double frac = rank - lo;
            return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
        }

        public static double Median(IEnumerable<double> values) => Percentile(values, 50);

        /// <summary>Percentuale di ping persi (0-100). 0 se non è stato inviato nulla.</summary>
        public static double LossPct(int sent, int received) =>
            sent <= 0 ? 0 : Math.Clamp((sent - received) * 100.0 / sent, 0, 100);

        /// <summary>Percentuale di valori null (= ping persi) nella serie.</summary>
        public static double LossPct(IEnumerable<double?> rtts)
        {
            int sent = 0, received = 0;
            foreach (var r in rtts ?? Array.Empty<double?>())
            {
                sent++;
                if (r is { } v && double.IsFinite(v) && v >= 0) received++;
            }
            return LossPct(sent, received);
        }

        /// <summary>
        /// Riassume una serie di RTT (ms, null = perso, in ordine di tempo). LastMs = ultimo ping riuscito.
        /// I valori in ms sono arrotondati a 0,1.
        /// </summary>
        public static PingStats Summarize(IEnumerable<double?> rtts, string target = "", string host = "")
        {
            var list = (rtts ?? Array.Empty<double?>()).ToList();
            var ok = new List<double>(list.Count);
            foreach (var r in list)
                if (r is { } v && double.IsFinite(v) && v >= 0) ok.Add(v);

            var st = new PingStats
            {
                Target = target ?? "",
                Host = host ?? "",
                Sent = list.Count,
                Received = ok.Count,
                LossPct = Math.Round(LossPct(list.Count, ok.Count), 2)
            };
            if (ok.Count == 0) return st;

            var sorted = ok.OrderBy(v => v).ToArray();
            st.LastMs = R1(ok[ok.Count - 1]);
            st.AvgMs = R1(ok.Average());
            st.MinMs = R1(sorted[0]);
            st.MaxMs = R1(sorted[sorted.Length - 1]);
            st.P95Ms = R1(PercentileSorted(sorted, 95));
            st.JitterMs = Jitter(list) is { } j ? R1(j) : null;
            return st;
        }

        private static double R1(double v) => Math.Round(v, 1);

        // ---- indirizzi ----

        /// <summary>
        /// Vero se l'indirizzo è pubblico (instradabile su Internet). Gli indirizzi privati, locali, CGNAT,
        /// link-local, multicast e di loopback non vanno mai mostrati né salvati nel report.
        /// </summary>
        public static bool IsPublicAddress(IPAddress? ip)
        {
            if (ip == null) return false;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                if (b.Length != 4) return false;
                if (b[0] == 0 || b[0] == 10 || b[0] == 127) return false;
                if (b[0] == 100 && (b[1] & 0xC0) == 64) return false;      // 100.64.0.0/10 (CGNAT)
                if (b[0] == 169 && b[1] == 254) return false;              // link-local
                if (b[0] == 172 && (b[1] & 0xF0) == 16) return false;      // 172.16.0.0/12
                if (b[0] == 192 && b[1] == 168) return false;              // 192.168.0.0/16
                if (b[0] == 192 && b[1] == 0 && b[2] == 0) return false;   // 192.0.0.0/24
                if (b[0] == 198 && (b[1] & 0xFE) == 18) return false;      // 198.18.0.0/15 (benchmark)
                if (b[0] >= 224) return false;                             // multicast e riservati
                return true;
            }
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (IPAddress.IPv6Loopback.Equals(ip) || IPAddress.IPv6None.Equals(ip) || IPAddress.IPv6Any.Equals(ip)) return false;
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return false;
                var b = ip.GetAddressBytes();
                if (b.Length != 16) return false;
                if ((b[0] & 0xFE) == 0xFC) return false;                   // fc00::/7 (ULA)
                return (b[0] & 0xE0) == 0x20;                              // 2000::/3 (unicast globale)
            }
            return false;
        }

        // ---- freeze e stutter ----

        /// <summary>Freeze di rete totali nei campioni al secondo.</summary>
        public static int TotalFreezes(IEnumerable<SecondSample>? seconds) =>
            seconds?.Sum(s => s?.NetFreezes ?? 0) ?? 0;

        /// <summary>
        /// Confronta i secondi con freeze di rete e quelli con stutter dei frame (tolleranza ±toleranceSec,
        /// perché rete e frame sono misurati da tracce diverse e l'allineamento non è perfetto).
        /// </summary>
        public static FreezeStutterCorrelation Correlate(IReadOnlyList<SecondSample>? seconds, int toleranceSec = 1)
        {
            if (seconds == null || seconds.Count == 0) return default;
            int n = seconds.Count;
            var freeze = new bool[n];
            var stutter = new bool[n];
            int fs = 0, ss = 0;
            for (int i = 0; i < n; i++)
            {
                var s = seconds[i];
                if (s == null) continue;
                if (s.NetFreezes > 0) { freeze[i] = true; fs++; }
                if (s.Stutters > 0) { stutter[i] = true; ss++; }
            }
            int fWithS = 0, sWithF = 0;
            for (int i = 0; i < n; i++)
            {
                if (freeze[i] && AnyNear(stutter, i, toleranceSec)) fWithS++;
                if (stutter[i] && AnyNear(freeze, i, toleranceSec)) sWithF++;
            }
            return new FreezeStutterCorrelation(fs, ss, fWithS, sWithF);
        }

        private static bool AnyNear(bool[] flags, int i, int tol)
        {
            int lo = Math.Max(0, i - tol), hi = Math.Min(flags.Length - 1, i + tol);
            for (int k = lo; k <= hi; k++)
                if (flags[k]) return true;
            return false;
        }

        // ---- Wi-Fi ----

        private static readonly Regex PercentValue = new(@"^\s*(\d{1,3})\s*%\s*$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Segnale Wi-Fi (%) dall'output di "netsh wlan show interfaces", indipendente dalla lingua di Windows:
        /// si cerca il blocco dell'interfaccia (riga il cui valore è il suo nome o la sua descrizione) e
        /// lì l'ultima riga con un valore del tipo "85%" / "85 %". null se non trovato.
        /// </summary>
        public static int? ParseWifiSignal(string? netshOutput, string? interfaceName, string? description)
        {
            if (string.IsNullOrWhiteSpace(netshOutput)) return null;
            var blocks = SplitBlocks(netshOutput);

            List<(string Key, string Value)>? chosen = null;
            foreach (var b in blocks)
            {
                if (b.Any(l => Matches(l.Value, interfaceName) || Matches(l.Value, description)))
                {
                    chosen = b;
                    break;
                }
            }
            if (chosen == null)
            {
                var withPct = blocks.Where(b => b.Any(l => PercentValue.IsMatch(l.Value))).ToList();
                if (withPct.Count != 1) return null;
                chosen = withPct[0];
            }

            int? result = null;
            foreach (var l in chosen)
            {
                var m = PercentValue.Match(l.Value);
                if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v))
                    result = Math.Clamp(v, 0, 100);
            }
            return result;
        }

        private static bool Matches(string value, string? wanted) =>
            !string.IsNullOrWhiteSpace(wanted) && string.Equals(value.Trim(), wanted.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>Blocchi "chiave : valore" separati da righe vuote (o dal ripetersi della prima chiave).</summary>
        private static List<List<(string Key, string Value)>> SplitBlocks(string text)
        {
            var blocks = new List<List<(string, string)>>();
            List<(string Key, string Value)>? cur = null;
            foreach (var raw in text.Replace("\r", "").Split('\n'))
            {
                int colon = raw.IndexOf(':');
                if (string.IsNullOrWhiteSpace(raw) || colon <= 0)
                {
                    if (string.IsNullOrWhiteSpace(raw)) cur = null;
                    continue;
                }
                var key = raw.Substring(0, colon).Trim();
                var value = raw.Substring(colon + 1).Trim();
                if (cur != null && cur.Count > 0 && string.Equals(cur[0].Key, key, StringComparison.Ordinal)) cur = null;
                if (cur == null)
                {
                    cur = new List<(string, string)>();
                    blocks.Add(cur);
                }
                cur.Add((key, value));
            }
            return blocks;
        }
    }

    /// <summary>Secondi con freeze di rete, secondi con stutter e quanti coincidono (±tolleranza).</summary>
    public readonly record struct FreezeStutterCorrelation(int FreezeSeconds, int StutterSeconds, int FreezeWithStutter, int StutterWithFreeze);

    /// <summary>Indirizzo remoto IP:porta (server di gioco o altro).</summary>
    public readonly record struct NetEndpoint(IPAddress Address, int Port)
    {
        public override string ToString() =>
            Address.AddressFamily == AddressFamily.InterNetworkV6
                ? $"[{Address}]:{Port.ToString(CultureInfo.InvariantCulture)}"
                : $"{Address}:{Port.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Campi utili di un evento UDP di Microsoft-Windows-Kernel-Network.</summary>
    public readonly record struct KernelUdpEvent(int Pid, int Size, IPAddress DAddr, IPAddress SAddr, int DPort, int SPort);

    /// <summary>
    /// Decodifica del payload degli eventi UDP di Microsoft-Windows-Kernel-Network (42/43 IPv4, 58/59 IPv6):
    /// PID (UInt32), size (UInt32), daddr, saddr (UInt32 o 16 byte), dport, sport (UInt16), seqnum, connid.
    /// Indirizzi e porte sono in ordine di rete (big-endian): l'indirizzo IPv4 si legge byte per byte,
    /// la porta va letta big-endian (il manifest la dichiara win:Port).
    /// </summary>
    public static class KernelNetPayload
    {
        public const int MinLengthV4 = 20;
        public const int MinLengthV6 = 44;

        public static bool TryParse(ReadOnlySpan<byte> data, bool ipv6, out KernelUdpEvent ev)
        {
            ev = default;
            int addrLen = ipv6 ? 16 : 4;
            if (data.Length < 8 + addrLen * 2 + 4) return false;
            uint pid = BinaryPrimitives.ReadUInt32LittleEndian(data);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4));
            if (pid > int.MaxValue || size > 1 << 20) return false;
            var d = new IPAddress(data.Slice(8, addrLen));
            var s = new IPAddress(data.Slice(8 + addrLen, addrLen));
            int portOff = 8 + addrLen * 2;
            int dport = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(portOff));
            int sport = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(portOff + 2));
            ev = new KernelUdpEvent((int)pid, (int)size, d, s, dport, sport);
            return true;
        }

        /// <summary>
        /// Indirizzo remoto: quello che non è un indirizzo locale della macchina. Se lo sono entrambi o nessuno
        /// (es. elenco locale non aggiornato), si usa daddr per gli invii e saddr per le ricezioni.
        /// </summary>
        public static NetEndpoint Remote(in KernelUdpEvent ev, bool isSend, ICollection<IPAddress>? localAddresses)
        {
            bool dLocal = localAddresses != null && localAddresses.Contains(Unmap(ev.DAddr));
            bool sLocal = localAddresses != null && localAddresses.Contains(Unmap(ev.SAddr));
            if (dLocal && !sLocal) return new NetEndpoint(Unmap(ev.SAddr), ev.SPort);
            if (sLocal && !dLocal) return new NetEndpoint(Unmap(ev.DAddr), ev.DPort);
            return isSend ? new NetEndpoint(Unmap(ev.DAddr), ev.DPort) : new NetEndpoint(Unmap(ev.SAddr), ev.SPort);
        }

        private static IPAddress Unmap(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }

    /// <summary>
    /// Rileva i "freeze" di rete su un flusso di pacchetti ricevuti (istanti in ms, in ordine):
    /// pausa &gt; soglia (250 ms) dopo aver ricevuto almeno N pacchetti (10) nel secondo precedente.
    /// La seconda condizione evita falsi allarmi quando il server manda pochi pacchetti (menu, fine partita).
    /// </summary>
    public sealed class FreezeDetector
    {
        public const double DefaultThresholdMs = 250;
        public const int DefaultMinPacketsPerSec = 10;

        private readonly Queue<double> _recent = new();
        private double _last = double.NaN;

        public double ThresholdMs { get; init; } = DefaultThresholdMs;
        public int MinPacketsPerSec { get; init; } = DefaultMinPacketsPerSec;

        /// <summary>Istante dell'ultimo pacchetto (NaN se nessuno).</summary>
        public double LastMs => _last;

        /// <summary>Registra un pacchetto: restituisce la pausa dal precedente (NaN se è il primo) e se è un freeze.</summary>
        public (double GapMs, bool Freeze) OnPacket(double tMs)
        {
            if (!double.IsFinite(tMs)) return (double.NaN, false);
            if (!double.IsNaN(_last) && tMs < _last) return (double.NaN, false); // fuori ordine: ignorato
            double gap = double.IsNaN(_last) ? double.NaN : tMs - _last;
            bool freeze = false;
            if (gap > ThresholdMs)
            {
                while (_recent.Count > 0 && _recent.Peek() <= _last - 1000) _recent.Dequeue();
                freeze = _recent.Count >= MinPacketsPerSec;
            }
            _last = tMs;
            _recent.Enqueue(tMs);
            while (_recent.Count > 0 && _recent.Peek() <= tMs - 1000) _recent.Dequeue();
            return (gap, freeze);
        }

        public void Reset()
        {
            _recent.Clear();
            _last = double.NaN;
        }
    }

    /// <summary>Un pacchetto UDP del gioco. Bytes comprende le intestazioni IP+UDP.</summary>
    public readonly record struct NetPacket(double TimeMs, bool Received, int Bytes, NetEndpoint Remote);

    /// <summary>Un freeze di rete: inizio (ms, tempo della traccia) e durata.</summary>
    public readonly record struct NetFreeze(double StartMs, double DurationMs);

    /// <summary>Traffico del gioco in un secondo della traccia.</summary>
    public sealed class NetSecondTraffic
    {
        public double StartMs { get; set; }
        public int PacketsIn { get; set; }
        public int PacketsOut { get; set; }
        public long BytesIn { get; set; }
        public long BytesOut { get; set; }
        /// <summary>Pausa più lunga tra due pacchetti del server terminata in questo secondo (null se nessun pacchetto dal server).</summary>
        public double? MaxRecvGapMs { get; set; }
        /// <summary>Freeze iniziati in questo secondo (o rilevati ora, se il secondo d'inizio era già chiuso).</summary>
        public int Freezes { get; set; }
        public double LongestFreezeMs { get; set; }
        public NetEndpoint? Server { get; set; }

        public double KbpsIn => BytesIn * 8 / 1000.0;
        public double KbpsOut => BytesOut * 8 / 1000.0;
    }

    /// <summary>
    /// Aggrega i pacchetti del gioco in secondi (tempo della traccia ETW), sceglie il server di gioco
    /// (indirizzo pubblico da cui arrivano più pacchetti negli ultimi 10 s) e rileva i freeze.
    /// I pacchetti possono arrivare fuori ordine (buffer ETW per CPU): vengono ordinati quando il secondo
    /// viene chiuso, con un ritardo scelto dal chiamante. Non thread-safe: va protetto dal chiamante.
    /// </summary>
    public sealed class NetTrafficAggregator
    {
        public const double BucketMs = 1000;
        public const int DominantWindowSec = 10;
        /// <summary>Pacchetti minimi in 10 s perché un indirizzo sia considerato "il server".</summary>
        public const int MinServerPackets = 20;
        public const int MaxPending = 200_000;
        private const int MaxFreezes = 2000;
        private const int MaxCatchUpSeconds = 300;

        private readonly List<NetPacket> _pending = new();
        private readonly Queue<Dictionary<NetEndpoint, int>> _window = new();
        private readonly Dictionary<NetEndpoint, FreezeDetector> _detectors = new();
        private readonly List<NetFreeze> _freezes = new();
        private readonly List<NetEndpoint> _servers = new();
        private long _cursor;

        public NetTrafficAggregator(double startMs = 0)
        {
            _cursor = (long)Math.Floor(startMs / BucketMs);
        }

        /// <summary>Server di gioco attuale (null se nessun flusso abbastanza intenso da un indirizzo pubblico).</summary>
        public NetEndpoint? Server { get; private set; }
        /// <summary>Server distinti visti (in ordine).</summary>
        public IReadOnlyList<NetEndpoint> ServersSeen => _servers;
        public IReadOnlyList<NetFreeze> Freezes => _freezes;
        /// <summary>Pacchetti scartati perché la coda era piena.</summary>
        public long Dropped { get; private set; }
        /// <summary>Inizio (ms) del primo secondo non ancora chiuso.</summary>
        public double CursorMs => _cursor * BucketMs;

        public void Add(in NetPacket p)
        {
            if (!double.IsFinite(p.TimeMs)) return;
            if (_pending.Count >= MaxPending)
            {
                Dropped++;
                return;
            }
            _pending.Add(p);
        }

        /// <summary>Dimentica server e pause (nuovo processo di gioco). I pacchetti in coda restano.</summary>
        public void ResetEndpoints()
        {
            _window.Clear();
            _detectors.Clear();
            Server = null;
        }

        public int CountFreezesSince(double sinceMs)
        {
            int c = 0;
            for (int i = _freezes.Count - 1; i >= 0; i--)
            {
                if (_freezes[i].StartMs + _freezes[i].DurationMs < sinceMs) break;
                c++;
            }
            return c;
        }

        /// <summary>Chiude tutti i secondi che terminano entro horizonMs e li restituisce in ordine.</summary>
        public List<NetSecondTraffic> Advance(double horizonMs)
        {
            var result = new List<NetSecondTraffic>();
            if (!double.IsFinite(horizonMs)) return result;
            long end = (long)Math.Floor(horizonMs / BucketMs); // i secondi < end sono completi
            if (end <= _cursor) return result;
            if (end - _cursor > MaxCatchUpSeconds)
            {
                // Pausa lunghissima (sospensione del PC): si riparte dagli ultimi secondi.
                _cursor = end - MaxCatchUpSeconds;
                ResetEndpoints();
            }

            int n = (int)(end - _cursor);
            double endMs = end * BucketMs;
            var ready = new List<NetPacket>();
            int keep = 0;
            for (int i = 0; i < _pending.Count; i++)
            {
                var p = _pending[i];
                if (p.TimeMs < endMs) ready.Add(p);
                else _pending[keep++] = p;
            }
            _pending.RemoveRange(keep, _pending.Count - keep);
            ready.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));

            int pi = 0;
            for (int i = 0; i < n; i++)
            {
                long k = _cursor + i;
                double bEnd = (k + 1) * BucketMs;
                var b = new NetSecondTraffic { StartMs = k * BucketMs };
                var counts = new Dictionary<NetEndpoint, int>();
                var server = Server;
                while (pi < ready.Count && ready[pi].TimeMs < bEnd)
                {
                    var p = ready[pi++];
                    bool late = p.TimeMs < _cursor * BucketMs; // arrivato dopo la chiusura del suo secondo
                    if (!p.Received)
                    {
                        b.PacketsOut++;
                        b.BytesOut += Math.Max(0, p.Bytes);
                        continue;
                    }
                    b.PacketsIn++;
                    b.BytesIn += Math.Max(0, p.Bytes);
                    counts[p.Remote] = counts.TryGetValue(p.Remote, out var c) ? c + 1 : 1;
                    if (late) continue;

                    if (!_detectors.TryGetValue(p.Remote, out var det))
                    {
                        if (_detectors.Count >= 256) PruneDetectors(p.TimeMs);
                        det = new FreezeDetector();
                        _detectors[p.Remote] = det;
                    }
                    var (gap, freeze) = det.OnPacket(p.TimeMs);
                    if (server == null || !server.Value.Equals(p.Remote) || double.IsNaN(gap)) continue;

                    b.MaxRecvGapMs = Math.Max(b.MaxRecvGapMs ?? 0, gap);
                    if (freeze)
                    {
                        // Il freeze appartiene al secondo in cui è iniziato, se non è già stato chiuso.
                        double start = p.TimeMs - gap;
                        long sk = (long)Math.Floor(start / BucketMs);
                        int idx = (int)Math.Max(0, sk - _cursor);
                        var target = idx < i ? result[idx] : b;
                        target.Freezes++;
                        target.LongestFreezeMs = Math.Max(target.LongestFreezeMs, gap);
                        _freezes.Add(new NetFreeze(start, gap));
                        if (_freezes.Count > MaxFreezes) _freezes.RemoveRange(0, _freezes.Count - MaxFreezes);
                    }
                }
                result.Add(b);

                // Finestra mobile di 10 s per scegliere il server.
                _window.Enqueue(counts);
                while (_window.Count > DominantWindowSec) _window.Dequeue();
                UpdateServer();
                b.Server = Server;
            }
            _cursor = end;
            if (_detectors.Count > 32) PruneDetectors(endMs);
            return result;
        }

        private void UpdateServer()
        {
            var sums = new Dictionary<NetEndpoint, int>();
            foreach (var d in _window)
                foreach (var kv in d)
                    sums[kv.Key] = sums.TryGetValue(kv.Key, out var c) ? c + kv.Value : kv.Value;

            NetEndpoint? best = null;
            int bestCount = 0;
            foreach (var kv in sums)
            {
                if (kv.Value < MinServerPackets || kv.Value <= bestCount) continue;
                if (!NetStats.IsPublicAddress(kv.Key.Address)) continue;
                best = kv.Key;
                bestCount = kv.Value;
            }
            // Con un pareggio si resta sul server attuale.
            if (best != null && Server != null && !best.Value.Equals(Server.Value) &&
                sums.TryGetValue(Server.Value, out var cur) && cur >= bestCount)
                best = Server;

            if (best != null && (Server == null || !best.Value.Equals(Server.Value)) && !_servers.Contains(best.Value))
            {
                _servers.Add(best.Value);
                if (_servers.Count > 100) _servers.RemoveAt(0);
            }
            Server = best;
        }

        private void PruneDetectors(double nowMs)
        {
            var stale = new List<NetEndpoint>();
            foreach (var kv in _detectors)
                if (double.IsNaN(kv.Value.LastMs) || nowMs - kv.Value.LastMs > 30000) stale.Add(kv.Key);
            foreach (var k in stale)
                if (Server == null || !k.Equals(Server.Value)) _detectors.Remove(k);
        }
    }

    /// <summary>Normalizzazione ed esclusioni dei nomi di processo per il campionamento CPU/RAM.</summary>
    public static class ProcessNames
    {
        private static readonly string[] ExcludedPrefixes =
        {
            "EasyAntiCheat", "BEService", "BattlEye", "FortniteClient-Win64-Shipping"
        };

        /// <summary>"chrome#3" → "chrome" (le istanze PDH dello stesso eseguibile si sommano).</summary>
        public static string Normalize(string? instance)
        {
            if (string.IsNullOrEmpty(instance)) return "";
            int hash = instance.LastIndexOf('#');
            if (hash > 0 && hash < instance.Length - 1)
            {
                bool digits = true;
                for (int i = hash + 1; i < instance.Length; i++)
                    if (!char.IsDigit(instance[i])) { digits = false; break; }
                if (digits) return instance.Substring(0, hash);
            }
            return instance;
        }

        /// <summary>
        /// Processi da non elencare: totale, Idle, FN Boost stesso, il gioco e gli anti-cheat
        /// (EasyAntiCheat, BattlEye): non vanno né misurati né nominati.
        /// </summary>
        public static bool IsExcluded(string name, string? ownName, string? gameName)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            if (name.Equals("_Total", StringComparison.OrdinalIgnoreCase) || name.Equals("Idle", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrEmpty(ownName) && name.Equals(ownName, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(gameName) && name.Equals(gameName, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var p in ExcludedPrefixes)
                if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>
    /// Accumula i campioni CPU/RAM per nome di processo durante una registrazione.
    /// Medie sull'intera registrazione: un processo assente in un campione conta come 0.
    /// </summary>
    public sealed class ProcessUsageAccumulator
    {
        private sealed class Acc
        {
            public double SumCpu, MaxCpu, SumRam;
        }

        private readonly Dictionary<string, Acc> _map = new(StringComparer.OrdinalIgnoreCase);

        public int Samples { get; private set; }

        /// <summary>Un campione: nome → (CPU % del totale della macchina, RAM privata MB).</summary>
        public void Add(IReadOnlyDictionary<string, (double CpuPct, double RamMb)> sample)
        {
            if (sample == null) return;
            Samples++;
            foreach (var kv in sample)
            {
                if (!_map.TryGetValue(kv.Key, out var a))
                {
                    if (_map.Count >= 2000) continue;
                    a = new Acc();
                    _map[kv.Key] = a;
                }
                double cpu = double.IsFinite(kv.Value.CpuPct) ? Math.Max(0, kv.Value.CpuPct) : 0;
                double ram = double.IsFinite(kv.Value.RamMb) ? Math.Max(0, kv.Value.RamMb) : 0;
                a.SumCpu += cpu;
                a.MaxCpu = Math.Max(a.MaxCpu, cpu);
                a.SumRam += ram;
            }
        }

        /// <summary>I primi <paramref name="count"/> per CPU media, solo se CPU media ≥ minAvgCpu o RAM media ≥ minRamMb.</summary>
        public List<ProcessUsage> Top(int count = 10, double minAvgCpu = 0.5, double minRamMb = 300)
        {
            if (Samples == 0) return new List<ProcessUsage>();
            return _map
                .Select(kv => new ProcessUsage
                {
                    Name = kv.Key,
                    AvgCpuPct = Math.Round(kv.Value.SumCpu / Samples, 1),
                    MaxCpuPct = Math.Round(kv.Value.MaxCpu, 1),
                    AvgRamMb = Math.Round(kv.Value.SumRam / Samples, 0)
                })
                .Where(p => p.AvgCpuPct >= minAvgCpu || p.AvgRamMb >= minRamMb)
                .OrderByDescending(p => p.AvgCpuPct)
                .ThenByDescending(p => p.AvgRamMb)
                .Take(Math.Max(0, count))
                .ToList();
        }
    }
}
