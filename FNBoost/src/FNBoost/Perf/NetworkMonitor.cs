using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using FNBoost.Core;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace FNBoost.Perf
{
    /// <summary>
    /// Misura il traffico UDP del gioco con la traccia di sistema Microsoft-Windows-Kernel-Network (ETW),
    /// la stessa usata da Monitoraggio risorse. Come per gli FPS, non tocca il processo del gioco: Windows
    /// registra ogni pacchetto inviato/ricevuto con il PID del processo e noi leggiamo quella traccia.
    /// Si contano solo pacchetti e byte (nessun contenuto): pacchetti/s, banda, pause tra i pacchetti del
    /// server (freeze) e indirizzo del server di gioco.
    /// In più somma i byte TCP e UDP di OGNI processo (solo PID e dimensione, come la scheda Rete di Monitoraggio
    /// risorse), per dire chi scarica durante la partita: Fortnite stesso (contenuti in streaming) o un'altra app.
    /// </summary>
    internal sealed class NetworkMonitor : IDisposable
    {
        public const string SessionName = "FNBoost-NetCapture";

        /// <summary>Microsoft-Windows-Kernel-Network.</summary>
        private static readonly Guid KernelNetworkProvider = new("7DD42A49-5329-4832-8DFD-43D979153A88");
        /// <summary>Keyword IPv4 (0x10) | IPv6 (0x20).</summary>
        private const ulong NetKeywords = 0x10 | 0x20;
        private const int UdpSendV4 = 42, UdpRecvV4 = 43, UdpSendV6 = 58, UdpRecvV6 = 59;
        /// <summary>TCP: invio/ricezione IPv4 (10/11) e IPv6 (26/27). Payload: PID (UInt32), size (UInt32), …</summary>
        private const int TcpSendV4 = 10, TcpRecvV4 = 11, TcpSendV6 = 26, TcpRecvV6 = 27;
        /// <summary>Intestazioni IP + UDP aggiunte alla dimensione del payload per stimare la banda reale.</summary>
        private const int HeaderV4 = 28, HeaderV6 = 48;
        /// <summary>
        /// Un secondo si chiude solo quando è passato da almeno così tanto: gli eventi ETW in tempo reale
        /// arrivano a blocchi (buffer per CPU svuotati ~1 volta al secondo) e non sempre in ordine.
        /// </summary>
        public const double LagMs = 2000;

        private readonly object _gate = new(); // sessione e thread
        private readonly object _data = new(); // aggregatore
        private readonly object _procData = new(); // traffico per processo
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private TraceEventSession? _session;
        private Thread? _thread;
        private RunState? _run;
        private volatile int _targetPid;
        private volatile HashSet<IPAddress> _local = new();
        private volatile string? _error;
        private bool _eventErrorLogged;

        private sealed class RunState
        {
            public volatile bool Stopping;
            public volatile bool Started;
            /// <summary>Stima di (orologio interno − tempo della traccia), in ms. NaN finché la sessione non parte.</summary>
            public double OffsetMs = double.NaN;
            public readonly NetTrafficAggregator Agg = new();
            /// <summary>Byte per PID e per secondo di tutti i processi (protetto da _procData).</summary>
            public readonly ProcessTrafficAggregator Procs = new();
            public bool ProcDropLogged;
            public int StatePid;
            public bool DropLogged;
            /// <summary>Buffer riusato dal thread ETW per copiare il payload.</summary>
            public readonly byte[] Buffer = new byte[64];
        }

        /// <summary>Processo di cui misurare il traffico (0 = nessuno). Scritto dal timer di PerfService.</summary>
        public int TargetPid
        {
            get => _targetPid;
            set => _targetPid = Math.Max(0, value);
        }

        /// <summary>
        /// Da quanti ms è passato l'istante <paramref name="traceMs"/> (tempo della traccia, come NetSecondTraffic.StartMs).
        /// NaN se la traccia non è attiva. Serve a ricollocare i secondi chiusi in ritardo nel tempo della sessione.
        /// </summary>
        public double AgeMs(double traceMs)
        {
            var run = _run;
            if (run == null || !double.IsFinite(traceMs)) return double.NaN;
            double offset = Volatile.Read(ref run.OffsetMs);
            if (double.IsNaN(offset)) return double.NaN;
            return _clock.Elapsed.TotalMilliseconds - offset - traceMs;
        }

        /// <summary>Errore in italiano (null se tutto bene).</summary>
        public string? Error => _error;

        /// <summary>Vero se la traccia è attiva e senza errori.</summary>
        public bool IsAvailable
        {
            get
            {
                var run = _run;
                return run != null && run.Started && !run.Stopping && _error == null;
            }
        }

        /// <summary>Server di gioco attuale (aggiornato da <see cref="TakeCompleted"/>).</summary>
        public NetEndpoint? Server { get; private set; }

        /// <summary>true se sono arrivati eventi TCP (traffico per programma completo: senza, solo UDP).</summary>
        public bool TcpAvailable { get; private set; }

        /// <summary>Freeze di rete negli ultimi 60 s (aggiornato da <see cref="TakeCompleted"/>).</summary>
        public int RecentFreezes { get; private set; }

        /// <summary>Errore della traccia (messaggio in italiano), su un thread in background.</summary>
        public event Action<string>? Failed;

        /// <summary>Indirizzi IP locali della macchina, per capire quale lato di un pacchetto è "remoto".</summary>
        public void SetLocalAddresses(IEnumerable<IPAddress>? addresses)
        {
            if (addresses == null) return;
            var set = new HashSet<IPAddress>();
            foreach (var a in addresses)
                if (a != null) set.Add(a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a);
            if (set.Count > 0) _local = set;
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_thread != null) return;
                _error = null;
                var run = new RunState();
                _run = run;
                _thread = new Thread(() => Run(run)) { IsBackground = true, Name = "FNBoost ETW rete" };
                _thread.Start();
            }
        }

        public void Stop()
        {
            Thread? t;
            lock (_gate)
            {
                if (_run != null) _run.Stopping = true;
                _run = null;
                t = _thread;
                _thread = null;
                try
                {
                    _session?.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warn("Chiusura sessione ETW di rete: " + ex.Message);
                }
                _session = null;
            }
            if (t != null && t != Thread.CurrentThread && !t.Join(3000))
                Log.Warn("Il thread ETW di rete non si è chiuso entro 3 secondi");
            Server = null;
            RecentFreezes = 0;
            TcpAvailable = false;
        }

        public void Dispose()
        {
            Stop();
            Failed = null;
        }

        /// <summary>
        /// Secondi completati dall'ultima chiamata (in ritardo di ~<see cref="LagMs"/>), in ordine.
        /// Da chiamare circa una volta al secondo da un solo thread.
        /// </summary>
        public List<NetSecondTraffic> TakeCompleted()
        {
            var run = _run;
            if (run == null || !run.Started || run.Stopping) return new List<NetSecondTraffic>();
            lock (_data)
            {
                double offset = Volatile.Read(ref run.OffsetMs);
                if (double.IsNaN(offset)) return new List<NetSecondTraffic>();
                double traceNow = _clock.Elapsed.TotalMilliseconds - offset;
                var list = run.Agg.Advance(traceNow - LagMs);
                Server = run.Agg.Server;
                RecentFreezes = run.Agg.CountFreezesSince(traceNow - LagMs - 60000);
                if (run.Agg.Dropped > 0 && !run.DropLogged)
                {
                    run.DropLogged = true;
                    Log.Warn("Rete: troppi pacchetti in coda, alcuni sono stati ignorati");
                }
                return list;
            }
        }

        /// <summary>
        /// Traffico per processo dei secondi completati dall'ultima chiamata (stesso ritardo di <see cref="TakeCompleted"/>).
        /// Da chiamare circa una volta al secondo dallo stesso thread di TakeCompleted.
        /// </summary>
        public List<ProcessSecond> TakeProcessSeconds()
        {
            var run = _run;
            if (run == null || !run.Started || run.Stopping) return new List<ProcessSecond>();
            double offset = Volatile.Read(ref run.OffsetMs);
            if (double.IsNaN(offset)) return new List<ProcessSecond>();
            double traceNow = _clock.Elapsed.TotalMilliseconds - offset;
            lock (_procData)
            {
                var list = run.Procs.Advance(traceNow - LagMs);
                TcpAvailable = run.Procs.TcpSeen;
                if (run.Procs.Dropped > 0 && !run.ProcDropLogged)
                {
                    run.ProcDropLogged = true;
                    Log.Warn("Rete: istanti degli eventi anomali, parte del traffico per programma ignorata");
                }
                return list;
            }
        }

        private void Run(RunState run)
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    Fail(run, "La misura del traffico di rete è disponibile solo su Windows.");
                    return;
                }
                if (TraceEventSession.IsElevated() != true)
                {
                    Fail(run, "Per misurare il traffico del gioco servono i permessi di amministratore (il ping funziona comunque).");
                    return;
                }

                ETWTraceEventSource source;
                lock (_gate)
                {
                    if (run.Stopping) return;
                    StopOrphanSession();
                    var session = new TraceEventSession(SessionName, TraceEventSessionOptions.Create)
                    {
                        StopOnDispose = true,
                        BufferSizeMB = 16
                    };
                    _session = session;
                    // Avvio completo sotto _gate e sorgente in locale: dopo un Dispose concorrente, rileggere
                    // session.Source ricreerebbe la sessione ETW (StartTrace) senza più nessuno che la chiuda.
                    source = session.Source;
                    source.AllEvents += e => OnEvent(e, run);
                    // Solo invio/ricezione UDP (42/43/58/59) e TCP (10/11/26/27): connessioni, ritrasmissioni e
                    // gli altri eventi del provider non arrivano nemmeno alla sessione.
                    var options = new TraceEventProviderOptions
                    {
                        EventIDsToEnable = new List<int>
                        {
                            UdpSendV4, UdpRecvV4, UdpSendV6, UdpRecvV6, TcpSendV4, TcpRecvV4, TcpSendV6, TcpRecvV6
                        }
                    };
                    session.EnableProvider(KernelNetworkProvider, TraceEventLevel.Verbose, NetKeywords, options);
                }

                lock (_data)
                {
                    // Il tempo della traccia parte (circa) adesso; la stima si affina con gli eventi.
                    Volatile.Write(ref run.OffsetMs, _clock.Elapsed.TotalMilliseconds);
                }
                run.Started = true;
                Log.Info("Sessione ETW di rete avviata (TCP e UDP di Kernel-Network)");

                source.Process(); // blocca finché la sessione non viene chiusa

                if (!run.Stopping) Fail(run, "La traccia di rete si è interrotta inaspettatamente (forse chiusa da un altro programma).");
            }
            catch (Exception ex)
            {
                if (run.Stopping) return;
                Log.Error("Cattura traffico di rete (ETW)", ex);
                Fail(run, ex is UnauthorizedAccessException
                    ? "Accesso negato alla traccia di rete: avvia FN Boost come amministratore."
                    : "Traffico del gioco non misurabile (traccia di rete di Windows non disponibile): " + ex.Message);
            }
            finally
            {
                run.Started = false;
            }
        }

        private static void StopOrphanSession()
        {
            try
            {
                if (!TraceEventSession.GetActiveSessionNames().Contains(SessionName)) return;
                using var old = TraceEventSession.GetActiveSession(SessionName);
                old?.Stop(true);
            }
            catch (Exception ex)
            {
                Log.Warn("Chiusura sessione ETW di rete precedente: " + ex.Message);
            }
        }

        private void Fail(RunState run, string message)
        {
            if (run.Stopping) return;
            _error = message;
            Log.Warn(message);
            try
            {
                Failed?.Invoke(message);
            }
            catch (Exception ex)
            {
                Log.Error("Gestione errore traccia di rete", ex);
            }
        }

        /// <summary>
        /// Thread ETW: arriva il traffico TCP e UDP di tutto il sistema, quindi deve essere velocissimo. Per ogni evento si
        /// leggono solo PID e dimensione (nessuna allocazione) per il traffico per processo; il resto del payload si
        /// decodifica solo per l'UDP del gioco.
        /// </summary>
        private void OnEvent(TraceEvent data, RunState run)
        {
            try
            {
                if (run.Stopping) return;
                int id = (int)data.ID;
                bool v6, recv, tcp = false;
                switch (id)
                {
                    case UdpSendV4: v6 = false; recv = false; break;
                    case UdpRecvV4: v6 = false; recv = true; break;
                    case UdpSendV6: v6 = true; recv = false; break;
                    case UdpRecvV6: v6 = true; recv = true; break;
                    case TcpSendV4: v6 = false; recv = false; tcp = true; break;
                    case TcpRecvV4: v6 = false; recv = true; tcp = true; break;
                    case TcpSendV6: v6 = true; recv = false; tcp = true; break;
                    case TcpRecvV6: v6 = true; recv = true; tcp = true; break;
                    default: return;
                }
                if (data.ProviderGuid != KernelNetworkProvider) return;

                double ts = data.TimeStampRelativeMSec;
                if (!double.IsFinite(ts)) return;

                // Stima del ritardo minimo tra l'evento e la sua ricezione (serve per sapere quando un secondo è chiuso).
                double off = _clock.Elapsed.TotalMilliseconds - ts;
                double cur = Volatile.Read(ref run.OffsetMs);
                if (double.IsNaN(cur) || off < cur) Volatile.Write(ref run.OffsetMs, off);

                int len = data.EventDataLength;
                IntPtr ptr = data.DataStart;
                if (len < 8 || ptr == IntPtr.Zero) return;
                // PID nel payload: per le ricezioni il PID dell'intestazione può essere quello di System.
                int pid = Marshal.ReadInt32(ptr);
                int size = Marshal.ReadInt32(ptr, 4);
                if (pid >= 0 && size > 0 && size <= KernelNetPayload.MaxEventBytes)
                    lock (_procData) run.Procs.Add(ts, pid, recv, size, tcp);
                if (tcp) return;

                int target = _targetPid;
                if (target <= 0 || pid != target) return;
                if (len < (v6 ? KernelNetPayload.MinLengthV6 : KernelNetPayload.MinLengthV4)) return;

                int n = Math.Min(len, run.Buffer.Length);
                Marshal.Copy(ptr, run.Buffer, 0, n);
                if (!KernelNetPayload.TryParse(new ReadOnlySpan<byte>(run.Buffer, 0, n), v6, out var ev)) return;
                var remote = KernelNetPayload.Remote(ev, !recv, _local);
                var packet = new NetPacket(ts, recv, ev.Size + (v6 ? HeaderV6 : HeaderV4), remote);

                lock (_data)
                {
                    if (run.StatePid != target)
                    {
                        run.Agg.ResetEndpoints();
                        run.StatePid = target;
                    }
                    run.Agg.Add(packet);
                }
            }
            catch (Exception ex)
            {
                if (_eventErrorLogged) return;
                _eventErrorLogged = true;
                Log.Error("Evento di rete", ex);
            }
        }
    }
}
