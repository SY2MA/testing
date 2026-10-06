using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using FNBoost.Core;

namespace FNBoost.Crosshair
{
    /// <summary>
    /// Gestisce la finestra overlay: accensione, aggiornamenti dal vivo, visibilità automatica e preset.
    ///
    /// Visibilità automatica (tutto in sola lettura, niente hook e niente contatto con il gioco):
    ///  • "solo con Fortnite in primo piano": GetForegroundWindow + nome del processo dall'elenco di sistema;
    ///  • "nascondi nei menu": GetCursorInfo (in partita il cursore è nascosto, in menu/inventario/mappa è visibile);
    ///  • "nascondi mentre miri": GetAsyncKeyState del tasto destro (solo lettura dello stato del pulsante).
    /// Un solo DispatcherTimer: 400 ms normalmente, ~60 Hz solo se l'overlay è acceso e una regola
    /// "cursore"/"mira" è attiva (servono reazioni immediate). Spento quando l'overlay è spento.
    /// </summary>
    public sealed class CrosshairService : IDisposable
    {
        private static readonly TimeSpan SlowPoll = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(16);

        private readonly CrosshairSettings _settings;
        private readonly DispatcherTimer _timer;
        private CrosshairOverlayWindow? _window;
        private uint _lastPid;
        private bool _lastPidIsFortnite;
        private long _lastTopmostMs;
        private bool _rbWasDown;
        private bool _aimToggled;
        private string? _lastPresetName;
        private bool _disposed;

        public event Action? StateChanged;

        /// <summary>Un preset è stato applicato da <see cref="NextPreset"/> o <see cref="ApplyPreset"/> (argomento: nome).</summary>
        public event Action<string>? PresetApplied;

        public CrosshairService(CrosshairSettings settings)
        {
            _settings = settings;
            // App.xaml.cs non converte le impostazioni dopo AppSettings.Load: lo facciamo qui per il mirino
            // principale (no-op se già v2). I preset vengono convertiti in modo pigro (NextPreset, pagina Mirino, CopyFrom).
            _settings.NormalizeLegacy();
            _settings.PropertyChanged += OnSettingsChanged;
            _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = SlowPoll };
            _timer.Tick += OnTick;
        }

        public bool Enabled
        {
            get => _settings.Enabled;
            set => _settings.Enabled = value;
        }

        public void Toggle() => Enabled = !Enabled;

        /// <summary>
        /// Applica il preset successivo di App.Settings.CrosshairPresets (ciclico) e ne restituisce il nome
        /// (null se non ci sono preset). Pensato per la scorciatoia globale HotkeyNextPreset.
        /// </summary>
        public string? NextPreset()
        {
            var list = App.Settings?.CrosshairPresets;
            if (list == null) return null;
            CrosshairPreset.NormalizeAll(list);
            if (list.Count == 0) return null;
            int idx = _lastPresetName == null ? -1 : list.FindIndex(p => p.Name == _lastPresetName);
            var next = list[(idx + 1) % list.Count];
            ApplyPreset(next);
            return next.Name;
        }

        /// <summary>Applica un preset (solo aspetto) e lo ricorda come punto di partenza per NextPreset.</summary>
        public void ApplyPreset(CrosshairPreset preset)
        {
            if (preset?.Settings == null) return; // preset rovinato nel JSON: niente da applicare
            _settings.CopyFrom(preset.Settings);
            _lastPresetName = preset.Name;
            Log.Info($"Preset mirino: {preset.Name}");
            if (_settings.ShowPresetName) _window?.ShowPresetLabel(preset.Name);
            PresetApplied?.Invoke(preset.Name);
        }

        private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(CrosshairSettings.Enabled):
                    Update();
                    Log.Info(_settings.Enabled ? "Mirino attivato" : "Mirino disattivato");
                    StateChanged?.Invoke();
                    return;
                case nameof(CrosshairSettings.OnlyWhenFortniteFocused):
                case nameof(CrosshairSettings.HideWhenCursorVisible):
                case nameof(CrosshairSettings.HideWhileAiming):
                case nameof(CrosshairSettings.AimToggleMode):
                    ResetAim();
                    UpdateTimer();
                    ApplyVisibility();
                    return;
                case nameof(CrosshairSettings.ShowPresetName):
                    return;
            }
            _window?.Reposition();
        }

        private void Update()
        {
            if (_disposed) return;
            if (_settings.Enabled)
            {
                if (_window == null)
                {
                    _window = new CrosshairOverlayWindow(_settings);
                    _window.Show();
                }
                ResetAim();
                ApplyVisibility();
            }
            else
            {
                _window?.Close();
                _window = null;
            }
            UpdateTimer();
        }

        private bool NeedsFastPoll => _settings.HideWhenCursorVisible || _settings.HideWhileAiming;

        /// <summary>Un solo timer: veloce o lento a seconda delle regole attive, fermo se l'overlay è spento.</summary>
        private void UpdateTimer()
        {
            if (_disposed || !_settings.Enabled || _window == null)
            {
                _timer.Stop();
                return;
            }
            var interval = NeedsFastPoll ? FastPoll : SlowPoll;
            if (_timer.Interval != interval) _timer.Interval = interval;
            if (!_timer.IsEnabled) _timer.Start();
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

        /// <summary>
        /// Tasti che in Fortnite fanno uscire dalla mira (cambio slot 1-6, modalità costruzione Q e F1-F6 nei
        /// comandi predefiniti). In modalità alternata azzerano lo stato, così non resta "nascosto" dopo un cambio arma.
        /// </summary>
        private static readonly int[] AimCancelKeys =
        {
            0x31, 0x32, 0x33, 0x34, 0x35, 0x36, // 1-6
            0x51,                               // Q
            0x70, 0x71, 0x72, 0x73, 0x74, 0x75  // F1-F6
        };
        private readonly bool[] _cancelWasDown = new bool[AimCancelKeys.Length];

        private void ResetAim()
        {
            _aimToggled = false;
            _rbWasDown = false;
            Array.Clear(_cancelWasDown);
        }

        /// <summary>true se uno dei tasti che annullano la mira è appena stato premuto (fronte di discesa, sola lettura).</summary>
        private bool AimCancelPressed()
        {
            bool pressed = false;
            for (int i = 0; i < AimCancelKeys.Length; i++)
            {
                bool down = (Native.GetAsyncKeyState(AimCancelKeys[i]) & 0x8000) != 0;
                if (down && !_cancelWasDown[i]) pressed = true;
                _cancelWasDown[i] = down;
            }
            return pressed;
        }

        private void ApplyVisibility()
        {
            if (_window == null) return;

            bool toggleAim = _settings.HideWhileAiming && _settings.AimToggleMode;
            bool fortniteFg = (_settings.OnlyWhenFortniteFocused || toggleAim) && IsFortniteForeground();
            bool cursorVisible = (_settings.HideWhenCursorVisible || toggleAim) && Native.IsCursorVisible();

            bool show = true;
            if (_settings.OnlyWhenFortniteFocused && !fortniteFg) show = false;
            if (_settings.HideWhenCursorVisible && cursorVisible) show = false;
            // La mira va valutata a ogni giro (anche se già nascosto) per non perdere i clic in modalità alternata.
            if (_settings.HideWhileAiming && IsAiming(fortniteFg, cursorVisible)) show = false;

            var vis = show ? Visibility.Visible : Visibility.Hidden;
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
        /// Tasto destro letto con GetAsyncKeyState (solo il bit "premuto ora", sola lettura).
        /// Modalità "tieni premuto": mira = tasto giù. Modalità "alternata" (opzione di Fortnite):
        /// ogni pressione con Fortnite in primo piano inverte lo stato, che si azzera quando il gioco
        /// perde il focus, compare il cursore (menu, inventario, mappa) o si cambia arma / si entra in costruzione.
        /// </summary>
        private bool IsAiming(bool fortniteFg, bool cursorVisible)
        {
            bool down = (Native.GetAsyncKeyState(Native.VK_RBUTTON) & 0x8000) != 0;
            bool aiming;
            if (_settings.AimToggleMode)
            {
                bool cancel = AimCancelPressed();
                if (!fortniteFg || cursorVisible || cancel) _aimToggled = false;
                else if (down && !_rbWasDown) _aimToggled = !_aimToggled;
                aiming = _aimToggled;
            }
            else
            {
                aiming = down;
            }
            _rbWasDown = down;
            return aiming;
        }

        /// <summary>
        /// Controlla solo QUALE finestra è in primo piano (GetForegroundWindow) e confronta il suo PID con
        /// l'istantanea di sistema dei processi di Fortnite: nessun handle aperto verso il gioco.
        /// </summary>
        private bool IsFortniteForeground()
        {
            var hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == _lastPid) return _lastPidIsFortnite;
            _lastPid = pid;
            _lastPidIsFortnite = FortniteLocator.IsClientPid(pid);
            return _lastPidIsFortnite;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            _timer.Tick -= OnTick;
            _settings.PropertyChanged -= OnSettingsChanged;
            _window?.Close();
            _window = null;
        }
    }
}
