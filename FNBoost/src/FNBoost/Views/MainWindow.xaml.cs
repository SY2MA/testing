using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FNBoost.Core;
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
            HotkeyText.Text = $"{App.Settings.HotkeyCrosshair}: mirino\n{App.Settings.HotkeyPanel}: pannello";
        }

        public void Navigate(string key)
        {
            if (!_pages.TryGetValue(key, out var page))
            {
                page = _factories[key]();
                _pages[key] = page;
            }
            PageHost.Content = page;
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
