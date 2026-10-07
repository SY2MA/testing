using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FNBoost.Core;

namespace FNBoost.Views.Pages
{
    public partial class FortnitePage : UserControl
    {
        private readonly DispatcherTimer _timer;
        private FortniteSettings? _current;
        private int _refreshHz = 60;

        private static readonly (string label, double value)[] FpsOptions =
        {
            ("Illimitato", 0), ("60", 60), ("120", 120), ("144", 144), ("165", 165),
            ("180", 180), ("240", 240), ("280", 280), ("360", 360), ("480", 480)
        };

        public FortnitePage()
        {
            InitializeComponent();
            foreach (var o in FpsOptions) FpsCombo.Items.Add(o.label);
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (_, _) => UpdateRunning();
            Loaded += (_, _) =>
            {
                _timer.Start();
                LoadAll();
            };
            Unloaded += (_, _) => _timer.Stop();
        }

        private void LoadAll()
        {
            var primary = SystemDiagnostics.ReadDisplayList().FirstOrDefault();
            _refreshHz = primary?.CurrentHz ?? 60;
            FpsHint.Text = $"Il tuo monitor principale: {_refreshHz} Hz. Consigliato: {_refreshHz} FPS (o {Math.Max(30, _refreshHz - 3)} con G-SYNC/FreeSync), oppure il valore più alto che il PC mantiene senza cali.";

            InstallText.Text = "Installazione: " + (FortniteLocator.InstallDir ?? "non trovata");
            ConfigText.Text = "Configurazione: " + FortniteLocator.ConfigFile + (FortniteConfigService.ConfigExists ? "" : "  (non trovata: avvia Fortnite almeno una volta)");
            UpdateRunning();
            LoadSettings();
            LoadBackups();
        }

        private void UpdateRunning()
        {
            var running = FortniteLocator.IsRunning();
            RunDot.Fill = (Brush)FindResource(running ? "WarnBrush" : "OkBrush");
            RunText.Text = running ? "Fortnite è in esecuzione – chiudilo per salvare le impostazioni" : "Fortnite chiuso – puoi salvare le impostazioni";
            SaveBtn.IsEnabled = !running && FortniteConfigService.ConfigExists;
            SaveHint.Text = running ? "Il gioco riscrive il file all'uscita: chiudilo prima." : "";
        }

        private void LoadSettings()
        {
            if (!FortniteConfigService.ConfigExists)
            {
                SettingsCard.IsEnabled = false;
                return;
            }
            try
            {
                _current = FortniteSettings.Read(FortniteLocator.ConfigFile);
                SettingsCard.IsEnabled = true;
                Show(_current);
            }
            catch (Exception ex)
            {
                Log.Error("Lettura GameUserSettings.ini", ex);
                SettingsCard.IsEnabled = false;
            }
        }

        private void Show(FortniteSettings s)
        {
            RenderCombo.SelectedIndex = s.RenderMode switch
            {
                RenderMode.Performance => 0,
                RenderMode.DirectX12 => 1,
                _ => -1
            };
            RenderNote.Text = s.RenderMode == RenderMode.Unknown
                ? $"Valore attuale non standard ({s.RawRhi}): non verrà modificato se non scegli una modalità."
                : s.RenderMode == RenderMode.Performance
                    ? $"Performance usa Direct3D 11 a feature level ridotto (ES3_1), come scrive il gioco nel suo log. Valore nel file: {s.RawRhi}."
                    : $"DirectX 12 a pieno feature level (SM6): necessario per ray tracing e Nanite. Valore nel file: {s.RawRhi}.";
            FpsCombo.Text = s.FrameRateLimit <= 0 ? "Illimitato" : s.FrameRateLimit.ToString("0", CultureInfo.InvariantCulture);
            ReflexCombo.SelectedIndex = Math.Clamp(s.Reflex, 0, 2);
            WindowCombo.SelectedIndex = Math.Clamp(s.WindowMode, 0, 2);
            PresetCombo.SelectedIndex = (int)s.Preset;
            VSyncBox.IsChecked = s.VSync;
            MotionBlurBox.IsChecked = s.MotionBlur;
            ShowFpsBox.IsChecked = s.ShowFps;
            RayTracingBox.IsChecked = s.RayTracing;
            NaniteBox.IsChecked = s.Nanite;
            GrassBox.IsChecked = s.ShowGrass;
            MeshCombo.SelectedIndex = Math.Clamp(s.MeshQuality, 0, 2);
            SgText.Text = s.ScalabilitySummary;
            ResText.Text = s.ResolutionX > 0 ? $"Risoluzione salvata: {s.ResolutionX}×{s.ResolutionY}" : "";
        }

        private bool Collect(FortniteSettings s)
        {
            s.RenderMode = RenderCombo.SelectedIndex switch
            {
                0 => RenderMode.Performance,
                1 => RenderMode.DirectX12,
                _ => RenderMode.Unknown
            };
            var fpsText = (FpsCombo.Text ?? "").Trim();
            if (fpsText.Equals("Illimitato", StringComparison.OrdinalIgnoreCase) || fpsText == "0" || fpsText.Length == 0)
                s.FrameRateLimit = 0;
            else if (double.TryParse(fpsText, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) && fps >= 30 && fps <= 1000)
                s.FrameRateLimit = Math.Round(fps);
            else
            {
                MessageBox.Show(Window.GetWindow(this)!, "Limite FPS non valido: usa un numero tra 30 e 1000, oppure 'Illimitato'.",
                    "FN Boost", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            s.Reflex = Math.Max(0, ReflexCombo.SelectedIndex);
            s.WindowMode = Math.Max(0, WindowCombo.SelectedIndex);
            s.Preset = (QualityPreset)Math.Max(0, PresetCombo.SelectedIndex);
            s.VSync = VSyncBox.IsChecked == true;
            s.MotionBlur = MotionBlurBox.IsChecked == true;
            s.ShowFps = ShowFpsBox.IsChecked == true;
            s.RayTracing = RayTracingBox.IsChecked == true;
            s.Nanite = NaniteBox.IsChecked == true;
            s.ShowGrass = GrassBox.IsChecked == true;
            s.MeshQuality = Math.Max(0, MeshCombo.SelectedIndex);
            return true;
        }

        private void Competitive_Click(object sender, RoutedEventArgs e)
        {
            var isNvidia = false;
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
                foreach (var o in searcher.Get())
                    if ((Convert.ToString(o["Name"]) ?? "").Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) isNvidia = true;
            }
            catch { /* ignorato */ }

            RenderCombo.SelectedIndex = 0;
            FpsCombo.Text = _refreshHz.ToString(CultureInfo.InvariantCulture);
            ReflexCombo.SelectedIndex = isNvidia ? 1 : 0;
            WindowCombo.SelectedIndex = 1;
            PresetCombo.SelectedIndex = (int)QualityPreset.Competitive;
            VSyncBox.IsChecked = false;
            MotionBlurBox.IsChecked = false;
            ShowFpsBox.IsChecked = true;
            RayTracingBox.IsChecked = false;
            NaniteBox.IsChecked = false;
            GrassBox.IsChecked = false;
            MeshCombo.SelectedIndex = 0;
            Log.Info("Preset competitivo caricato: controlla i valori e premi 'Salva in Fortnite'.");
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            if (!Collect(_current)) return;
            try
            {
                FortniteConfigService.Save(_current);
                MessageBox.Show(Window.GetWindow(this)!, "Impostazioni salvate. Avvia Fortnite per usarle.\nÈ stato creato un backup del file precedente.",
                    "FN Boost", MessageBoxButton.OK, MessageBoxImage.Information);
                LoadSettings();
                LoadBackups();
            }
            catch (Exception ex)
            {
                Log.Error("Salvataggio impostazioni Fortnite", ex);
                MessageBox.Show(Window.GetWindow(this)!, ex.Message, "FN Boost", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void LoadBackups() => BackupList.ItemsSource = FortniteConfigService.ListBackups();

        private void RestoreBackup_Click(object sender, RoutedEventArgs e)
        {
            if (BackupList.SelectedItem is not FileInfo f) return;
            try
            {
                FortniteConfigService.RestoreBackup(f);
                LoadSettings();
                LoadBackups();
                Log.Info("Configurazione Fortnite ripristinata.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this)!, ex.Message, "FN Boost", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BackupNow_Click(object sender, RoutedEventArgs e)
        {
            if (!FortniteConfigService.ConfigExists) return;
            try
            {
                FortniteConfigService.Backup("manuale");
                LoadBackups();
            }
            catch (Exception ex)
            {
                Log.Error("Backup manuale", ex);
            }
        }

        private void Reload_Click(object sender, RoutedEventArgs e) => LoadAll();

        private void OpenConfig_Click(object sender, RoutedEventArgs e)
        {
            var dir = Path.GetDirectoryName(FortniteLocator.ConfigFile)!;
            if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }

        private void OpenBackups_Click(object sender, RoutedEventArgs e)
        {
            AppPaths.Ensure();
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.IniBackupDir}\"") { UseShellExecute = true });
        }
    }
}
