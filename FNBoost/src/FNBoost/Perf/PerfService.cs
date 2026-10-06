using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using FNBoost.Core;

namespace FNBoost.Perf
{
    /// <summary>
    /// Motore del modulo Prestazioni: riceve i frame dalla traccia ETW, calcola le statistiche dal vivo
    /// (~4 volte al secondo, su un thread in background), campiona CPU/GPU/RAM/VRAM e registra le sessioni.
    /// Gli eventi pubblici arrivano sempre sul thread del Dispatcher che ha chiamato <see cref="Start"/>.
    /// Non apre mai handle verso il processo del gioco: nomi e PID arrivano dall'elenco processi di sistema.
    /// </summary>
    public sealed class PerfService : IDisposable
    {
        private const int TickMs = 250;
        private const int RecentCount = 600;
        private const int HistorySeconds = 120;
        /// <summary>Senza frame da così tanto (tempo di ricezione) il bersaglio non è più "attivo".</summary>
        private const double StaleMs = 2000;
        private const double AutoStartAfterMs = 5000;
        private const double AutoStopAfterMs = 10000;
        private const double MaxRecordingMs = 4 * 3600 * 1000.0;

        private readonly Func<IReadOnlyList<string>> _activeTweakIds;
        private readonly object _lock = new();     // buffer condivisi tra thread ETW, timer e UI
        private readonly object _tickLock = new(); // il tick non deve girare mentre Stop smonta capture e sampler
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly int _ownPid = Environment.ProcessId;
        private readonly Lazy<double?> _vramTotal = new(SystemSampler.ReadVramTotalGb);

        private Dispatcher _dispatcher;
        private FrameCapture? _capture;
        private SystemSampler? _sampler;
        private Timer? _timer;
        private bool _running;
        private PerfTarget _runningTarget;
        private volatile bool _disposed;
        private int _tick;

        // Bersaglio (scritti dal timer, letti dal thread ETW).
        private volatile int[] _targetPids = Array.Empty<int>();
        private volatile Dictionary<int, string>? _procNames; // istantanea pid → nome, sostituita in blocco
        private double _lastProcRefreshMs = double.NegativeInfinity;
        private volatile string? _captureError;
        private int _lastEventsLost;
        private double _lossUntilMs;

        // Dati dal vivo (sotto _lock).
        private readonly FrameRing _window = new();
        private readonly Queue<double> _fpsHistory = new();
        private int _livePid;
        private double _lastTs = double.NaN;
        private float _lastFt;
        private double _lastReceiptMs = double.NegativeInfinity;
        private double _continuousSinceTs;
        private SystemSample _sys;
        private FrameStatsResult _lastWindowStats = new();

        // Registrazione (sotto _lock).
        private Recording? _rec;
        private int _autoSuppressPid;
        private bool _autoStartPending;
        private bool _stopPending;

        // Pubblicazione verso la UI.
        private LiveSnapshot? _pendingSnapshot;
        private int _publishQueued;

        public PerfService(PerfSettings settings, Func<IReadOnlyList<string>> activeTweakIds)
        {
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _activeTweakIds = activeTweakIds ?? (() => Array.Empty<string>());
            _dispatcher = Dispatcher.CurrentDispatcher;
            Store = new PerfSessionStore(PerfSessionStore.DefaultDirectory, settings.MaxSessions);
            Live = new LiveSnapshot { Status = CaptureStatus.Stopped, StatusText = "Misurazione ferma" };
        }

        public PerfSettings Settings { get; }
        public PerfSessionStore Store { get; }

        /// <summary>Ultimo stato pubblicato (mai null). Aggiornato sul thread della UI.</summary>
        public LiveSnapshot Live { get; private set; }

        public CaptureStatus Status { get; private set; } = CaptureStatus.Stopped;

        public bool IsRecording
        {
            get
            {
                lock (_lock) return _rec != null;
            }
        }

        /// <summary>Nuovo stato dal vivo (~4 Hz), sul thread del Dispatcher.</summary>
        public event Action<LiveSnapshot>? LiveUpdated;
        /// <summary>Sessione salvata nell'archivio, sul thread del Dispatcher.</summary>
        public event Action<PerfSession>? SessionSaved;
        /// <summary>Cambio di Status (o del testo di stato), sul thread del Dispatcher.</summary>
        public event Action? StatusChanged;

        // ================= Avvio / arresto =================

        /// <summary>Avvia ETW e campionamento se <see cref="PerfSettings.CaptureEnabled"/>. Da chiamare sul thread UI; idempotente.</summary>
        public void Start()
        {
            if (_disposed) return;
            _dispatcher = Dispatcher.CurrentDispatcher;
            if (_running) return;
            if (!Settings.CaptureEnabled)
            {
                PublishNow(new LiveSnapshot { Status = CaptureStatus.Stopped, StatusText = "Contatore FPS disattivato nelle impostazioni" });
                return;
            }

            _running = true;
            _runningTarget = Settings.Target;
            _captureError = null;
            _lastEventsLost = 0;
            _lossUntilMs = 0;
            _lastProcRefreshMs = double.NegativeInfinity;
            _targetPids = Array.Empty<int>();
            ResetLiveCore();

            try
            {
                var capture = new FrameCapture();
                capture.FrameReceived += OnFrame;
                capture.Failed += OnCaptureFailed;
                _capture = capture;
                capture.Start();
            }
            catch (Exception ex)
            {
                Log.Error("Avvio cattura FPS", ex);
                _captureError = "Impossibile avviare la misura degli FPS: " + ex.Message;
            }

            var timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
            _timer = timer;
            timer.Change(0, Timeout.Infinite);

            PublishNow(new LiveSnapshot
            {
                Status = _captureError != null ? CaptureStatus.Error : CaptureStatus.WaitingForGame,
                StatusText = _captureError ?? "Avvio della misura…"
            });
            Log.Info(Settings.Target == PerfTarget.Fortnite
                ? "Contatore FPS avviato (solo Fortnite)"
                : "Contatore FPS avviato (app in primo piano)");
        }

        /// <summary>Ferma la cattura (salvando l'eventuale registrazione in corso).</summary>
        public void Stop()
        {
            if (!_running) return;
            if (IsRecording) FinishRecording(manual: false, reason: "misurazione fermata");

            _running = false;
            var timer = _timer;
            _timer = null;
            try
            {
                timer?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Chiusura timer prestazioni: " + ex.Message);
            }

            lock (_tickLock)
            {
                var capture = _capture;
                _capture = null;
                if (capture != null)
                {
                    capture.FrameReceived -= OnFrame;
                    capture.Failed -= OnCaptureFailed;
                    capture.Dispose();
                }
                _sampler?.Dispose();
                _sampler = null;
            }

            _targetPids = Array.Empty<int>();
            ResetLiveCore();
            if (!_disposed)
                PublishNow(new LiveSnapshot { Status = CaptureStatus.Stopped, StatusText = "Misurazione ferma" });
            Log.Info("Contatore FPS fermato");
        }

        /// <summary>Rilegge le impostazioni: riavvia la cattura solo se serve (attivazione, bersaglio, errore precedente).</summary>
        public void Restart()
        {
            if (_disposed) return;
            bool full = _running != Settings.CaptureEnabled || _runningTarget != Settings.Target || _captureError != null;
            if (!full)
            {
                // Finestra mobile, soglie e registrazione automatica vengono rilette a ogni tick.
                return;
            }
            Stop();
            Start();
        }

        public void Dispose()
        {
            if (_disposed) return;
            try
            {
                if (IsRecording) FinishRecording(manual: false, reason: "chiusura dell'app");
            }
            catch (Exception ex)
            {
                Log.Error("Salvataggio sessione in chiusura", ex);
            }
            _disposed = true;
            try
            {
                Stop();
            }
            catch (Exception ex)
            {
                Log.Error("Arresto contatore FPS", ex);
            }
            LiveUpdated = null;
            SessionSaved = null;
            StatusChanged = null;
        }

        // ================= Registrazione =================

        public void StartRecording(string? label = null) => StartRecordingCore(label, auto: false);

        /// <summary>Ferma la registrazione; salva solo se dura almeno <see cref="PerfSettings.MinSessionSeconds"/>.</summary>
        public PerfSession? StopRecording() => FinishRecording(manual: true, reason: null);

        public void ToggleRecording()
        {
            if (IsRecording) StopRecording();
            else StartRecording();
        }

        /// <summary>Azzera finestra mobile, minimi/massimi e storico FPS dal vivo.</summary>
        public void ResetLive()
        {
            lock (_lock)
            {
                _window.Clear();
                _fpsHistory.Clear();
                _lastWindowStats = new FrameStatsResult();
            }
        }

        private void StartRecordingCore(string? label, bool auto)
        {
            if (_disposed) return;
            lock (_lock)
            {
                _autoStartPending = false;
                if (_rec != null) return;
            }

            var rec = new Recording
            {
                StartedAt = DateTime.Now,
                Label = label?.Trim() ?? "",
                Auto = auto,
                StartReceiptMs = _clock.Elapsed.TotalMilliseconds,
                VramTotalGb = SafeVramTotal()
            };
            rec.Id = PerfSessionStore.NewId(rec.StartedAt);

            try
            {
                rec.Tweaks = (_activeTweakIds() ?? Array.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            }
            catch (Exception ex)
            {
                Log.Warn("Elenco tweak attivi non disponibile: " + ex.Message);
            }

            try
            {
                var primary = SystemDiagnostics.ReadDisplayList().FirstOrDefault();
                if (primary != null && primary.CurrentHz > 1) rec.RefreshHz = primary.CurrentHz;
            }
            catch (Exception ex)
            {
                Log.Warn("Lettura frequenza monitor: " + ex.Message);
            }

            string? liveName;
            lock (_lock)
            {
                if (_rec != null) return;
                liveName = NameOf(_livePid);
                bool fresh = _clock.Elapsed.TotalMilliseconds - _lastReceiptMs < StaleMs;
                if (_livePid != 0 && fresh)
                {
                    rec.Pid = _livePid;
                    rec.ProcessName = liveName ?? "";
                    rec.LastFrameReceiptMs = _lastReceiptMs;
                }
                _rec = rec;
                _stopPending = false;
            }

            if (Settings.Target == PerfTarget.Fortnite || IsFortnite(liveName)) ReadFortniteConfig(rec);

            Log.Info(auto
                ? $"Registrazione automatica avviata{(string.IsNullOrEmpty(rec.ProcessName) ? "" : " · " + DisplayName(rec.ProcessName))}"
                : $"Registrazione avviata{(rec.Label.Length > 0 ? " · " + rec.Label : "")}");
            QueuePublish(BuildSnapshotSafe());
        }

        private PerfSession? FinishRecording(bool manual, string? reason)
        {
            Recording? rec;
            lock (_lock)
            {
                rec = _rec;
                _rec = null;
                _stopPending = false;
                if (rec == null) return null;
                // Dopo uno stop manuale non si riparte da soli finché quel gioco continua a renderizzare.
                if (manual && rec.Pid != 0) _autoSuppressPid = rec.Pid;
            }

            try
            {
                if (string.IsNullOrEmpty(rec.ProcessName))
                    rec.ProcessName = NameOf(rec.Pid) ?? (Settings.Target == PerfTarget.Fortnite ? FortniteLocator.ClientProcessName : "");
                if (!rec.FortniteConfigRead && IsFortnite(rec.ProcessName)) ReadFortniteConfig(rec);

                var ft = rec.Ft.ToArray();
                var session = BuildSession(rec, ft);
                int min = Math.Max(0, Settings.MinSessionSeconds);
                var why = reason == null ? "" : $" ({reason})";
                if (!session.Stats.HasData || session.DurationSec < min)
                {
                    Log.Info($"Registrazione terminata{why}: {session.DurationSec:0} s, troppo breve per essere salvata (minimo {min} s)");
                    QueuePublish(BuildSnapshotSafe());
                    return null;
                }

                Store.Save(session, ft);
                Log.Info($"Sessione salvata{why}: {DisplayName(session.ProcessName)} · {session.DurationText} · " +
                         $"media {session.Stats.AvgFps:0} FPS · 1% low {session.Stats.Low1Fps:0} FPS");
                RaiseOnUi(() => SessionSaved?.Invoke(session));
                QueuePublish(BuildSnapshotSafe());
                return session;
            }
            catch (Exception ex)
            {
                Log.Error("Salvataggio sessione di prestazioni", ex);
                return null;
            }
        }

        private PerfSession BuildSession(Recording rec, float[] ft)
        {
            double factor = Settings.StutterFactor > 1 ? Settings.StutterFactor : 2.5;
            double minMs = Math.Max(0, Settings.StutterMinMs);
            var session = new PerfSession
            {
                Id = rec.Id,
                StartedAt = rec.StartedAt,
                ProcessName = rec.ProcessName,
                Label = rec.Label,
                Notes = rec.Auto ? "Registrazione automatica" : "",
                ActiveTweaks = rec.Tweaks,
                RenderMode = rec.RenderMode,
                FpsCap = rec.FpsCap,
                RefreshHz = rec.RefreshHz,
                VramTotalGb = rec.VramTotalGb,
                Stats = FrameStats.Compute(ft, factor, minMs)
            };
            if (ft.Length < 2) return session;

            // Tempo della sessione basato sui timestamp ETW (comprende le pause brevi tra i frame).
            double baseTs = rec.Ts[0] - ft[0];
            double span = rec.Ts[rec.Ts.Count - 1] - baseTs;
            session.DurationSec = span / 1000.0;

            int full = (int)(span / 1000.0);
            double rest = span - full * 1000.0;
            int nSec = full + (rest >= 500 ? 1 : 0);
            if (nSec == 0) return session;

            var flags = FrameStats.StutterFlags(ft, factor, minMs);
            var buckets = new List<float>?[nSec];
            var stutters = new int[nSec];
            for (int i = 0; i < ft.Length; i++)
            {
                // Un frame appartiene al secondo in cui termina: (k·1000, (k+1)·1000] → k.
                int sec = (int)Math.Ceiling((rec.Ts[i] - baseTs) / 1000.0) - 1;
                if (sec < 0) sec = 0;
                if (sec >= nSec) continue; // ultimo secondo parziale troppo corto
                (buckets[sec] ??= new List<float>()).Add(ft[i]);
                if (flags[i]) stutters[sec]++;
            }

            for (int s = 0; s < nSec; s++)
            {
                var b = buckets[s];
                var sample = new SecondSample { T = s, Stutters = stutters[s] };
                if (b != null && b.Count > 0)
                {
                    bool partial = s == full; // solo l'ultimo, se incluso
                    sample.Fps = partial ? b.Count * 1000.0 / rest : b.Count;
                    sample.Low1Fps = FrameStats.LowFps(b, 0.01);
                    sample.MaxFrametimeMs = b.Max();
                }
                if (rec.Sys.Count > 0)
                {
                    var sys = rec.Sys[Math.Min(s, rec.Sys.Count - 1)];
                    sample.CpuPercent = Math.Round(sys.CpuPercent, 1);
                    sample.GpuPercent = sys.GpuPercent is { } g ? Math.Round(g, 1) : null;
                    sample.RamPercent = Math.Round(sys.RamPercent, 1);
                    sample.VramUsedGb = sys.VramUsedGb is { } v ? Math.Round(v, 2) : null;
                }
                session.Seconds.Add(sample);
            }
            return session;
        }

        private static void ReadFortniteConfig(Recording rec)
        {
            rec.FortniteConfigRead = true;
            try
            {
                if (!System.IO.File.Exists(FortniteLocator.ConfigFile)) return;
                var fs = FortniteSettings.Read(FortniteLocator.ConfigFile);
                rec.RenderMode = fs.RenderMode == RenderMode.Unknown ? null : fs.RenderMode.ToString();
                rec.FpsCap = fs.FrameRateLimit > 0 ? fs.FrameRateLimit : null;
            }
            catch (Exception ex)
            {
                Log.Warn("Lettura impostazioni di Fortnite per la sessione: " + ex.Message);
            }
        }

        // ================= Thread ETW =================

        private void OnCaptureFailed(string message) => _captureError = message;

        /// <summary>Frame dal thread ETW: deve essere velocissimo.</summary>
        private void OnFrame(int pid, double ts, float ft)
        {
            var targets = _targetPids;
            if (Array.IndexOf(targets, pid) < 0) return;
            double now = _clock.Elapsed.TotalMilliseconds;

            lock (_lock)
            {
                var rec = _rec;
                if (rec != null)
                {
                    if (rec.Pid == 0)
                    {
                        rec.Pid = pid;
                        rec.ProcessName = NameOf(pid) ?? "";
                    }
                    // Mentre si registra, anche il "dal vivo" segue solo il processo registrato.
                    if (rec.Pid != pid) return;
                    rec.LastFrameReceiptMs = now;
                    if (rec.Ts.Count == 0 || ts - rec.Ts[0] < MaxRecordingMs)
                    {
                        rec.Ft.Add(ft);
                        rec.Ts.Add(ts);
                    }
                }

                if (pid != _livePid)
                {
                    // Un altro bersaglio sta ancora renderizzando: si resta su quello (salvo registrazione in corso).
                    if (rec == null && _livePid != 0 && now - _lastReceiptMs < 1000) return;
                    _livePid = pid;
                    _window.Clear();
                    _lastTs = double.NaN;
                }

                if (double.IsNaN(_lastTs) || ts - _lastTs > FrameCapture.BreakMs + 1)
                    _continuousSinceTs = ts - ft;
                _window.Add(ts, ft);
                _lastTs = ts;
                _lastFt = ft;
                _lastReceiptMs = now;
            }
        }

        // ================= Timer (4 Hz) =================

        private void OnTimer(object? state)
        {
            try
            {
                lock (_tickLock)
                {
                    if (_disposed || !_running) return;
                    Tick();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Aggiornamento contatore FPS", ex);
                _captureError ??= "Errore interno del contatore FPS: " + ex.Message;
            }
            finally
            {
                try
                {
                    if (_running && !_disposed) _timer?.Change(TickMs, Timeout.Infinite);
                }
                catch (ObjectDisposedException)
                {
                    // Stop in corso.
                }
            }
        }

        private void Tick()
        {
            _tick++;
            double now = _clock.Elapsed.TotalMilliseconds;
            bool secondTick = _tick % 4 == 1;

            // ---- 1. bersaglio ----
            UpdateTargets(now);

            // ---- 2. sistema (1 Hz) ----
            if (secondTick)
            {
                _sampler ??= CreateSampler();
                int livePid;
                lock (_lock) livePid = _livePid;
                var sys = _sampler?.Sample(livePid != 0 ? livePid : null) ?? default;
                lock (_lock)
                {
                    _sys = sys;
                    _rec?.Sys.Add(sys);
                }
            }

            // ---- 3. perdite di eventi ETW ----
            var capture = _capture;
            if (capture != null && _tick % 8 == 0)
            {
                int lost = capture.EventsLost;
                if (lost > _lastEventsLost)
                {
                    Log.Warn($"ETW: {lost - _lastEventsLost} eventi persi (sistema molto carico)");
                    _lossUntilMs = now + 5000;
                }
                _lastEventsLost = lost;
            }

            // ---- 4. statistiche dal vivo ----
            var snap = BuildSnapshot(now, secondTick);

            // ---- 5. registrazione automatica ----
            AutoRecord(now, snap);

            QueuePublish(snap);
        }

        private SystemSampler? CreateSampler()
        {
            try
            {
                return new SystemSampler();
            }
            catch (Exception ex)
            {
                Log.Error("Campionamento di sistema", ex);
                return null;
            }
        }

        private void UpdateTargets(double now)
        {
            bool fortnite = Settings.Target == PerfTarget.Fortnite;
            int fgPid = 0;
            if (!fortnite)
            {
                try
                {
                    var hwnd = Native.GetForegroundWindow();
                    if (hwnd != IntPtr.Zero && Native.GetWindowThreadProcessId(hwnd, out var p) != 0) fgPid = (int)p;
                }
                catch
                {
                    fgPid = 0;
                }
                if (fgPid == _ownPid) fgPid = 0;
            }

            // Elenco processi ogni ~2 s (o subito se il primo piano è un processo nuovo).
            var names = _procNames;
            bool needRefresh = now - _lastProcRefreshMs >= 2000 ||
                               (fgPid != 0 && names != null && !names.ContainsKey(fgPid) && now - _lastProcRefreshMs >= 500);
            if (needRefresh)
            {
                _lastProcRefreshMs = now;
                names = SnapshotProcesses() ?? names;
                _procNames = names;
            }

            var list = new List<int>(4);
            if (fortnite)
            {
                if (names != null)
                    foreach (var kv in names)
                        if (string.Equals(kv.Value, FortniteLocator.ClientProcessName, StringComparison.OrdinalIgnoreCase))
                            list.Add(kv.Key);
            }
            else if (fgPid != 0)
            {
                list.Add(fgPid);
            }

            int recPid;
            lock (_lock) recPid = _rec?.Pid ?? 0;
            if (recPid != 0 && !list.Contains(recPid)) list.Add(recPid);
            _targetPids = list.ToArray();
        }

        /// <summary>
        /// pid → nome di tutti i processi. Process.GetProcesses legge l'elenco di sistema
        /// (NtQuerySystemInformation) senza aprire handle verso i processi.
        /// </summary>
        private static Dictionary<int, string>? SnapshotProcesses()
        {
            try
            {
                var procs = Process.GetProcesses();
                var map = new Dictionary<int, string>(procs.Length);
                foreach (var p in procs)
                {
                    try
                    {
                        map[p.Id] = p.ProcessName;
                    }
                    catch
                    {
                        // Processo terminato nel frattempo.
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
                return map;
            }
            catch (Exception ex)
            {
                Log.Warn("Elenco processi non disponibile: " + ex.Message);
                return null;
            }
        }

        private LiveSnapshot BuildSnapshot(double now, bool secondTick)
        {
            int windowSec = Math.Clamp(Settings.WindowSeconds, 5, 300);
            double factor = Settings.StutterFactor > 1 ? Settings.StutterFactor : 2.5;
            double minMs = Math.Max(0, Settings.StutterMinMs);

            float[] windowFt, recent;
            int livePid;
            double lastTs, lastReceipt, currentFps, recSeconds = 0;
            float lastFt;
            bool recording;
            SystemSample sys;
            double[] history;
            lock (_lock)
            {
                livePid = _livePid;
                lastTs = _lastTs;
                lastReceipt = _lastReceiptMs;
                lastFt = _lastFt;
                bool fresh = now - lastReceipt < StaleMs;

                // Bersaglio fermo da molto: si riparte puliti alla prossima ripresa.
                if (!fresh && now - lastReceipt > 5000 && _window.Count > 0) _window.Clear();
                if (!double.IsNaN(lastTs)) _window.TrimBefore(lastTs - windowSec * 1000.0);

                windowFt = _window.CopyFrametimes(_window.Count);
                recent = _window.CopyFrametimes(RecentCount);
                currentFps = fresh && !double.IsNaN(lastTs) ? _window.CountSince(lastTs - 1000.0) : 0;

                if (secondTick)
                {
                    _fpsHistory.Enqueue(currentFps);
                    while (_fpsHistory.Count > HistorySeconds) _fpsHistory.Dequeue();
                }
                history = _fpsHistory.ToArray();
                recording = _rec != null;
                if (_rec != null) recSeconds = Math.Max(0, (now - _rec.StartReceiptMs) / 1000.0);
                sys = _sys;
            }

            // Con finestre enormi si ricalcola a 2 Hz per non sprecare CPU.
            FrameStatsResult stats;
            if (windowFt.Length > 50000 && _tick % 2 == 0) stats = _lastWindowStats;
            else stats = FrameStats.Compute(windowFt, factor, minMs);
            lock (_lock) _lastWindowStats = stats;

            bool active = now - lastReceipt < StaleMs && livePid != 0;
            var name = livePid != 0 ? NameOf(livePid) : null;
            if (name == null && livePid != 0 && Settings.Target == PerfTarget.Fortnite) name = FortniteLocator.ClientProcessName;

            var (status, text) = ComputeStatus(now, active, name);
            string? shownName = active ? name : null;
            int? shownPid = active ? livePid : null;
            if (!active && Settings.Target == PerfTarget.Fortnite)
            {
                // Fortnite aperto ma fermo (caricamento, minimizzato): il bersaglio resta comunque lui.
                var targets = _targetPids;
                if (targets.Length > 0)
                {
                    shownName = FortniteLocator.ClientProcessName;
                    shownPid = targets[0];
                }
            }
            return new LiveSnapshot
            {
                HasData = active && stats.HasData,
                ProcessName = shownName,
                ProcessId = shownPid,
                CurrentFps = currentFps,
                LastFrametimeMs = active ? lastFt : 0,
                Window = stats,
                RecentFrametimes = recent,
                FpsHistory = history,
                CpuPercent = sys.CpuPercent,
                GpuPercent = sys.GpuPercent,
                RamPercent = sys.RamPercent,
                VramUsedGb = sys.VramUsedGb,
                VramTotalGb = SafeVramTotal(),
                IsRecording = recording,
                RecordingSeconds = recSeconds,
                Status = status,
                StatusText = text
            };
        }

        private (CaptureStatus, string) ComputeStatus(double now, bool active, string? name)
        {
            var err = _captureError;
            if (err != null) return (CaptureStatus.Error, err);
            if (now < _lossUntilMs)
                return (CaptureStatus.Error, "Alcuni eventi ETW sono andati persi: le misure di questi secondi potrebbero essere imprecise.");
            if (active) return (CaptureStatus.Capturing, "Misurazione attiva · " + DisplayName(name));
            if (Settings.Target == PerfTarget.Fortnite)
            {
                bool running = _targetPids.Length > 0;
                return (CaptureStatus.WaitingForGame, running
                    ? "Fortnite è aperto: in attesa dei frame…"
                    : "In attesa di Fortnite…");
            }
            return (CaptureStatus.WaitingForGame, "In attesa di un gioco in primo piano…");
        }

        private void AutoRecord(double now, LiveSnapshot snap)
        {
            bool postStart = false;
            string? stopReason = null;
            lock (_lock)
            {
                var rec = _rec;
                bool fresh = now - _lastReceiptMs < StaleMs;

                // Lo stop manuale vale finché quel processo smette di renderizzare per un po'.
                if (_autoSuppressPid != 0 && (_livePid != _autoSuppressPid || now - _lastReceiptMs >= AutoStopAfterMs))
                    _autoSuppressPid = 0;

                if (rec == null)
                {
                    if (Settings.AutoRecord && Settings.CaptureEnabled && fresh && _livePid != 0 &&
                        _livePid != _autoSuppressPid && !_autoStartPending && !double.IsNaN(_lastTs) &&
                        _lastTs - _continuousSinceTs >= AutoStartAfterMs)
                    {
                        _autoStartPending = true;
                        postStart = true;
                    }
                }
                else if (!_stopPending)
                {
                    var names = _procNames;
                    if (now - rec.StartReceiptMs >= MaxRecordingMs)
                        stopReason = "durata massima di 4 ore";
                    else if (rec.Pid != 0 && names != null && !names.ContainsKey(rec.Pid) && now - rec.LastFrameReceiptMs > StaleMs)
                        stopReason = "gioco chiuso";
                    else if (rec.Auto && rec.Pid != 0 && now - rec.LastFrameReceiptMs >= AutoStopAfterMs)
                        stopReason = "nessun frame da 10 secondi";
                    if (stopReason != null) _stopPending = true;
                }
            }

            if (postStart) Post(() => StartRecordingCore(null, auto: true));
            if (stopReason != null)
            {
                bool suppress = stopReason.StartsWith("durata", StringComparison.Ordinal);
                Post(() => FinishRecording(manual: suppress, reason: stopReason));
            }
        }

        // ================= Pubblicazione sulla UI =================

        private LiveSnapshot BuildSnapshotSafe()
        {
            try
            {
                lock (_tickLock)
                {
                    if (!_running || _disposed) return Live;
                    return BuildSnapshot(_clock.Elapsed.TotalMilliseconds, false);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Stato contatore FPS", ex);
                return Live;
            }
        }

        /// <summary>Accoda l'ultimo snapshot: se la UI è indietro, si salta direttamente al più recente.</summary>
        private void QueuePublish(LiveSnapshot snap)
        {
            Volatile.Write(ref _pendingSnapshot, snap);
            if (Interlocked.Exchange(ref _publishQueued, 1) == 1) return;
            Post(() =>
            {
                Interlocked.Exchange(ref _publishQueued, 0);
                var s = Interlocked.Exchange(ref _pendingSnapshot, null);
                if (s != null) Apply(s);
            }, onFail: () => Interlocked.Exchange(ref _publishQueued, 0));
        }

        /// <summary>Pubblica subito (solo dal thread UI).</summary>
        private void PublishNow(LiveSnapshot snap)
        {
            if (_dispatcher.CheckAccess())
            {
                // Uno snapshot accodato prima di questo è ormai vecchio (es. "in misura" dopo Stop).
                Interlocked.Exchange(ref _pendingSnapshot, null);
                Apply(snap);
            }
            else
            {
                QueuePublish(snap);
            }
        }

        private void Apply(LiveSnapshot snap)
        {
            if (_disposed) return;
            if (!_running && snap.Status != CaptureStatus.Stopped) return; // arrivato dopo uno Stop
            bool statusChanged = snap.Status != Status || snap.StatusText != Live.StatusText;
            Live = snap;
            Status = snap.Status;
            try
            {
                LiveUpdated?.Invoke(snap);
                if (statusChanged) StatusChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error("Aggiornamento interfaccia prestazioni", ex);
            }
        }

        private void RaiseOnUi(Action action)
        {
            if (_dispatcher.CheckAccess())
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Error("Evento prestazioni", ex);
                }
            }
            else
            {
                Post(action);
            }
        }

        private void Post(Action action, Action? onFail = null)
        {
            try
            {
                var d = _dispatcher;
                if (d.HasShutdownStarted || d.HasShutdownFinished)
                {
                    onFail?.Invoke();
                    return;
                }
                d.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Prestazioni (thread UI)", ex);
                    }
                }));
            }
            catch (Exception ex)
            {
                onFail?.Invoke();
                Log.Warn("Dispatcher non disponibile: " + ex.Message);
            }
        }

        // ================= Utilità =================

        private void ResetLiveCore()
        {
            lock (_lock)
            {
                _window.Clear();
                _fpsHistory.Clear();
                _livePid = 0;
                _lastTs = double.NaN;
                _lastFt = 0;
                _lastReceiptMs = double.NegativeInfinity;
                _continuousSinceTs = 0;
                _sys = default;
                _lastWindowStats = new FrameStatsResult();
                _autoStartPending = false;
                _stopPending = false;
                _autoSuppressPid = 0;
            }
        }

        private string? NameOf(int pid)
        {
            if (pid == 0) return null;
            var names = _procNames;
            return names != null && names.TryGetValue(pid, out var n) ? n : null;
        }

        private double? SafeVramTotal()
        {
            try
            {
                return _vramTotal.Value;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsFortnite(string? name) =>
            string.Equals(name, FortniteLocator.ClientProcessName, StringComparison.OrdinalIgnoreCase);

        private static string DisplayName(string? name) =>
            string.IsNullOrEmpty(name) ? "gioco" : IsFortnite(name) ? "Fortnite" : name;

        /// <summary>Registrazione in corso (protetta da _lock, tranne i metadati scritti prima di pubblicarla).</summary>
        private sealed class Recording
        {
            public string Id = "";
            public DateTime StartedAt;
            public string Label = "";
            public bool Auto;
            public int Pid;
            public string ProcessName = "";
            public readonly List<float> Ft = new();
            public readonly List<double> Ts = new();
            public readonly List<SystemSample> Sys = new();
            public double StartReceiptMs;
            public double LastFrameReceiptMs;
            public List<string> Tweaks = new();
            public string? RenderMode;
            public double? FpsCap;
            public int? RefreshHz;
            public double? VramTotalGb;
            public bool FortniteConfigRead;
        }

        /// <summary>Buffer circolare (istante, frametime) che cresce quando serve.</summary>
        private sealed class FrameRing
        {
            private double[] _ts = new double[4096];
            private float[] _ft = new float[4096];
            private int _start, _count;

            public int Count => _count;

            public void Add(double ts, float ft)
            {
                if (_count == _ts.Length) Grow();
                int i = (_start + _count) % _ts.Length;
                _ts[i] = ts;
                _ft[i] = ft;
                _count++;
            }

            public void TrimBefore(double minTs)
            {
                while (_count > 0 && _ts[_start] < minTs)
                {
                    _start = (_start + 1) % _ts.Length;
                    _count--;
                }
            }

            public int CountSince(double minTs)
            {
                int c = 0;
                for (int k = _count - 1; k >= 0; k--)
                {
                    if (_ts[(_start + k) % _ts.Length] <= minTs) break;
                    c++;
                }
                return c;
            }

            /// <summary>Ultimi n frametime, dal più vecchio al più recente.</summary>
            public float[] CopyFrametimes(int n)
            {
                n = Math.Min(n, _count);
                var result = new float[n];
                int first = _count - n;
                for (int k = 0; k < n; k++) result[k] = _ft[(_start + first + k) % _ft.Length];
                return result;
            }

            public void Clear()
            {
                _start = 0;
                _count = 0;
            }

            private void Grow()
            {
                int cap = _ts.Length * 2;
                var ts = new double[cap];
                var ft = new float[cap];
                for (int k = 0; k < _count; k++)
                {
                    int i = (_start + k) % _ts.Length;
                    ts[k] = _ts[i];
                    ft[k] = _ft[i];
                }
                _ts = ts;
                _ft = ft;
                _start = 0;
            }
        }
    }
}
