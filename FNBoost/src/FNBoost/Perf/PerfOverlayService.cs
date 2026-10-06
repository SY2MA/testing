using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using FNBoost.Core;

namespace FNBoost.Perf
{
    /// <summary>
    /// Gestisce l'overlay contatore FPS: accensione, aggiornamenti dal vivo (~4 Hz dal PerfService)
    /// e visibilità automatica "solo con il gioco in primo piano".
    ///
    /// La visibilità usa solo GetForegroundWindow + GetWindowThreadProcessId e il nome del processo
    /// dall'elenco di sistema (come il mirino): nessun handle aperto verso il gioco.
    /// Quando FN Boost stesso è in primo piano l'overlay resta visibile, così lo si vede mentre lo si configura.
    /// </summary>
    public sealed class PerfOverlayService : IDisposable
    {
        private readonly PerfOverlaySettings _settings;
        private readonly PerfService _perf;
        private readonly DispatcherTimer _timer;
        private readonly int _ownPid = Environment.ProcessId;
        private PerfOverlayWindow? _window;
        private uint _lastPid;
        private bool _lastPidIsFortnite;
        private long _lastTopmostMs;
        private bool _disposed;

        public event Action? StateChanged;

        public PerfOverlayService(PerfOverlaySettings settings, PerfService perf)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _perf = perf ?? throw new ArgumentNullException(nameof(perf));
            _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(300) };
            _timer.Tick += OnTick;
            _settings.PropertyChanged += OnSettingsChanged;
            _perf.LiveUpdated += OnLive;
            if (_settings.Enabled) Update();
        }

        public bool Enabled
        {
            get => _settings.Enabled;
            set => _settings.Enabled = value;
        }

        public void Toggle() => Enabled = !Enabled;

        private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_disposed) return;
            switch (e.PropertyName)
            {
                case nameof(PerfOverlaySettings.Enabled):
                    Update();
                    Log.Info(_settings.Enabled ? "Overlay FPS attivato" : "Overlay FPS disattivato");
                    StateChanged?.Invoke();
                    return;
                case nameof(PerfOverlaySettings.OnlyWhenGameFocused):
                    ApplyVisibility();
                    return;
            }
            _window?.ApplySettings();
        }

        private void Update()
        {
            if (_disposed) return;
            if (_settings.Enabled)
            {
                if (_window == null)
                {
                    try
                    {
                        _window = new PerfOverlayWindow(_settings);
                        _window.Show();
                        _window.Update(_perf.Live);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Apertura overlay FPS", ex);
                        _window?.Close();
                        _window = null;
                        _timer.Stop();
                        return;
                    }
                }
                ApplyVisibility();
                if (!_timer.IsEnabled) _timer.Start();
            }
            else
            {
                _timer.Stop();
                _window?.Close();
                _window = null;
            }
        }

        private void OnLive(LiveSnapshot snap)
        {
            if (_window == null || _disposed) return;
            // Da nascosto non serve ridisegnare: i numeri si aggiornano appena torna visibile.
            if (_window.IsVisible) _window.Update(snap);
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (_window == null) return;
            ApplyVisibility();
            var now = Environment.TickCount64;
            if (now - _lastTopmostMs >= 2000)
            {
                _lastTopmostMs = now;
                if (_window.IsVisible) _window.EnsureTopmost();
            }
        }

        private void ApplyVisibility()
        {
            if (_window == null) return;
            bool show = !_settings.OnlyWhenGameFocused || IsGameForeground();
            var vis = show ? Visibility.Visible : Visibility.Hidden;
            if (_window.Visibility == vis) return;
            _window.Visibility = vis;
            if (show)
            {
                _window.Update(_perf.Live);
                _window.EnsureTopmost();
            }
        }

        /// <summary>
        /// true se in primo piano c'è il processo misurato (stesso PID del contatore), FN Boost stesso (anteprima)
        /// oppure, mentre si attendono i primi frame, il client di Fortnite.
        /// </summary>
        private bool IsGameForeground()
        {
            var hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return false;
            if (pid == _ownPid) return true;
            var live = _perf.Live;
            if (live.HasData && live.ProcessId is int gamePid && gamePid > 0) return pid == gamePid;
            return IsFortnitePid(pid);
        }

        private bool IsFortnitePid(uint pid)
        {
            if (pid == _lastPid) return _lastPidIsFortnite;
            _lastPid = pid;
            try
            {
                // Solo il nome dall'elenco processi di sistema (nessun accesso al processo).
                using var p = Process.GetProcessById((int)pid);
                _lastPidIsFortnite = string.Equals(p.ProcessName, FortniteLocator.ClientProcessName, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                _lastPidIsFortnite = false;
            }
            return _lastPidIsFortnite;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            _timer.Tick -= OnTick;
            _settings.PropertyChanged -= OnSettingsChanged;
            _perf.LiveUpdated -= OnLive;
            _window?.Close();
            _window = null;
            StateChanged = null;
        }
    }
}
