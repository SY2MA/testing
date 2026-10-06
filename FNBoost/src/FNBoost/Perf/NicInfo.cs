using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using FNBoost.Core;

namespace FNBoost.Perf
{
    /// <summary>Interfaccia di rete usata per Internet. Gateway e indirizzi locali servono solo internamente: mai nel report.</summary>
    internal sealed record NicState(
        string ConnectionType,
        string AdapterName,
        double? LinkSpeedMbps,
        int? WifiSignalPct,
        IPAddress? Gateway,
        string InterfaceId,
        IReadOnlyCollection<IPAddress> LocalAddresses);

    /// <summary>
    /// Informazioni sulla connessione: tipo (Ethernet / Wi-Fi), velocità del collegamento, segnale Wi-Fi
    /// (da "netsh wlan show interfaces"), gateway e banda totale dell'interfaccia (contatori di Windows).
    /// L'elenco delle interfacce si aggiorna ogni 30 s su un thread del pool; la banda si legge dal timer di PerfService.
    /// </summary>
    internal sealed class NicInfo : IDisposable
    {
        private const int RefreshMs = 30000;
        private const int NetshTimeoutMs = 3000;

        private readonly object _lock = new();
        private Timer? _timer;
        private volatile NicState? _state;
        private NetworkInterface? _nic;      // sotto _lock
        private string? _counterId;          // interfaccia dei contatori precedenti
        private long _lastIn, _lastOut;
        private long _lastStamp;
        private int _refreshing;
        private bool _disposed;
        private bool _netshLogged;

        /// <summary>Ultimo stato letto (null finché la prima lettura non è finita o se non c'è connessione).</summary>
        public NicState? State => _state;

        public void Start()
        {
            lock (_lock)
            {
                if (_timer != null || _disposed) return;
                _timer = new Timer(_ => Refresh(), null, 0, RefreshMs);
            }
        }

        public void Dispose()
        {
            Timer? t;
            lock (_lock)
            {
                _disposed = true;
                t = _timer;
                _timer = null;
                _nic = null;
            }
            try
            {
                t?.Dispose();
            }
            catch
            {
                // Best-effort.
            }
        }

        /// <summary>Banda totale dell'interfaccia (kbit/s) dall'ultima chiamata. null alla prima lettura o se non disponibile.</summary>
        public (double InKbps, double OutKbps)? SampleThroughput()
        {
            NetworkInterface? nic;
            lock (_lock) nic = _nic;
            if (nic == null) return null;
            try
            {
                var st = nic.GetIPStatistics();
                long bin = st.BytesReceived, bout = st.BytesSent;
                long stamp = Stopwatch.GetTimestamp();
                lock (_lock)
                {
                    bool same = _counterId == nic.Id && _lastStamp != 0;
                    double sec = same ? (stamp - _lastStamp) / (double)Stopwatch.Frequency : 0;
                    long dIn = bin - _lastIn, dOut = bout - _lastOut;
                    _counterId = nic.Id;
                    _lastIn = bin;
                    _lastOut = bout;
                    _lastStamp = stamp;
                    // Contatori azzerati (riconnessione) o intervallo anomalo: si salta questo campione.
                    if (!same || sec < 0.2 || sec > 10 || dIn < 0 || dOut < 0) return null;
                    return (dIn * 8 / 1000.0 / sec, dOut * 8 / 1000.0 / sec);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Tutti gli indirizzi unicast locali (di tutte le interfacce).</summary>
        public static HashSet<IPAddress> ReadLocalAddresses()
        {
            var set = new HashSet<IPAddress>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        foreach (var u in nic.GetIPProperties().UnicastAddresses)
                            set.Add(u.Address.IsIPv4MappedToIPv6 ? u.Address.MapToIPv4() : u.Address);
                    }
                    catch
                    {
                        // Interfaccia sparita nel frattempo.
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Indirizzi locali non disponibili: " + ex.Message);
            }
            return set;
        }

        private void Refresh()
        {
            if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
            try
            {
                lock (_lock)
                    if (_disposed) return;

                var all = NetworkInterface.GetAllNetworkInterfaces();
                var locals = new HashSet<IPAddress>();
                var candidates = new List<(NetworkInterface Nic, IPAddress Gateway, int Index)>();
                foreach (var nic in all)
                {
                    try
                    {
                        var props = nic.GetIPProperties();
                        foreach (var u in props.UnicastAddresses)
                            locals.Add(u.Address.IsIPv4MappedToIPv6 ? u.Address.MapToIPv4() : u.Address);
                        if (nic.OperationalStatus != OperationalStatus.Up) continue;
                        if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                        var gw = props.GatewayAddresses
                            .Select(g => g.Address)
                            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
                        if (gw == null) continue;
                        int index = -1;
                        try
                        {
                            index = props.GetIPv4Properties()?.Index ?? -1;
                        }
                        catch
                        {
                            // IPv4 non abilitato.
                        }
                        candidates.Add((nic, gw, index));
                    }
                    catch
                    {
                        // Interfaccia sparita nel frattempo.
                    }
                }

                // Preferita: quella che Windows userebbe per raggiungere 1.1.1.1.
                int best = BestInterfaceIndexFor1111();
                var chosen = candidates.FirstOrDefault(c => best >= 0 && c.Index == best);
                if (chosen.Nic == null) chosen = candidates.FirstOrDefault();

                if (chosen.Nic == null)
                {
                    lock (_lock)
                    {
                        _nic = null;
                        _state = new NicState("", "", null, null, null, "", locals);
                    }
                    return;
                }

                var nicSel = chosen.Nic;
                string type = TypeName(nicSel.NetworkInterfaceType);
                double? speed = null;
                try
                {
                    long bps = nicSel.Speed;
                    if (bps > 0) speed = Math.Round(bps / 1_000_000.0, 0);
                }
                catch
                {
                    // Velocità non disponibile.
                }
                int? signal = type == "Wi-Fi" ? ReadWifiSignal(nicSel.Name, nicSel.Description) : null;

                lock (_lock)
                {
                    if (_disposed) return;
                    _nic = nicSel;
                    _state = new NicState(type, nicSel.Description ?? "", speed, signal, chosen.Gateway, nicSel.Id, locals);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Lettura interfaccia di rete: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        }

        private static string TypeName(NetworkInterfaceType t) => t switch
        {
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit or NetworkInterfaceType.FastEthernetT or
                NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.GigabitEthernet => "Ethernet",
            _ => "altro"
        };

        /// <summary>Segnale Wi-Fi (%) da "netsh wlan show interfaces" (massimo 3 s). null se non leggibile.</summary>
        private int? ReadWifiSignal(string name, string description)
        {
            try
            {
                var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                var outTask = p.StandardOutput.ReadToEndAsync();
                _ = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(NetshTimeoutMs))
                {
                    try
                    {
                        p.Kill(true);
                    }
                    catch
                    {
                        // Già terminato.
                    }
                    return null;
                }
                if (!outTask.Wait(500)) return null;
                return NetStats.ParseWifiSignal(outTask.Result, name, description);
            }
            catch (Exception ex)
            {
                if (!_netshLogged)
                {
                    _netshLogged = true;
                    Log.Warn("Segnale Wi-Fi non disponibile: " + ex.Message);
                }
                return null;
            }
        }

        private static int BestInterfaceIndexFor1111()
        {
            try
            {
                if (!OperatingSystem.IsWindows()) return -1;
                // 1.1.1.1 è uguale in qualsiasi ordine dei byte.
                return GetBestInterface(0x01010101u, out var index) == 0 ? (int)index : -1;
            }
            catch
            {
                return -1;
            }
        }

        [DllImport("iphlpapi.dll")]
        private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);
    }
}
