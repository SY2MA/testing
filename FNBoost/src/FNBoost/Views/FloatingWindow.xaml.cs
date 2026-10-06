using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FNBoost.Core;
using FNBoost.Crosshair;
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
            PresetCombo.ItemsSource = s.CrosshairPresets;
            _loadingPreset = false;

            HotkeyText.Text = $"{s.HotkeyCrosshair} mirino · {s.HotkeyPanel} pannello";

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
                    PresetCombo.ItemsSource = App.Settings.CrosshairPresets;
                    _loadingPreset = false;
                    _timer.Start();
                    UpdateStats();
                }
                else _timer.Stop();
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
            if (PresetCombo.SelectedItem is CrosshairPreset p) App.Settings.Crosshair.CopyFrom(p.Settings);
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
