using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FNBoost.Core;

namespace FNBoost.Perf
{
    /// <summary>Bersagli del ping.</summary>
    internal enum PingRole { Server, Region, Gateway, Internet }

    /// <summary>
    /// Ping ICMP (come il comando "ping") dal nostro processo, una volta al secondo, verso: server di gioco,
    /// endpoint Epic della regione, router e Internet (1.1.1.1). Nessun contatto con il processo del gioco.
    /// Molti server di gioco non rispondono all'ICMP: in quel caso il "ping di gioco" è quello della regione Epic.
    /// Thread-safe: i cicli di ping girano sul pool di thread, le letture arrivano dal timer di PerfService.
    /// </summary>
    internal sealed class Pinger : IDisposable
    {
        public const int IntervalMs = 1000;
        public const int TimeoutMs = 1000;
        /// <summary>Risultati conservati per le statistiche dal vivo.</summary>
        private const double KeepMs = 60000;
        private const int MaxPending = 600;
        private const double RescanMs = 15 * 60 * 1000.0;
        private const double MinRescanGapMs = 2 * 60 * 1000.0;
        public const string InternetHost = "1.1.1.1";

        /// <summary>Endpoint ufficiali Epic per il ping delle regioni ("Fortnite latency and ping troubleshooting").</summary>
        private static readonly (PingRegion Region, string Host, string Name)[] Regions =
        {
            (PingRegion.Europe, "ping-eu.ds.on.epicgames.com", "Europa"),
            (PingRegion.NaEast, "ping-nae.ds.on.epicgames.com", "NA Est"),
            (PingRegion.NaCentral, "ping-nac.ds.on.epicgames.com", "NA Centro"),
            (PingRegion.NaWest, "ping-naw.ds.on.epicgames.com", "NA Ovest"),
            (PingRegion.Brazil, "ping-br.ds.on.epicgames.com", "Brasile"),
            (PingRegion.Asia, "ping-asia.ds.on.epicgames.com", "Asia"),
            (PingRegion.Oceania, "ping-oce.ds.on.epicgames.com", "Oceania"),
            (PingRegion.MiddleEast, "ping-me.ds.on.epicgames.com", "Medio Oriente")
        };

        public static string RegionDisplayName(PingRegion region) => NetStats.RegionDisplayName(region);

        private sealed class Target
        {
            public readonly PingRole Role;
            public string Label;
            public string Host = "";
            public IPAddress? Address;
            public int Generation;
            public readonly List<(double T, double? Rtt)> Window = new();
            public readonly List<double?> Pending = new();
            public int ConsecutiveFails;
            public int Successes;
            public double? LastResult;
            public bool HasResult;
            public bool ErrorLogged;

            public Target(PingRole role, string label)
            {
                Role = role;
                Label = label;
            }
        }

        private readonly object _lock = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Target[] _targets;
        private readonly PingRegion _regionSetting;
        private readonly bool _pingGateway;
        private CancellationTokenSource? _cts;
        private Task[] _tasks = Array.Empty<Task>();
        private bool _serverUnresponsive;
        private bool _scanRequested;
        private double _lastScanMs = double.NegativeInfinity;
        private bool _disposed;

        public Pinger(PingRegion region, bool pingGateway)
        {
            _regionSetting = region;
            _pingGateway = pingGateway;
            _targets = new[]
            {
                new Target(PingRole.Server, "Server di gioco"),
                new Target(PingRole.Region, "Regione Epic"),
                new Target(PingRole.Gateway, "Router"),
                new Target(PingRole.Internet, "Internet (" + InternetHost + ")") { Address = IPAddress.Parse(InternetHost), Host = InternetHost }
            };
        }

        /// <summary>Nome della regione usata (null finché la prima scansione non è finita).</summary>
        public string? RegionName { get; private set; }
        /// <summary>Regione con il ping più basso nell'ultima scansione.</summary>
        public string? BestRegionName { get; private set; }
        public double? BestRegionMs { get; private set; }

        /// <summary>Vero se il server di gioco non risponde all'ICMP (si usa la regione).</summary>
        public bool ServerUnresponsive
        {
            get
            {
                lock (_lock) return _serverUnresponsive && T(PingRole.Server).Address != null;
            }
        }

        /// <summary>Vero se il "ping di gioco" è quello del server (ha risposto almeno una volta e non è stato scartato).</summary>
        public bool GameUsesServer
        {
            get
            {
                lock (_lock)
                {
                    var s = T(PingRole.Server);
                    return s.Address != null && !_serverUnresponsive && s.Successes > 0;
                }
            }
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_cts != null || _disposed) return;
                var cts = new CancellationTokenSource();
                _cts = cts;
                var ct = cts.Token;
                var tasks = new List<Task>();
                foreach (var t in _targets)
                {
                    if (t.Role == PingRole.Gateway && !_pingGateway) continue;
                    var target = t;
                    tasks.Add(Task.Run(() => LoopAsync(target, ct)));
                }
                tasks.Add(Task.Run(() => ScanLoopAsync(ct)));
                _tasks = tasks.ToArray();
            }
        }

        public void Dispose()
        {
            CancellationTokenSource? cts;
            Task[] tasks;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                cts = _cts;
                _cts = null;
                tasks = _tasks;
                _tasks = Array.Empty<Task>();
            }
            if (cts == null) return;
            try
            {
                cts.Cancel();
                if (Task.WaitAll(tasks, 3000)) cts.Dispose();
                else Log.Warn("Ping: alcuni cicli non si sono chiusi entro 3 secondi");
            }
            catch (AggregateException)
            {
                // Cancellazione: atteso.
                cts.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Chiusura ping: " + ex.Message);
            }
        }

        /// <summary>Server di gioco ricavato dal traffico (null = nessuno). Solo indirizzi pubblici.</summary>
        public void SetServer(IPAddress? ip)
        {
            if (ip != null && !NetStats.IsPublicAddress(ip)) ip = null;
            lock (_lock)
            {
                var t = T(PingRole.Server);
                if (Equals(t.Address, ip)) return;
                ResetTarget(t, ip, ip?.ToString() ?? "");
                _serverUnresponsive = false;
                // Server nuovo: conviene ricontrollare quale regione Epic è la più vicina (al massimo ogni 2 minuti).
                if (ip != null) _scanRequested = true;
            }
        }

        /// <summary>Router (gateway predefinito IPv4). L'indirizzo non viene mai mostrato: l'host è "router".</summary>
        public void SetGateway(IPAddress? ip)
        {
            lock (_lock)
            {
                var t = T(PingRole.Gateway);
                if (Equals(t.Address, ip)) return;
                ResetTarget(t, ip, ip != null ? "router" : "");
            }
        }

        /// <summary>Statistiche degli ultimi <paramref name="windowMs"/> ms (null se il bersaglio non c'è e non ha dati).</summary>
        public PingStats? Stats(PingRole role, double windowMs = KeepMs)
        {
            lock (_lock)
            {
                var t = T(role);
                if (t.Address == null && t.Window.Count == 0) return null;
                if (role == PingRole.Gateway && !_pingGateway) return null;
                double since = _clock.Elapsed.TotalMilliseconds - windowMs;
                var rtts = new List<double?>();
                foreach (var (time, rtt) in t.Window)
                    if (time >= since) rtts.Add(rtt);
                return NetStats.Summarize(rtts, t.Label, t.Host);
            }
        }

        /// <summary>Statistiche del "ping di gioco": server se risponde, altrimenti regione Epic.</summary>
        public PingStats? GameStats(double windowMs = KeepMs) =>
            Stats(GameUsesServer ? PingRole.Server : PingRole.Region, windowMs);

        /// <summary>Risultati arrivati dall'ultima chiamata (null = perso), in ordine. Da chiamare ~1 volta al secondo.</summary>
        public List<double?> Drain(PingRole role)
        {
            lock (_lock)
            {
                var t = T(role);
                var list = new List<double?>(t.Pending);
                t.Pending.Clear();
                return list;
            }
        }

        public string HostOf(PingRole role)
        {
            lock (_lock) return T(role).Host;
        }

        public string LabelOf(PingRole role)
        {
            lock (_lock) return T(role).Label;
        }

        // ================= cicli =================

        private Target T(PingRole role) => _targets[(int)role];

        private void ResetTarget(Target t, IPAddress? ip, string host)
        {
            t.Address = ip;
            t.Host = host;
            t.Generation++;
            t.Window.Clear();
            t.Pending.Clear();
            t.ConsecutiveFails = 0;
            t.Successes = 0;
            t.HasResult = false;
            t.LastResult = null;
        }

        private async Task LoopAsync(Target t, CancellationToken ct)
        {
            try
            {
                using var ping = new Ping(); // un oggetto Ping per bersaglio, riusato
                var buffer = new byte[32];
                while (!ct.IsCancellationRequested)
                {
                    long start = Stopwatch.GetTimestamp();
                    IPAddress? addr;
                    int gen;
                    lock (_lock)
                    {
                        addr = t.Address;
                        gen = t.Generation;
                        if (t.Role == PingRole.Server && _serverUnresponsive) addr = null; // inutile insistere
                    }

                    if (addr != null)
                    {
                        double? rtt = null;
                        try
                        {
                            var reply = await ping.SendPingAsync(addr, TimeSpan.FromMilliseconds(TimeoutMs), buffer, null, ct).ConfigureAwait(false);
                            if (reply.Status == IPStatus.Success) rtt = reply.RoundtripTime;
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            // Rete assente o indirizzo non raggiungibile: conta come perso.
                            if (!t.ErrorLogged)
                            {
                                t.ErrorLogged = true;
                                Log.Warn($"Ping {t.Label}: {ex.GetBaseException().Message}");
                            }
                        }
                        if (ct.IsCancellationRequested) return;
                        Record(t, gen, rtt);
                    }

                    double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(50, IntervalMs - elapsed)), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Arresto.
            }
            catch (Exception ex)
            {
                Log.Error("Ciclo di ping " + t.Label, ex);
            }
        }

        private void Record(Target t, int gen, double? rtt)
        {
            lock (_lock)
            {
                if (gen != t.Generation) return; // bersaglio cambiato durante il ping
                double now = _clock.Elapsed.TotalMilliseconds;
                t.Window.Add((now, rtt));
                int drop = 0;
                while (drop < t.Window.Count && t.Window[drop].T < now - KeepMs) drop++;
                if (drop > 0) t.Window.RemoveRange(0, drop);
                t.Pending.Add(rtt);
                if (t.Pending.Count > MaxPending) t.Pending.RemoveRange(0, t.Pending.Count - MaxPending);
                t.HasResult = true;
                t.LastResult = rtt;

                if (rtt.HasValue)
                {
                    t.Successes++;
                    t.ConsecutiveFails = 0;
                    return;
                }
                t.ConsecutiveFails++;
                if (t.Role != PingRole.Server || _serverUnresponsive) return;

                // Mai risposto dopo 3 tentativi → il server filtra l'ICMP (comune). Se rispondeva e smette per 10 s
                // mentre la regione risponde, è quasi certamente un limite all'ICMP, non una perdita reale.
                var region = T(PingRole.Region);
                bool regionOk = region.HasResult && region.LastResult.HasValue;
                if ((t.Successes == 0 && t.ConsecutiveFails >= 3) || (t.ConsecutiveFails >= 10 && regionOk))
                {
                    _serverUnresponsive = true;
                    t.Pending.Clear(); // i tentativi falliti non sono perdita "di gioco"
                    Log.Info("Il server di gioco non risponde all'ICMP: come ping di gioco si usa la regione Epic");
                }
            }
        }

        // ================= regioni Epic =================

        private async Task ScanLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    bool due;
                    lock (_lock)
                    {
                        double now = _clock.Elapsed.TotalMilliseconds;
                        due = now - _lastScanMs >= RescanMs ||
                              (_scanRequested && _regionSetting == PingRegion.Auto && now - _lastScanMs >= MinRescanGapMs);
                        if (due)
                        {
                            _scanRequested = false;
                            _lastScanMs = now;
                        }
                    }
                    if (due) await ScanRegionsAsync(ct).ConfigureAwait(false);
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Arresto.
            }
            catch (Exception ex)
            {
                Log.Error("Scansione regioni Epic", ex);
            }
        }

        /// <summary>Risolve gli 8 endpoint Epic, li pinga 3 volte e sceglie la regione (mediana più bassa, o quella impostata).</summary>
        private async Task ScanRegionsAsync(CancellationToken ct)
        {
            var probes = await Task.WhenAll(Regions.Select(r => ProbeAsync(r.Region, r.Host, r.Name, ct))).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;

            var measured = probes.Where(p => p.Median.HasValue).OrderBy(p => p.Median!.Value).ToList();
            var best = measured.FirstOrDefault();
            var chosen = _regionSetting == PingRegion.Auto
                ? (best.Address != null ? best : default)
                : probes.FirstOrDefault(p => p.Region == _regionSetting);

            lock (_lock)
            {
                if (_disposed) return;
                BestRegionName = best.Address != null ? best.Name : null;
                BestRegionMs = best.Median.HasValue ? Math.Round(best.Median.Value, 1) : null;
                var t = T(PingRole.Region);
                if (chosen.Address != null && !(Equals(t.Address, chosen.Address) && t.Host == chosen.Host))
                {
                    ResetTarget(t, chosen.Address, chosen.Host);
                    t.Label = "Regione " + chosen.Name;
                    RegionName = chosen.Name;
                }
            }

            if (chosen.Address == null)
                Log.Warn(_regionSetting == PingRegion.Auto
                    ? "Nessuna regione Epic raggiungibile con il ping"
                    : $"Regione Epic {RegionDisplayName(_regionSetting)} non raggiungibile");
            else
                Log.Info($"Regione Epic per il ping: {chosen.Name}" +
                         (chosen.Median.HasValue ? $" ({chosen.Median.Value:0} ms)" : "") +
                         (best.Address != null && best.Name != chosen.Name ? $"; la più vicina è {best.Name} ({best.Median:0} ms)" : ""));
        }

        private readonly record struct Probe(PingRegion Region, string Host, string Name, IPAddress? Address, double? Median);

        private static async Task<Probe> ProbeAsync(PingRegion region, string host, string name, CancellationToken ct)
        {
            IPAddress? addr = null;
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                addr = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs.FirstOrDefault();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return new Probe(region, host, name, null, null);
            }
            if (addr == null) return new Probe(region, host, name, null, null);

            var rtts = new List<double>();
            try
            {
                using var ping = new Ping();
                var buffer = new byte[32];
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        var reply = await ping.SendPingAsync(addr, TimeSpan.FromMilliseconds(TimeoutMs), buffer, null, ct).ConfigureAwait(false);
                        if (reply.Status == IPStatus.Success) rtts.Add(reply.RoundtripTime);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // Perso.
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Ping non disponibile.
            }
            return new Probe(region, host, name, addr, rtts.Count > 0 ? NetStats.Median(rtts) : null);
        }
    }
}
