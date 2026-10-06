using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using FNBoost.Core;
using FNBoost.Crosshair;
using FNBoost.Perf;
using FNBoost.Views;
using WinForms = System.Windows.Forms;

namespace FNBoost
{
    public partial class App : Application
    {
        public const string Version = "1.0.0";

        public static AppSettings Settings { get; private set; } = null!;
        public static BackupStore? Backups { get; private set; }
        public static TweakService Tweaks { get; private set; } = null!;
        public static CrosshairService Crosshair { get; private set; } = null!;
        /// <summary>Contatore FPS / sessioni. Null solo se la creazione è fallita (vedi registro): il resto dell'app funziona.</summary>
        public static PerfService Perf { get; private set; } = null!;
        /// <summary>Overlay FPS in gioco. Null solo se <see cref="Perf"/> non è disponibile.</summary>
        public static PerfOverlayService PerfOverlay { get; private set; } = null!;

        private static Mutex? _mutex;
        private static HotkeyManager? _hotkeys;
        private static WinForms.NotifyIcon? _tray;
        private static MainWindow? _main;
        private static FloatingWindow? _floating;
        private static DispatcherTimer? _saveTimer;

        public static bool IsExiting { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _mutex = new Mutex(true, "Local\\FNBoost.SingleInstance", out var created);
            if (!created)
            {
                MessageBox.Show("FN Boost è già in esecuzione (controlla l'icona nell'area di notifica).",
                    "FN Boost", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            DispatcherUnhandledException += OnUnhandled;
            // Disconnessione/arresto di Windows: WPF chiama Shutdown() da solo (OnExit) senza passare da ExitApp.
            // La pulizia va fatta subito, mentre Windows attende la risposta a WM_QUERYENDSESSION.
            SessionEnding += (_, _) => Cleanup();
            AppPaths.Ensure();
            Log.Info($"Avvio FN Boost {Version}");

            Settings = AppSettings.Load();
            Backups = BackupStore.Load();
            Tweaks = new TweakService(Backups, Settings);
            Crosshair = new CrosshairService(Settings.Crosshair);
            Crosshair.StateChanged += UpdateTrayText;

            // Salvataggio automatico (con ritardo) delle modifiche al mirino.
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                Settings.Save();
            };
            Settings.Crosshair.PropertyChanged += (_, _) =>
            {
                _saveTimer.Stop();
                _saveTimer.Start();
            };

            SetupPerf();

            SetupTray();
            var hotkeyFailures = ReloadHotkeys();

            _main = new MainWindow();
            if (Settings.StartFloating) ShowFloating();
            else _main.Show();

            if (hotkeyFailures.Count > 0)
                NotifyTray("Alcune scorciatoie non sono attive (es. " + hotkeyFailures[0] +
                           "). Puoi cambiarle in Sicurezza e backup › Scorciatoie da tastiera.");

            _ = Tweaks.RefreshAsync();
        }

        /// <summary>
        /// Contatore FPS (ETW) e overlay. Se qualcosa va storto (es. ETW non disponibile) si registra l'errore
        /// e l'app continua: mirino, tweak e pulizia non dipendono da questo modulo.
        /// </summary>
        private static void SetupPerf()
        {
            Settings.Perf ??= new PerfSettings();
            Settings.Perf.Overlay ??= new PerfOverlaySettings();
            try
            {
                Perf = new PerfService(Settings.Perf, ActiveTweakIds);
            }
            catch (Exception ex)
            {
                Log.Error("Avvio modulo Prestazioni", ex);
                return;
            }
            try
            {
                Perf.Start();
            }
            catch (Exception ex)
            {
                Log.Error("Avvio contatore FPS", ex);
            }
            try
            {
                PerfOverlay = new PerfOverlayService(Settings.Perf.Overlay, Perf);
                // Salvataggio automatico (con ritardo) delle modifiche all'overlay, come per il mirino.
                Settings.Perf.Overlay.PropertyChanged += (_, _) =>
                {
                    _saveTimer?.Stop();
                    _saveTimer?.Start();
                };
            }
            catch (Exception ex)
            {
                Log.Error("Avvio overlay FPS", ex);
            }
        }

        private static IReadOnlyList<string> ActiveTweakIds() =>
            Tweaks.Tweaks.Where(t => t.IsOn).Select(t => t.Id).ToList();

        private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error("Errore imprevisto: " + e.Exception);
            MessageBox.Show("Si è verificato un errore imprevisto:\n\n" + e.Exception.Message +
                            "\n\nI dettagli sono nel registro (Sicurezza e backup › Registro attività).",
                "FN Boost", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }

        // ---------------- Scorciatoie globali ----------------

        /// <summary>Le scorciatoie sono state (ri)registrate: le finestre aggiornano i testi d'aiuto.</summary>
        public static event Action? HotkeysChanged;

        /// <summary>
        /// (Ri)registra tutte le scorciatoie globali attive (HotkeySetting.Enabled) leggendo App.Settings.
        /// Restituisce la descrizione, in italiano, di quelle non attivate: doppioni interni all'app,
        /// tasti non validi o combinazioni già prese da un'altra applicazione. Va chiamato sul thread della UI.
        /// </summary>
        public static IReadOnlyList<string> ReloadHotkeys() => ReloadHotkeysCore(logFailures: true);

        private static int _hotkeySuspend;

        /// <summary>
        /// Sospende (true) o riattiva (false) tutte le scorciatoie globali. Lo usa la casella di modifica
        /// delle scorciatoie: finché sono registrate, Windows consegnerebbe la combinazione all'app.
        /// Le chiamate vanno bilanciate.
        /// </summary>
        public static void SuspendHotkeys(bool suspend)
        {
            try
            {
                if (suspend)
                {
                    if (_hotkeySuspend++ == 0) _hotkeys?.UnregisterAll();
                }
                else if (_hotkeySuspend > 0 && --_hotkeySuspend == 0 && !IsExiting)
                {
                    ReloadHotkeysCore(logFailures: false);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Sospensione scorciatoie", ex);
            }
        }

        private static IReadOnlyList<string> ReloadHotkeysCore(bool logFailures)
        {
            var failures = new List<string>();
            if (Settings == null) return failures;
            Settings.HotkeyCrosshair ??= AppSettings.DefaultHotkey("X");
            Settings.HotkeyPanel ??= AppSettings.DefaultHotkey("Z");
            Settings.HotkeyOverlay ??= AppSettings.DefaultHotkey("F");
            Settings.HotkeyNextPreset ??= AppSettings.DefaultHotkey("C");
            Settings.HotkeyRecord ??= AppSettings.DefaultHotkey("R");

            try
            {
                _hotkeys ??= new HotkeyManager();
                _hotkeys.UnregisterAll();

                var entries = new (string Name, HotkeySetting Hk, Action Action)[]
                {
                    ("Mirino on/off", Settings.HotkeyCrosshair, () => Crosshair.Toggle()),
                    ("Pannello flottante", Settings.HotkeyPanel, ToggleFloating),
                    ("Overlay FPS", Settings.HotkeyOverlay, HotkeyToggleOverlay),
                    ("Preset mirino successivo", Settings.HotkeyNextPreset, HotkeyNextPreset),
                    ("Registra sessione", Settings.HotkeyRecord, HotkeyToggleRecording)
                };

                // Prima i doppioni interni all'app: Windows rifiuterebbe la seconda con un errore poco chiaro.
                var used = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (name, hk, action) in entries)
                {
                    if (!hk.Enabled) continue;
                    var sig = HotkeyManager.Signature(hk);
                    if (sig == null)
                    {
                        failures.Add($"{name}: tasto \"{hk.Key}\" non valido");
                        continue;
                    }
                    if (used.TryGetValue(sig, out var other))
                    {
                        failures.Add($"{name}: {hk} è già usata per \"{other}\"");
                        continue;
                    }
                    used[sig] = name;
                    if (!_hotkeys.Register(hk, action))
                        failures.Add($"{name}: {hk} – {_hotkeys.LastError}");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Registrazione scorciatoie", ex);
                failures.Add("Impossibile attivare le scorciatoie globali: " + ex.Message);
            }

            if (logFailures)
                foreach (var f in failures) Log.Warn("Scorciatoia non attiva · " + f);
            try
            {
                HotkeysChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error("Aggiornamento testi scorciatoie", ex);
            }
            return failures;
        }

        private static void HotkeyToggleOverlay()
        {
            if (PerfOverlay == null)
            {
                NotifyTray("Overlay FPS non disponibile: controlla il registro attività.");
                return;
            }
            PerfOverlay.Toggle();
        }

        private static void HotkeyNextPreset()
        {
            var name = Crosshair.NextPreset();
            if (name == null)
            {
                NotifyTray("Nessun preset del mirino salvato.");
                return;
            }
            // Il mirino mostra già il nome sotto al centro: la notifica serve solo se richiesta o se il mirino è spento.
            if (Settings.PresetChangeBalloon || !Crosshair.Enabled)
                NotifyTray(Crosshair.Enabled ? $"Preset mirino: {name}" : $"Preset mirino: {name} (il mirino è spento)");
        }

        private static void HotkeyToggleRecording()
        {
            var perf = Perf;
            if (perf == null)
            {
                NotifyTray("Contatore FPS non disponibile: controlla il registro attività.");
                return;
            }
            if (perf.IsRecording)
            {
                var session = perf.StopRecording();
                NotifyTray(session != null
                    ? $"Registrazione salvata: {session.DurationText} · media {session.Stats.AvgFps:0} FPS · 1% low {session.Stats.Low1Fps:0} FPS."
                    : $"Registrazione fermata: troppo breve per essere salvata (minimo {Settings.Perf.MinSessionSeconds} s).");
                return;
            }
            perf.StartRecording();
            if (!perf.IsRecording)
                NotifyTray("Impossibile avviare la registrazione: controlla il registro attività.");
            else if (perf.Status == CaptureStatus.Stopped || perf.Status == CaptureStatus.Error)
                NotifyTray("Registrazione avviata, ma il contatore FPS non sta misurando: controlla la pagina Prestazioni.");
            else
                NotifyTray("Registrazione avviata.");
        }

        private static void SetupTray()
        {
            try
            {
                var menu = new WinForms.ContextMenuStrip();
                menu.Items.Add("Apri FN Boost", null, (_, _) => ShowMain());
                menu.Items.Add("Pannello flottante", null, (_, _) => ShowFloating());
                menu.Items.Add("Mirino on/off", null, (_, _) => Crosshair.Toggle());
                menu.Items.Add("Overlay FPS on/off", null, (_, _) => PerfOverlay?.Toggle());
                menu.Items.Add("Avvia/ferma registrazione", null, (_, _) => HotkeyToggleRecording());
                menu.Items.Add(new WinForms.ToolStripSeparator());
                menu.Items.Add("Esci", null, (_, _) => ExitApp());

                System.Drawing.Icon icon;
                var res = GetResourceStream(new Uri("pack://application:,,,/Assets/fnboost.ico"));
                icon = res != null ? new System.Drawing.Icon(res.Stream) : System.Drawing.SystemIcons.Application;

                _tray = new WinForms.NotifyIcon
                {
                    Icon = icon,
                    Text = "FN Boost",
                    ContextMenuStrip = menu,
                    Visible = true
                };
                _tray.DoubleClick += (_, _) => ShowMain();
                UpdateTrayText();
            }
            catch (Exception ex)
            {
                Log.Error("Icona nell'area di notifica", ex);
            }
        }

        private static void UpdateTrayText()
        {
            if (_tray == null) return;
            _tray.Text = Crosshair.Enabled ? "FN Boost – mirino attivo" : "FN Boost";
        }

        public static void NotifyTray(string message)
        {
            _tray?.ShowBalloonTip(3500, "FN Boost", message, WinForms.ToolTipIcon.Info);
        }

        public static void ShowMain()
        {
            _main ??= new MainWindow();
            _main.Show();
            if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
            _main.Activate();
        }

        public static void ShowFloating()
        {
            _floating ??= new FloatingWindow();
            _floating.Show();
            _floating.Activate();
        }

        public static void ToggleFloating()
        {
            if (_floating is { IsVisible: true }) _floating.Hide();
            else ShowFloating();
        }

        public static void SwitchToFloating()
        {
            _main?.Hide();
            ShowFloating();
        }

        public static void SwitchToDesktop()
        {
            _floating?.Hide();
            ShowMain();
        }

        public static void ExitApp()
        {
            if (IsExiting) return;
            try
            {
                Cleanup();
            }
            finally
            {
                Current.Shutdown();
            }
        }

        /// <summary>
        /// Pulizia di chiusura (una sola volta): salva la registrazione in corso, chiude le sessioni ETW,
        /// salva le impostazioni, libera scorciatoie e icona. Usata da ExitApp, dalla fine della sessione
        /// di Windows (SessionEnding) e, come ultima rete, da OnExit.
        /// </summary>
        private static void Cleanup()
        {
            if (IsExiting) return;
            IsExiting = true;
            try
            {
                _saveTimer?.Stop();
                // Prima l'overlay, poi il contatore: Dispose salva l'eventuale registrazione abbastanza lunga.
                SafeDispose(PerfOverlay, "overlay FPS");
                SafeDispose(Perf, "contatore FPS");
                Settings?.Save();
                SafeDispose(Crosshair, "mirino");
                SafeDispose(_hotkeys, "scorciatoie");
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                }
                Log.Info("Chiusura FN Boost");
            }
            catch (Exception ex)
            {
                Log.Error("Chiusura FN Boost", ex);
            }
        }

        private static void SafeDispose(IDisposable? d, string what)
        {
            try
            {
                d?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("Chiusura " + what, ex);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Seconda istanza (Settings mai caricate): niente da pulire.
            if (Settings != null) Cleanup();
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
