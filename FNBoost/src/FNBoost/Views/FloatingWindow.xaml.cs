using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FNBoost.Core;
using FNBoost.Crosshair;
using FNBoost.Perf;
using FNBoost.Views.Pages;

namespace FNBoost.Views
{
    /// <summary>Pannello compatto, trasparente e trascinabile, sempre in primo piano.</summary>
    public partial class FloatingWindow : Window
    {
        private readonly SystemMonitor _monitor = new();
        private readonly DispatcherTimer _timer;
        private bool _loadingPreset;
        private int _tick;
        private bool _perfSubscribed;
        private string? _recordNote;
        private long _recordNoteUntilMs;

        public FloatingWindow()
        {
            InitializeComponent();
            DataContext = App.Settings.Crosshair;

            var s = App.Settings;
            Opacity = Math.Clamp(s.FloatingOpacity, 0.35, 1);
            OpacitySlider.Value = Opacity;
            Topmost = s.FloatingTopmost;
            UpdatePin();
            if (!double.IsNaN(s.FloatingLeft) && !double.IsNaN(s.FloatingTop) && IsOnScreen(s.FloatingLeft, s.FloatingTop))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = s.FloatingLeft;
                Top = s.FloatingTop;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = SystemParameters.WorkArea.Right - Width - 24;
                Top = SystemParameters.WorkArea.Top + 80;
            }

            foreach (var hex in CrosshairPage.Palette)
            {
                var b = new Button
                {
                    Style = (Style)FindResource("Swatch"),
                    Width = 22,
                    Height = 22,
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                    ToolTip = hex
                };
                b.Click += (_, _) => App.Settings.Crosshair.Color = hex;
                Swatches.Children.Add(b);
            }

            _loadingPreset = true;
            CrosshairPreset.NormalizeAll(s.CrosshairPresets); // preset v1 o con "Settings": null nel JSON
            PresetCombo.ItemsSource = s.CrosshairPresets;
            _loadingPreset = false;

            UpdateHotkeyText();
            App.HotkeysChanged += UpdateHotkeyText;

            if (App.PerfOverlay != null) App.PerfOverlay.StateChanged += UpdatePerfOverlay;
            else PerfOverlayToggle.IsEnabled = false;
            UpdatePerfOverlay();
            if (App.Perf == null)
            {
                RecordBtn.IsEnabled = false;
                PerfStatusText.Text = "Contatore FPS non disponibile";
            }

            App.Crosshair.StateChanged += UpdateCrosshair;
            App.Tweaks.Changed += UpdateTweaks;
            UpdateCrosshair();
            UpdateTweaks();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => UpdateStats();
            IsVisibleChanged += (_, _) =>
            {
                if (IsVisible)
                {
                    _loadingPreset = true;
                    PresetCombo.ItemsSource = null;
                    CrosshairPreset.NormalizeAll(App.Settings.CrosshairPresets);
                    PresetCombo.ItemsSource = App.Settings.CrosshairPresets;
                    _loadingPreset = false;
                    _timer.Start();
                    UpdateStats();
                    SubscribePerf(true);
                }
                else
                {
                    _timer.Stop();
                    // Da nascosto il pannello non riceve più gli aggiornamenti del contatore FPS.
                    SubscribePerf(false);
                }
            };
            LocationChanged += (_, _) =>
            {
                App.Settings.FloatingLeft = Left;
                App.Settings.FloatingTop = Top;
            };
        }

        private static bool IsOnScreen(double left, double top) =>
            left >= SystemParameters.VirtualScreenLeft - 50 &&
            top >= SystemParameters.VirtualScreenTop - 50 &&
            left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50 &&
            top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50;

        private void UpdateStats()
        {
            var (cpu, ram, used, total) = _monitor.Sample();
            CpuValue.Text = $"{cpu:0}%";
            RamValue.Text = $"{ram:0}% · {used:0.0}/{total:0} GB";
            var w = ((FrameworkElement)CpuBar.Parent).ActualWidth;
            CpuBar.Width = w * cpu / 100;
            RamBar.Width = w * ram / 100;

            if (_tick++ % 3 == 0)
            {
                var running = FortniteLocator.IsRunning();
                FnDot.Fill = (Brush)FindResource(running ? "OkBrush" : "MutedBrush");
                FnText.Text = running ? "Fortnite: in esecuzione" : "Fortnite: chiuso";
            }
        }

        // ---------------- Prestazioni ----------------

        private void SubscribePerf(bool on)
        {
            var perf = App.Perf;
            if (perf == null || on == _perfSubscribed) return;
            _perfSubscribed = on;
            if (on)
            {
                perf.LiveUpdated += OnPerfLive;
                perf.StatusChanged += OnPerfStatus;
                ShowPerf(perf.Live);
            }
            else
            {
                perf.LiveUpdated -= OnPerfLive;
                perf.StatusChanged -= OnPerfStatus;
            }
        }

        private void OnPerfLive(LiveSnapshot snap)
        {
            if (IsVisible) ShowPerf(snap);
        }

        private void OnPerfStatus()
        {
            if (IsVisible && App.Perf != null) ShowPerf(App.Perf.Live);
        }

        private void ShowPerf(LiveSnapshot snap)
        {
            var c = CultureInfo.CurrentCulture;
            var w = snap.Window;
            string F(double v) => w.HasData && v > 0 && !double.IsNaN(v) && !double.IsInfinity(v) ? v.ToString("0", c) : "–";

            if (snap.HasData)
            {
                // In secondo piano Fortnite rallenta da solo a ~30 FPS: niente numero, che sarebbe fuorviante.
                PerfFpsText.Text = snap.GameFocused ? snap.CurrentFps.ToString("0", c) : "–";
                PerfAvgText.Text = F(w.AvgFps);
                PerfLow1Text.Text = F(w.Low1Fps);
                PerfLow01Text.Text = F(w.Low01Fps);
                PerfMinText.Text = F(w.MinFps);
                PerfMaxText.Text = F(w.MaxFps);
                // 1% low molto sotto la media = frametime irregolari (stessa regola dell'overlay).
                PerfLow1Text.Foreground = w.HasData && w.Low1Fps < w.AvgFps * 0.5
                    ? (Brush)FindResource("WarnBrush")
                    : (Brush)FindResource("TextBrush");
                PerfGraph.Frametimes = snap.RecentFrametimes;
                PerfStatusText.Text = snap.GameFocused ? $"{snap.LastFrametimeMs.ToString("0.0", c)} ms" : "fuori fuoco";
                PerfStatusText.ToolTip = snap.StatusText;
            }
            else
            {
                PerfFpsText.Text = "–";
                PerfAvgText.Text = PerfLow1Text.Text = PerfLow01Text.Text = PerfMinText.Text = PerfMaxText.Text = "–";
                PerfLow1Text.Foreground = (Brush)FindResource("TextBrush");
                PerfGraph.Frametimes = null;
                PerfStatusText.Text = snap.Status switch
                {
                    CaptureStatus.Stopped => "Misurazione ferma",
                    CaptureStatus.Error => "Non disponibile",
                    _ => "In attesa del gioco…"
                };
                PerfStatusText.ToolTip = string.IsNullOrEmpty(snap.StatusText) ? null : snap.StatusText;
            }

            ShowNet(snap.Net);

            if (_recordNote != null)
            {
                if (Environment.TickCount64 < _recordNoteUntilMs) PerfStatusText.Text = _recordNote;
                else _recordNote = null;
            }
            UpdateRecord(snap.IsRecording, snap.RecordingSeconds);
        }

        /// <summary>Riga "Ping 24 ms · jitter 2 · perdita 0%" (gialla/rossa oltre le soglie), oppure "Ping: n/d".</summary>
        private void ShowNet(NetworkSnapshot? net)
        {
            var game = net?.Game;
            bool off = net == null && !App.Settings.Perf.NetCaptureEnabled;
            PerfNetText.Text = off ? "Ping: misura di rete spenta" : NetDisplay.PingLine(game);
            PerfNetText.Foreground = (Brush)FindResource(NetDisplay.Level(game) switch
            {
                2 => "BadBrush",
                1 => "WarnBrush",
                _ => "MutedBrush"
            });
            string? target = game != null && NetDisplay.PingMs(game).HasValue && !string.IsNullOrEmpty(game.Target) ? game.Target : null;
            var tip = net == null
                ? off ? "Attiva «Misura ping e rete» nella pagina Prestazioni" : "La misura di rete parte insieme al contatore FPS"
                : string.Join("\n", new[] { target, net.StatusText }.Where(x => !string.IsNullOrEmpty(x)));
            PerfNetText.ToolTip = tip.Length > 0 ? tip : null;
        }

        private void UpdateRecord(bool recording, double seconds)
        {
            if (recording)
            {
                var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
                RecordText.Text = "Ferma registrazione · " + t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");
                RecordDot.Fill = (Brush)FindResource("BadBrush");
                RecordBtn.ToolTip = $"Ferma e salva la sessione ({App.Settings.HotkeyRecord})";
            }
            else
            {
                RecordText.Text = "Avvia registrazione";
                RecordDot.Fill = (Brush)FindResource("MutedBrush");
                RecordBtn.ToolTip = $"Registra una sessione da analizzare nella pagina Prestazioni ({App.Settings.HotkeyRecord})";
            }
        }

        private void Record_Click(object sender, RoutedEventArgs e)
        {
            var perf = App.Perf;
            if (perf == null) return;
            if (perf.IsRecording)
            {
                var session = perf.StopRecording();
                _recordNote = session != null
                    ? "Sessione salvata"
                    : $"Troppo breve (min {App.Settings.Perf.MinSessionSeconds} s)";
                _recordNoteUntilMs = Environment.TickCount64 + 5000;
            }
            else
            {
                perf.StartRecording();
                _recordNote = null;
            }
            ShowPerf(perf.Live);
            UpdateRecord(perf.IsRecording, perf.IsRecording ? perf.Live.RecordingSeconds : 0);
        }

        private void UpdatePerfOverlay()
        {
            PerfOverlayToggle.IsChecked = App.PerfOverlay?.Enabled == true;
        }

        private void PerfOverlayToggle_Click(object sender, RoutedEventArgs e)
        {
            if (App.PerfOverlay != null) App.PerfOverlay.Enabled = PerfOverlayToggle.IsChecked == true;
            UpdatePerfOverlay();
        }

        /// <summary>
        /// Aiuto breve sulle scorciatoie. Se condividono i modificatori (es. tutte Ctrl+Alt) li scrive una volta sola:
        /// "Ctrl+Alt + X mirino · Z pannello · F overlay · C preset · R registra".
        /// </summary>
        private void UpdateHotkeyText()
        {
            var s = App.Settings;
            var items = new List<(HotkeySetting Hk, string What)>
            {
                (s.HotkeyCrosshair, "mirino"), (s.HotkeyPanel, "pannello"), (s.HotkeyOverlay, "overlay"),
                (s.HotkeyNextPreset, "preset"), (s.HotkeyRecord, "registra")
            };
            items.RemoveAll(i => i.Hk == null || !i.Hk.Enabled);
            if (items.Count == 0)
            {
                HotkeyText.Text = "Scorciatoie disattivate (Sicurezza e backup › Scorciatoie da tastiera)";
            }
            else
            {
                static string Prefix(HotkeySetting h) => $"{(h.Ctrl ? "Ctrl+" : "")}{(h.Alt ? "Alt+" : "")}{(h.Shift ? "Shift+" : "")}";
                var prefix = Prefix(items[0].Hk);
                HotkeyText.Text = prefix.Length > 0 && items.All(i => Prefix(i.Hk) == prefix)
                    ? prefix.TrimEnd('+') + " + " + string.Join(" · ", items.Select(i => $"{HotkeySetting.KeyDisplayName(i.Hk.Key)} {i.What}"))
                    : string.Join(" · ", items.Select(i => $"{i.Hk} {i.What}"));
            }
            HideBtn.ToolTip = s.HotkeyPanel is { Enabled: true } ? $"Nascondi ({s.HotkeyPanel} per riaprire)" : "Nascondi";
        }

        private void UpdateCrosshair() => CrosshairToggle.IsChecked = App.Crosshair.Enabled;

        private void UpdateTweaks()
        {
            TweakCount.Text = $"{App.Tweaks.RecommendedActive}/{App.Tweaks.RecommendedCount} consigliati";
        }

        private void UpdatePin()
        {
            PinBtn.Content = Topmost ? "" : "";
            PinBtn.Foreground = Topmost ? (Brush)FindResource("Accent2Brush") : (Brush)FindResource("MutedBrush");
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void Pin_Click(object sender, RoutedEventArgs e)
        {
            Topmost = !Topmost;
            App.Settings.FloatingTopmost = Topmost;
            UpdatePin();
        }

        private void Desktop_Click(object sender, RoutedEventArgs e) => App.SwitchToDesktop();
        private void Hide_Click(object sender, RoutedEventArgs e) => Hide();

        private void CrosshairToggle_Click(object sender, RoutedEventArgs e) =>
            App.Crosshair.Enabled = CrosshairToggle.IsChecked == true;

        private void Preset_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingPreset) return;
            // Come la scorciatoia "preset successivo": ricorda il preset (NextPreset riparte da qui) e avvisa la pagina Mirino.
            if (PresetCombo.SelectedItem is CrosshairPreset p) App.Crosshair.ApplyPreset(p);
        }

        private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            Opacity = e.NewValue;
            App.Settings.FloatingOpacity = e.NewValue;
        }

        private async void Apply_Click(object sender, RoutedEventArgs e)
        {
            ApplyBtn.IsEnabled = RevertBtn.IsEnabled = false;
            try
            {
                var (ok, fail) = await App.Tweaks.ApplyRecommendedAsync();
                App.NotifyTray($"Applicati {ok} tweak consigliati{(fail > 0 ? $", {fail} errori" : "")}." +
                               (App.Tweaks.RebootPending ? " Riavvia per completare." : ""));
            }
            finally
            {
                ApplyBtn.IsEnabled = RevertBtn.IsEnabled = true;
            }
        }

        private async void Revert_Click(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show(this, "Ripristinare tutte le modifiche fatte da FN Boost?", "FN Boost",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            ApplyBtn.IsEnabled = RevertBtn.IsEnabled = false;
            try
            {
                await App.Tweaks.RevertAllAsync();
            }
            finally
            {
                ApplyBtn.IsEnabled = RevertBtn.IsEnabled = true;
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Il pannello non si distrugge: si nasconde e resta pronto per la scorciatoia.
            e.Cancel = true;
            Hide();
            App.Settings.Save();
        }
    }
}
