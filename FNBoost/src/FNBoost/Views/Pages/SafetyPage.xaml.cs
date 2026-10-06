using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FNBoost.Core;

namespace FNBoost.Views.Pages
{
    public partial class SafetyPage : UserControl
    {
        public SafetyPage()
        {
            InitializeComponent();
            Loaded += (_, _) => RefreshAll();
            App.Tweaks.Changed += () => Dispatcher.BeginInvoke(new System.Action(RefreshBackups));
        }

        private void RefreshAll()
        {
            AutoRpBox.IsChecked = App.Settings.AutoRestorePoint;
            TrayBox.IsChecked = App.Settings.CloseToTray;
            StartFloatingBox.IsChecked = App.Settings.StartFloating;
            HotkeysText.Text = $"Scorciatoie globali: {App.Settings.HotkeyCrosshair} = mirino on/off · {App.Settings.HotkeyPanel} = pannello flottante " +
                               "(modificabili in %LOCALAPPDATA%\\FNBoost\\settings.json).";
            RefreshBackups();
            RefreshLog();
        }

        private void RefreshBackups()
        {
            if (App.Backups == null) return;
            var items = App.Backups.All();
            BackupList.ItemsSource = items;
            NoBackupsText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshLog()
        {
            LogBox.Text = string.Join("\n", Log.Tail(200));
            LogBox.ScrollToEnd();
        }

        private async void RestorePoint_Click(object sender, RoutedEventArgs e)
        {
            RestorePointBtn.IsEnabled = false;
            RestorePointText.Text = "Creazione in corso (può richiedere un minuto)…";
            var (ok, msg) = await Task.Run(() => RestorePoint.Create("FN Boost - punto manuale"));
            RestorePointText.Text = msg;
            RestorePointText.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "OkBrush" : "WarnBrush");
            RestorePointBtn.IsEnabled = true;
            RefreshLog();
        }

        private async void RevertAll_Click(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show(Window.GetWindow(this)!, "Ripristinare tutte le modifiche fatte da FN Boost ai valori originali?",
                "FN Boost", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            await App.Tweaks.RevertAllAsync();
            RefreshBackups();
            RefreshLog();
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            App.Settings.AutoRestorePoint = AutoRpBox.IsChecked == true;
            App.Settings.CloseToTray = TrayBox.IsChecked == true;
            App.Settings.StartFloating = StartFloatingBox.IsChecked == true;
            App.Settings.Save();
        }

        private void RefreshLog_Click(object sender, RoutedEventArgs e) => RefreshLog();

        private void OpenData_Click(object sender, RoutedEventArgs e)
        {
            AppPaths.Ensure();
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Root}\"") { UseShellExecute = true });
        }
    }
}
