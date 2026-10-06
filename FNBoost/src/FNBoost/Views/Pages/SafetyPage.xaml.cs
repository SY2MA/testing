using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
            HotkeysText.Text = "Scorciatoie globali: " + string.Join(" · ", HotkeySummary()) +
                               ". Si modificano qui sotto, in «Scorciatoie da tastiera».";
            LoadHotkeys();
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

        // ---------------- Scorciatoie da tastiera ----------------

        private static IEnumerable<string> HotkeySummary()
        {
            var s = App.Settings;
            yield return $"{s.HotkeyCrosshair} mirino";
            yield return $"{s.HotkeyPanel} pannello";
            yield return $"{s.HotkeyOverlay} overlay FPS";
            yield return $"{s.HotkeyNextPreset} preset successivo";
            yield return $"{s.HotkeyRecord} registrazione";
        }

        /// <summary>Caselle, nome mostrato e tasto predefinito, nello stesso ordine di App.ReloadHotkeys.</summary>
        private (HotkeyBox Box, string Name, string DefaultKey)[] HotkeyRows => new[]
        {
            (HkCrosshair, "Mirino on/off", "X"),
            (HkPanel, "Pannello flottante", "Z"),
            (HkOverlay, "Overlay FPS", "F"),
            (HkNextPreset, "Preset mirino successivo", "C"),
            (HkRecord, "Registra sessione", "R")
        };

        private void LoadHotkeys()
        {
            var s = App.Settings;
            HkCrosshair.Hotkey = s.HotkeyCrosshair ?? AppSettings.DefaultHotkey("X");
            HkPanel.Hotkey = s.HotkeyPanel ?? AppSettings.DefaultHotkey("Z");
            HkOverlay.Hotkey = s.HotkeyOverlay ?? AppSettings.DefaultHotkey("F");
            HkNextPreset.Hotkey = s.HotkeyNextPreset ?? AppSettings.DefaultHotkey("C");
            HkRecord.Hotkey = s.HotkeyRecord ?? AppSettings.DefaultHotkey("R");
            PresetBalloonBox.IsChecked = s.PresetChangeBalloon;
            HotkeyResultText.Visibility = Visibility.Collapsed;
        }

        /// <summary>Doppioni tra le caselle (prima di applicare), in italiano.</summary>
        private List<string> FindDuplicates()
        {
            var result = new List<string>();
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (box, name, _) in HotkeyRows)
            {
                var hk = box.Hotkey;
                if (!hk.Enabled) continue;
                var sig = HotkeyManager.Signature(hk);
                if (sig == null) continue;
                if (seen.TryGetValue(sig, out var other)) result.Add($"{hk} è usata sia per \"{other}\" sia per \"{name}\"");
                else seen[sig] = name;
            }
            return result;
        }

        private void ShowHotkeyResult(string text, string brushKey)
        {
            HotkeyResultText.Text = text;
            HotkeyResultText.Foreground = (System.Windows.Media.Brush)FindResource(brushKey);
            HotkeyResultText.Visibility = Visibility.Visible;
        }

        private void Hotkey_Changed(object? sender, EventArgs e)
        {
            var dup = FindDuplicates();
            if (dup.Count > 0) ShowHotkeyResult("Attenzione: " + string.Join("; ", dup) + ".", "WarnBrush");
            else ShowHotkeyResult("Modifiche non ancora applicate: premi Applica.", "MutedBrush");
        }

        private void HotkeyDefaults_Click(object sender, RoutedEventArgs e)
        {
            foreach (var (box, _, key) in HotkeyRows) box.Hotkey = AppSettings.DefaultHotkey(key);
            ShowHotkeyResult("Predefinite ripristinate nelle caselle (Ctrl+Alt+X/Z/F/C/R): premi Applica per attivarle.", "MutedBrush");
        }

        private void PresetBalloon_Click(object sender, RoutedEventArgs e)
        {
            App.Settings.PresetChangeBalloon = PresetBalloonBox.IsChecked == true;
            App.Settings.Save();
        }

        private void HotkeyApply_Click(object sender, RoutedEventArgs e)
        {
            var dup = FindDuplicates();
            if (dup.Count > 0)
            {
                ShowHotkeyResult("Non applicate: " + string.Join("; ", dup) + ". Cambia una delle due combinazioni.", "BadBrush");
                return;
            }

            var s = App.Settings;
            s.HotkeyCrosshair = HkCrosshair.Hotkey;
            s.HotkeyPanel = HkPanel.Hotkey;
            s.HotkeyOverlay = HkOverlay.Hotkey;
            s.HotkeyNextPreset = HkNextPreset.Hotkey;
            s.HotkeyRecord = HkRecord.Hotkey;
            s.Save();

            var failures = App.ReloadHotkeys();
            int active = HotkeyRows.Count(r => r.Box.Hotkey.Enabled) - failures.Count;
            if (failures.Count == 0)
            {
                ShowHotkeyResult($"Scorciatoie salvate e attive ({Math.Max(0, active)} su 5).", "OkBrush");
                Log.Info("Scorciatoie aggiornate");
            }
            else
            {
                ShowHotkeyResult("Salvate, ma alcune non sono attive:\n• " + string.Join("\n• ", failures) +
                                 "\nScegli un'altra combinazione per quelle indicate.", "WarnBrush");
            }
            HotkeysText.Text = "Scorciatoie globali: " + string.Join(" · ", HotkeySummary()) +
                               ". Si modificano qui sotto, in «Scorciatoie da tastiera».";
            RefreshLog();
        }

        private void RefreshLog_Click(object sender, RoutedEventArgs e) => RefreshLog();

        private void OpenData_Click(object sender, RoutedEventArgs e)
        {
            AppPaths.Ensure();
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Root}\"") { UseShellExecute = true });
        }
    }
}
