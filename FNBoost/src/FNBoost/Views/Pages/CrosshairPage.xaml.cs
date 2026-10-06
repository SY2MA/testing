using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FNBoost.Core;
using FNBoost.Crosshair;

namespace FNBoost.Views.Pages
{
    public sealed class ShapeOption
    {
        public CrosshairShape Value { get; init; }
        public string Label { get; init; } = "";
    }

    public partial class CrosshairPage : UserControl
    {
        public static readonly string[] Palette =
        {
            "#00FF66", "#00E5FF", "#FFFF00", "#FF2D55", "#FF4DFF", "#FF8A00", "#FFFFFF", "#7C5CFF"
        };

        /// <summary>Modelli rapidi: scegliendone uno si impostano i livelli (vedi CrosshairSettings.ApplyTemplate).</summary>
        public static readonly List<ShapeOption> Shapes = new()
        {
            new() { Value = CrosshairShape.CrossDot, Label = "Croce + punto" },
            new() { Value = CrosshairShape.Cross, Label = "Croce" },
            new() { Value = CrosshairShape.Dot, Label = "Punto" },
            new() { Value = CrosshairShape.Circle, Label = "Cerchio" },
            new() { Value = CrosshairShape.CircleDot, Label = "Cerchio + punto" },
            new() { Value = CrosshairShape.TShape, Label = "T (senza braccio superiore)" },
            new() { Value = CrosshairShape.XShape, Label = "X diagonale" },
            new() { Value = CrosshairShape.Image, Label = "Immagine personalizzata" },
        };

        /// <summary>Screenshot di sfondo dell'anteprima: resta per tutta la sessione (non viene salvato).</summary>
        private static string _screenshot = "";

        private bool _syncingShape;
        private bool _subscribed;

        private CrosshairSettings S => App.Settings.Crosshair;

        public CrosshairPage()
        {
            InitializeComponent();
            DataContext = S;
            Preview.Settings = S;
            _syncingShape = true;
            ShapeCombo.ItemsSource = Shapes;
            _syncingShape = false;

            foreach (var hex in Palette)
            {
                var b = new Button
                {
                    Style = (Style)FindResource("Swatch"),
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                    Tag = hex,
                    ToolTip = hex
                };
                b.Click += (_, _) => S.Color = hex;
                Swatches.Children.Add(b);
            }

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // I preset salvati dalla v1 vengono convertiti qui (App.xaml.cs non lo fa dopo il caricamento).
            CrosshairPreset.NormalizeAll(App.Settings.CrosshairPresets);
            RefreshMonitors();
            RefreshPresets();
            HotkeyHint.Text = $"Scorciatoia globale: {App.Settings.HotkeyCrosshair}";
            NextPresetHint.Text = $"Scorciatoia preset successivo: {App.Settings.HotkeyNextPreset}";
            if (!_subscribed)
            {
                S.PropertyChanged += OnSettingChanged;
                App.Crosshair.PresetApplied += OnPresetApplied;
                _subscribed = true;
            }
            SyncShape();
            if (_screenshot.Length > 0 && Preview.BackgroundImagePath != _screenshot) Preview.BackgroundImagePath = _screenshot;
            ResetScreenshotBtn.IsEnabled = Preview.HasBackgroundImage;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            // La pagina resta in cache: ci si stacca dagli eventi quando non è visibile.
            if (!_subscribed) return;
            S.PropertyChanged -= OnSettingChanged;
            App.Crosshair.PresetApplied -= OnPresetApplied;
            _subscribed = false;
        }

        private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CrosshairSettings.Shape)) SyncShape();
        }

        private void OnPresetApplied(string name)
        {
            var p = App.Settings.CrosshairPresets.FirstOrDefault(x => x.Name == name);
            if (p != null) PresetCombo.SelectedItem = p;
        }

        // ---------------- Modello ----------------

        private void SyncShape()
        {
            _syncingShape = true;
            try
            {
                ShapeCombo.SelectedValue = S.Shape;
            }
            finally
            {
                _syncingShape = false;
            }
        }

        private void ShapeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Solo scelte dell'utente: niente modelli applicati durante l'inizializzazione o la sincronizzazione.
            if (_syncingShape || !IsLoaded || ShapeCombo.SelectedValue is not CrosshairShape shape) return;
            S.ApplyTemplate(shape);
            if (shape == CrosshairShape.Image && string.IsNullOrEmpty(S.ImagePath)) PickImage_Click(sender, e);
        }

        private void MainColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string which }) return;
            switch (which)
            {
                case "Dot": S.DotColor = ""; break;
                case "Circle": S.CircleColor = ""; break;
                case "Glow": S.GlowColor = ""; break;
            }
        }

        private void OutlineSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string hex }) S.OutlineColor = hex;
        }

        // ---------------- Posizione ----------------

        private void RefreshMonitors()
        {
            var monitors = Monitors.GetAll();
            MonitorCombo.ItemsSource = monitors;
            if (string.IsNullOrEmpty(S.Monitor) || monitors.All(m => m.Device != S.Monitor))
                S.Monitor = monitors.FirstOrDefault(m => m.Primary)?.Device ?? "";
        }

        private void Rotation_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string t } && int.TryParse(t, out var deg)) S.Rotation = deg;
        }

        private void Nudge_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string dir }) return;
            switch (dir)
            {
                case "U": S.OffsetY -= 1; break;
                case "D": S.OffsetY += 1; break;
                case "L": S.OffsetX -= 1; break;
                case "R": S.OffsetX += 1; break;
            }
        }

        private void Center_Click(object sender, RoutedEventArgs e)
        {
            S.OffsetX = 0;
            S.OffsetY = 0;
        }

        // ---------------- Immagine ----------------

        private void PickImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Scegli un'immagine per il mirino",
                Filter = "Immagini PNG (*.png)|*.png|Tutte le immagini|*.png;*.bmp;*.gif;*.jpg;*.jpeg"
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true)
            {
                CrosshairRenderer.ResetImageCache(); // ricarica anche se è lo stesso file modificato
                S.ImagePath = dlg.FileName;
                S.UseImage = true;
            }
        }

        // ---------------- Anteprima ----------------

        private void PickScreenshot_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Scegli uno screenshot di Fortnite",
                Filter = "Immagini (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|Tutti i file|*.*"
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            Preview.BackgroundImagePath = dlg.FileName;
            if (Preview.HasBackgroundImage)
            {
                _screenshot = dlg.FileName;
            }
            else
            {
                Preview.BackgroundImagePath = "";
                _screenshot = "";
                MessageBox.Show(Window.GetWindow(this)!, "Impossibile aprire l'immagine scelta. Prova con un PNG o un JPG.", "FN Boost",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            ResetScreenshotBtn.IsEnabled = Preview.HasBackgroundImage;
        }

        private void ResetScreenshot_Click(object sender, RoutedEventArgs e)
        {
            _screenshot = "";
            Preview.BackgroundImagePath = "";
            ResetScreenshotBtn.IsEnabled = false;
        }

        // ---------------- Preset ----------------

        private void RefreshPresets()
        {
            var selected = PresetCombo.SelectedItem;
            PresetCombo.ItemsSource = null;
            PresetCombo.ItemsSource = App.Settings.CrosshairPresets;
            if (selected != null && App.Settings.CrosshairPresets.Contains(selected)) PresetCombo.SelectedItem = selected;
            if (PresetCombo.Items.Count > 0 && PresetCombo.SelectedIndex < 0) PresetCombo.SelectedIndex = 0;
        }

        private void ApplyPreset_Click(object sender, RoutedEventArgs e)
        {
            if (PresetCombo.SelectedItem is CrosshairPreset p) App.Crosshair.ApplyPreset(p);
        }

        private void NextPreset_Click(object sender, RoutedEventArgs e)
        {
            if (App.Crosshair.NextPreset() == null) SetStatus(PresetStatus, "Non ci sono preset: aggiungi quelli predefiniti o salvane uno.", "WarnBrush");
        }

        private void SavePreset_Click(object sender, RoutedEventArgs e)
        {
            var name = PresetName.Text.Trim();
            if (name.Length == 0)
            {
                MessageBox.Show(Window.GetWindow(this)!, "Scrivi un nome per il preset.", "FN Boost");
                return;
            }
            var existing = App.Settings.CrosshairPresets.FirstOrDefault(x => x.Name == name);
            if (existing != null) existing.Settings = S.Clone();
            else App.Settings.CrosshairPresets.Add(new CrosshairPreset { Name = name, Settings = S.Clone() });
            App.Settings.Save();
            RefreshPresets();
            PresetCombo.SelectedItem = App.Settings.CrosshairPresets.First(x => x.Name == name);
            PresetName.Text = "";
            SetStatus(PresetStatus, existing != null ? $"Preset \"{name}\" aggiornato." : $"Preset \"{name}\" salvato.", "OkBrush");
        }

        private void DeletePreset_Click(object sender, RoutedEventArgs e)
        {
            if (PresetCombo.SelectedItem is not CrosshairPreset p) return;
            App.Settings.CrosshairPresets.Remove(p);
            App.Settings.Save();
            PresetCombo.SelectedItem = null;
            RefreshPresets();
            SetStatus(PresetStatus, $"Preset \"{p.Name}\" eliminato.", "MutedBrush");
        }

        private void AddBuiltIn_Click(object sender, RoutedEventArgs e)
        {
            var list = App.Settings.CrosshairPresets;
            int added = 0;
            foreach (var p in CrosshairPreset.BuiltIn())
            {
                if (list.Any(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(p);
                added++;
            }
            if (added > 0)
            {
                App.Settings.Save();
                RefreshPresets();
            }
            SetStatus(PresetStatus, added > 0 ? $"Aggiunti {added} preset predefiniti." : "Hai già tutti i preset predefiniti.", added > 0 ? "OkBrush" : "MutedBrush");
        }

        // ---------------- Codice di condivisione ----------------

        private void ExportCode_Click(object sender, RoutedEventArgs e)
        {
            var code = CrosshairShareCode.Export(S);
            ShareOut.Text = code;
            ShareOut.Focus();
            ShareOut.SelectAll();
            try
            {
                Clipboard.SetText(code);
                SetStatus(ShareStatus, "Codice copiato negli appunti: incollalo dove vuoi.", "OkBrush");
            }
            catch (Exception ex)
            {
                // Gli appunti possono essere bloccati da un'altra app: il codice resta selezionabile nel riquadro.
                Log.Warn("Copia del codice mirino negli appunti non riuscita: " + ex.Message);
                SetStatus(ShareStatus, "Appunti occupati da un'altra app: copia il codice dal riquadro (Ctrl+C).", "WarnBrush");
            }
        }

        private void ImportCode_Click(object sender, RoutedEventArgs e)
        {
            if (!CrosshairShareCode.TryImport(ShareIn.Text, out var imported, out var error) || imported == null)
            {
                SetStatus(ShareStatus, error, "BadBrush");
                return;
            }
            // Il codice non contiene mai un percorso: si mantiene l'immagine locale già scelta (se c'è).
            var keepImage = S.ImagePath;
            S.CopyFrom(imported);
            if (imported.ImagePath.Length == 0) S.ImagePath = keepImage;
            ShareIn.Text = "";
            SetStatus(ShareStatus, "Mirino importato! Salvalo come preset se vuoi tenerlo.", "OkBrush");
            Log.Info("Mirino importato da codice di condivisione");
        }

        private void SetStatus(TextBlock target, string text, string brushKey)
        {
            target.Text = text;
            target.Foreground = (Brush)FindResource(brushKey);
        }
    }
}
