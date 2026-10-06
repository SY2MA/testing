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

            SetupHotkeys();
            SetupTray();

            _main = new MainWindow();
            if (Settings.StartFloating) ShowFloating();
            else _main.Show();

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

        private static void SetupHotkeys()
        {
            try
            {
                _hotkeys = new HotkeyManager();
                _hotkeys.Register(Settings.HotkeyCrosshair, () => Crosshair.Toggle());
                _hotkeys.Register(Settings.HotkeyPanel, ToggleFloating);
            }
            catch (Exception ex)
            {
                Log.Error("Registrazione scorciatoie", ex);
            }
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
            IsExiting = true;
            try
            {
                _saveTimer?.Stop();
                // Prima l'overlay, poi il contatore: Dispose salva l'eventuale registrazione abbastanza lunga.
                SafeDispose(PerfOverlay, "overlay FPS");
                SafeDispose(Perf, "contatore FPS");
                Settings.Save();
                Crosshair.Dispose();
                _hotkeys?.Dispose();
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                }
                Log.Info("Chiusura FN Boost");
            }
            finally
            {
                Current.Shutdown();
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
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
