using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FNBoost.Core;
using FNBoost.Perf;
using FNBoost.Views.Pages;

namespace FNBoost.Views
{
    public partial class MainWindow : Window
    {
        private readonly Dictionary<string, Func<UserControl>> _factories;
        private readonly Dictionary<string, UserControl> _pages = new();

        public MainWindow()
        {
            InitializeComponent();
            _factories = new Dictionary<string, Func<UserControl>>
            {
                ["dashboard"] = () => new DashboardPage(),
                ["tweaks"] = () => new TweaksPage(),
                ["fortnite"] = () => new FortnitePage(),
                ["crosshair"] = () => new CrosshairPage(),
                ["performance"] = () => new PerformancePage(),
                ["cleanup"] = () => new CleanupPage(),
                ["safety"] = () => new SafetyPage(),
                ["info"] = () => new InfoPage(),
            };
            Navigate("dashboard");

            Log.MessageLogged += OnLog;
            App.Tweaks.Changed += UpdateSummary;
            App.Crosshair.StateChanged += UpdateCrosshair;
            StateChanged += (_, _) => OnWindowStateChanged();
            UpdateSummary();
            UpdateCrosshair();
            UpdateHotkeyTexts();
            App.HotkeysChanged += UpdateHotkeyTexts;
            Closed += (_, _) => App.HotkeysChanged -= UpdateHotkeyTexts;
            SetupPerf();
        }

        // ---------------- Prestazioni (overlay FPS + numeri dal vivo nella barra laterale) ----------------

        private long _lastPerfTextMs;

        private void SetupPerf()
        {
            var overlay = App.PerfOverlay;
            if (overlay != null)
            {
                overlay.StateChanged += UpdatePerfOverlay;
                Closed += (_, _) => overlay.StateChanged -= UpdatePerfOverlay;
                UpdatePerfOverlay();
            }
            else
            {
                PerfOverlayQuick.IsEnabled = false;
            }

            var perf = App.Perf;
            if (perf == null)
            {
                PerfLiveText.Text = "Contatore FPS non disponibile";
                return;
            }
            perf.LiveUpdated += OnPerfLive;
            perf.StatusChanged += OnPerfStatus;
            Closed += (_, _) =>
            {
                perf.LiveUpdated -= OnPerfLive;
                perf.StatusChanged -= OnPerfStatus;
            };
            ShowPerfLive(perf.Live);
        }

        private void UpdatePerfOverlay()
        {
            if (App.PerfOverlay != null) PerfOverlayQuick.IsChecked = App.PerfOverlay.Enabled;
        }

        private void PerfOverlayQuick_Click(object sender, RoutedEventArgs e)
        {
            if (App.PerfOverlay != null) App.PerfOverlay.Enabled = PerfOverlayQuick.IsChecked == true;
        }

        private void OnPerfStatus()
        {
            if (App.Perf != null) ShowPerfLive(App.Perf.Live);
        }

        private void OnPerfLive(LiveSnapshot snap)
        {
            // ~2 aggiornamenti al secondo bastano per la barra laterale; da nascosta non si fa nulla.
            if (!IsVisible) return;
            var now = Environment.TickCount64;
            if (now - _lastPerfTextMs < 450) return;
            _lastPerfTextMs = now;
            ShowPerfLive(snap);
        }

        private void ShowPerfLive(LiveSnapshot snap)
        {
            var c = CultureInfo.CurrentCulture;
            if (snap.HasData)
            {
                PerfLiveText.Text = snap.GameFocused
                    ? $"FPS {snap.CurrentFps.ToString("0", c)} · 1% low {snap.Window.Low1Fps.ToString("0", c)}"
                    : "FPS: gioco fuori fuoco";
                PerfLiveText.ToolTip = $"{snap.StatusText}\nMedia {snap.Window.AvgFps.ToString("0", c)} · 0,1% low {snap.Window.Low01Fps.ToString("0", c)}";
            }
            else
            {
                PerfLiveText.Text = snap.Status switch
                {
                    CaptureStatus.Stopped => "FPS: misurazione ferma",
                    CaptureStatus.Error => "FPS: non disponibile",
                    _ => "FPS: in attesa del gioco…"
                };
                PerfLiveText.ToolTip = string.IsNullOrEmpty(snap.StatusText) ? null : snap.StatusText;
            }
        }

        private void UpdateHotkeyTexts()
        {
            var s = App.Settings;
            HotkeyText.Text = $"{s.HotkeyCrosshair}: mirino\n{s.HotkeyPanel}: pannello";
            PerfOverlayQuick.ToolTip = $"Contatore FPS sopra al gioco ({s.HotkeyOverlay})\nRegistra sessione: {s.HotkeyRecord}";
        }

        /// <summary>Mostra una pagina ("dashboard", "performance"…) e seleziona la voce corrispondente nel menu.</summary>
        public void Navigate(string key)
        {
            if (!_factories.ContainsKey(key)) return;
            if (!_pages.TryGetValue(key, out var page))
            {
                page = _factories[key]();
                _pages[key] = page;
            }
            PageHost.Content = page;
            foreach (var rb in NavPanel.Children.OfType<RadioButton>())
                if (rb.CommandParameter is string k && k == key && rb.IsChecked != true) rb.IsChecked = true;
        }

        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.CommandParameter is string key) Navigate(key);
        }

        private void OnLog(string message)
        {
            Dispatcher.BeginInvoke(new Action(() => StatusText.Text = message));
        }

        private void UpdateSummary()
        {
            var t = App.Tweaks;
            TweakSummary.Text = $"{t.RecommendedActive}/{t.RecommendedCount} consigliati attivi\n{t.ActiveCount} tweak attivi in totale";
            RebootBanner.Visibility = t.RebootPending ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateCrosshair()
        {
            CrosshairQuick.IsChecked = App.Crosshair.Enabled;
        }

        private void CrosshairQuick_Click(object sender, RoutedEventArgs e)
        {
            App.Crosshair.Enabled = CrosshairQuick.IsChecked == true;
        }

        private void OnWindowStateChanged()
        {
            // Con WindowChrome la finestra massimizzata "sborda" di qualche pixel: si compensa.
            RootBorder.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxBtn.Content = WindowState == WindowState.Maximized ? "" : "";
        }

        private void Floating_Click(object sender, RoutedEventArgs e) => App.SwitchToFloating();
        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Maximize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            if (App.IsExiting) return;
            if (App.Settings.CloseToTray)
            {
                e.Cancel = true;
                Hide();
                if (!App.Settings.TrayHintShown)
                {
                    App.Settings.TrayHintShown = true;
                    App.Settings.Save();
                    App.NotifyTray("FN Boost resta attivo nell'area di notifica (mirino e scorciatoie funzionano). Per uscire: tasto destro sull'icona › Esci.");
                }
                return;
            }
            App.ExitApp();
        }
    }
}
