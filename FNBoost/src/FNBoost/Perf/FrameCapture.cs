using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using FNBoost.Core;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace FNBoost.Perf
{
    /// <summary>
    /// Legge gli eventi "Present" di DXGI tramite una sessione ETW di sistema, esattamente come fanno
    /// PresentMon e il contatore FPS della Xbox Game Bar. Non tocca in alcun modo il processo del gioco:
    /// nessun handle, nessuna DLL, nessuna lettura di memoria. Windows stesso registra quando ogni app
    /// presenta un frame e noi ascoltiamo quella traccia.
    /// </summary>
    internal sealed class FrameCapture : IDisposable
    {
        public const string SessionName = "FNBoost-FrameCapture";

        /// <summary>Microsoft-Windows-DXGI</summary>
        private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
        /// <summary>Keyword Analytic (0x8000000000000000) | Events (0x2).</summary>
        private const ulong DxgiKeywords = 0x8000000000000002UL;
        /// <summary>Present_Start.</summary>
        private const int PresentStartId = 42;
        /// <summary>DXGI_PRESENT_TEST: la present non mostra nulla, va ignorata.</summary>
        private const uint DxgiPresentTest = 0x1;
        /// <summary>Limite di visualizzazione dei grafici: i frametime oltre questo valore vengono "schiacciati" qui.</summary>
        public const double BreakMs = 500;
        /// <summary>
        /// Oltre questa pausa tra due frame non è un frametime ma un'interruzione (gioco ridotto a icona, processo fermo…).
        /// I blocchi reali di 0,5-5 s (compilazione shader, streaming, DPC) restano frametime: sono gli scatti peggiori.
        /// </summary>
        public const double PauseMs = 5000;

        private readonly object _gate = new();
        private TraceEventSession? _session;
        private Thread? _thread;
        private RunState? _run;
        private bool _eventErrorLogged;

        /// <summary>Stato di una singola esecuzione: dopo Stop/Start rapidi il vecchio thread non deve segnalare errori.</summary>
        private sealed class RunState
        {
            public volatile bool Stopping;
            /// <summary>Ultima present valida per processo (usato solo dal thread ETW di questa esecuzione).</summary>
            public readonly Dictionary<int, double> LastPresent = new();
            public double LastCleanup;
        }

        /// <summary>Frame valido: (pid, istante in ms dall'avvio della sessione, frametime ms). Chiamato sul thread ETW.</summary>
        public event Action<int, double, float>? FrameReceived;

        /// <summary>Errore della cattura (messaggio in italiano). Chiamato su un thread in background.</summary>
        public event Action<string>? Failed;

        public bool IsRunning
        {
            get
            {
                lock (_gate) return _thread != null;
            }
        }

        /// <summary>Eventi persi dalla sessione ETW (buffer pieni). 0 se non disponibile.</summary>
        public int EventsLost
        {
            get
            {
                try
                {
                    lock (_gate) return _session?.EventsLost ?? 0;
                }
                catch
                {
                    return 0;
                }
            }
        }

        /// <summary>Avvia la cattura su un thread dedicato (l'avvio di ETW può richiedere qualche istante).</summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_thread != null) return;
                var run = new RunState();
                _run = run;
                _thread = new Thread(() => Run(run)) { IsBackground = true, Name = "FNBoost ETW", Priority = ThreadPriority.AboveNormal };
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
                    // Dispose su una sessione real-time fa terminare Process() sull'altro thread.
                    _session?.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warn("Chiusura sessione ETW: " + ex.Message);
                }
                _session = null;
            }
            if (t != null && t != Thread.CurrentThread && !t.Join(3000))
                Log.Warn("Il thread ETW non si è chiuso entro 3 secondi");
        }

        public void Dispose() => Stop();

        private void Run(RunState run)
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    Fail("La misura degli FPS è disponibile solo su Windows.");
                    return;
                }
                if (TraceEventSession.IsElevated() != true)
                {
                    Fail("Per misurare gli FPS servono i permessi di amministratore: riavvia FN Boost come amministratore.");
                    return;
                }

                ETWTraceEventSource source;
                lock (_gate)
                {
                    if (run.Stopping) return;
                    StopOrphanSession();
                    // Create (predefinito): se esiste già una sessione con lo stesso nome viene fermata e ricreata.
                    var session = new TraceEventSession(SessionName, TraceEventSessionOptions.Create)
                    {
                        StopOnDispose = true,
                        BufferSizeMB = 32
                    };
                    _session = session;
                    // Tutto l'avvio resta sotto _gate: Stop() può chiudere la sessione solo dopo, e la sorgente
                    // è tenuta in locale. Rileggere session.Source dopo un Dispose ricreerebbe la sessione ETW
                    // (StartTrace) senza più nessuno che la chiuda.
                    source = session.Source;
                    // AllEvents riceve ogni evento, anche quelli senza un parser registrato (come i nostri DXGI "grezzi").
                    source.AllEvents += e => OnEvent(e, run);
                    session.EnableProvider(DxgiProvider, TraceEventLevel.Informational, DxgiKeywords);
                }
                Log.Info("Sessione ETW avviata (eventi Present di DXGI)");

                source.Process(); // blocca finché la sessione non viene chiusa

                if (!run.Stopping) Fail("La sessione ETW si è interrotta inaspettatamente (forse chiusa da un altro programma).");
            }
            catch (Exception ex)
            {
                if (run.Stopping) return;
                Log.Error("Cattura FPS (ETW)", ex);
                Fail(ex is UnauthorizedAccessException
                    ? "Accesso negato alla traccia ETW: avvia FN Boost come amministratore."
                    : "Impossibile avviare la misura degli FPS (ETW): " + ex.Message);
            }
        }

        private static void StopOrphanSession()
        {
            try
            {
                // Una sessione rimasta aperta da un'esecuzione precedente (crash) va chiusa.
                if (!TraceEventSession.GetActiveSessionNames().Contains(SessionName)) return;
                using var old = TraceEventSession.GetActiveSession(SessionName);
                old?.Stop(true);
            }
            catch (Exception ex)
            {
                Log.Warn("Chiusura sessione ETW precedente: " + ex.Message);
            }
        }

        private void Fail(string message)
        {
            Log.Warn(message);
            try
            {
                Failed?.Invoke(message);
            }
            catch (Exception ex)
            {
                Log.Error("Gestione errore cattura", ex);
            }
        }

        private void OnEvent(TraceEvent data, RunState run)
        {
            try
            {
                if (run.Stopping) return;
                if ((int)data.ID != PresentStartId || data.ProviderGuid != DxgiProvider) return;

                // Payload Present_Start: pIDXGISwapChain (puntatore), Flags (UInt32), SyncInterval (Int32).
                int ptr = data.PointerSize;
                if (ptr != 4 && ptr != 8) ptr = 8;
                if (data.EventDataLength < ptr + 4 || data.DataStart == IntPtr.Zero) return;
                uint flags = unchecked((uint)Marshal.ReadInt32(data.DataStart, ptr));
                if ((flags & DxgiPresentTest) != 0) return;

                int pid = data.ProcessID;
                double ts = data.TimeStampRelativeMSec;
                if (pid <= 0 || double.IsNaN(ts)) return;

                var lastPresent = run.LastPresent;
                if (lastPresent.TryGetValue(pid, out var last))
                {
                    double ft = ts - last;
                    lastPresent[pid] = ts;
                    if (ft > 0 && ft <= PauseMs)
                        FrameReceived?.Invoke(pid, ts, (float)ft);
                    // ft > PauseMs: pausa (gioco ridotto a icona, processo fermo) → non è un frametime, si riparte da qui.
                }
                else
                {
                    lastPresent[pid] = ts;
                }

                // Pulizia periodica dei processi che non presentano più.
                if (ts - run.LastCleanup > 10000)
                {
                    run.LastCleanup = ts;
                    var stale = new List<int>();
                    foreach (var kv in lastPresent)
                        if (ts - kv.Value > 10000) stale.Add(kv.Key);
                    foreach (var k in stale) lastPresent.Remove(k);
                }
            }
            catch (Exception ex)
            {
                // Mai far cadere il thread ETW per un singolo evento (e non inondare il log).
                if (_eventErrorLogged) return;
                _eventErrorLogged = true;
                Log.Error("Evento Present", ex);
            }
        }
    }
}
