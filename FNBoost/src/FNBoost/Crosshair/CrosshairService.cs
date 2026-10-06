using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
using FNBoost.Core;

namespace FNBoost.Crosshair
{
    /// <summary>Gestisce la finestra overlay: accensione, aggiornamenti dal vivo, visibilità automatica.</summary>
    public sealed class CrosshairService : IDisposable
    {
        private readonly CrosshairSettings _settings;
        private readonly DispatcherTimer _timer;
        private CrosshairOverlayWindow? _window;
        private uint _lastPid;
        private bool _lastPidIsFortnite;
        private int _ticks;

        public event Action? StateChanged;

        public CrosshairService(CrosshairSettings settings)
        {
            _settings = settings;
            _settings.PropertyChanged += OnSettingsChanged;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
            _timer.Tick += (_, _) => Tick();
        }

        public bool Enabled
        {
            get => _settings.Enabled;
            set => _settings.Enabled = value;
        }

        public void Toggle() => Enabled = !Enabled;

        private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CrosshairSettings.Enabled))
            {
                Update();
                Log.Info(_settings.Enabled ? "Mirino attivato" : "Mirino disattivato");
                StateChanged?.Invoke();
                return;
            }
            _window?.Reposition();
        }

        private void Update()
        {
            if (_settings.Enabled)
            {
                if (_window == null)
                {
                    _window = new CrosshairOverlayWindow(_settings);
                    _window.Show();
                }
                ApplyVisibility();
                _timer.Start();
            }
            else
            {
                _timer.Stop();
                _window?.Close();
                _window = null;
            }
        }

        private void Tick()
        {
            if (_window == null) return;
            ApplyVisibility();
            if (++_ticks % 5 == 0) _window.EnsureTopmost();
        }

        private void ApplyVisibility()
        {
            if (_window == null) return;
            bool show = !_settings.OnlyWhenFortniteFocused || IsFortniteForeground();
            var vis = show ? System.Windows.Visibility.Visible : System.Windows.Visibility.Hidden;
            if (_window.Visibility != vis)
            {
                _window.Visibility = vis;
                if (show)
                {
                    _window.Reposition();
                    _window.EnsureTopmost();
                }
            }
        }

        /// <summary>
        /// Controlla solo QUALE finestra è in primo piano (GetForegroundWindow) e il nome del suo processo
        /// dall'elenco processi di sistema: nessun handle aperto verso il gioco.
        /// </summary>
        private bool IsFortniteForeground()
        {
            var hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == _lastPid) return _lastPidIsFortnite;
            _lastPid = pid;
            try
            {
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
            _timer.Stop();
            _settings.PropertyChanged -= OnSettingsChanged;
            _window?.Close();
            _window = null;
        }
    }
}
