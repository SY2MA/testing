using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

        private CrosshairSettings S => App.Settings.Crosshair;

        public CrosshairPage()
        {
            InitializeComponent();
            DataContext = S;
            Preview.Settings = S;
            ShapeCombo.ItemsSource = Shapes;
            HotkeyHint.Text = $"Scorciatoia globale: {App.Settings.HotkeyCrosshair}";

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

            RefreshPresets();
            Loaded += (_, _) => RefreshMonitors();
        }

        private void RefreshMonitors()
        {
            var monitors = Monitors.GetAll();
            MonitorCombo.ItemsSource = monitors;
            if (string.IsNullOrEmpty(S.Monitor) || monitors.All(m => m.Device != S.Monitor))
                S.Monitor = monitors.FirstOrDefault(m => m.Primary)?.Device ?? "";
        }

        private void RefreshPresets()
        {
            PresetCombo.ItemsSource = null;
            PresetCombo.ItemsSource = App.Settings.CrosshairPresets;
            if (PresetCombo.Items.Count > 0 && PresetCombo.SelectedIndex < 0) PresetCombo.SelectedIndex = 0;
        }

        private void ApplyPreset_Click(object sender, RoutedEventArgs e)
        {
            if (PresetCombo.SelectedItem is CrosshairPreset p) S.CopyFrom(p.Settings);
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
        }

        private void DeletePreset_Click(object sender, RoutedEventArgs e)
        {
            if (PresetCombo.SelectedItem is not CrosshairPreset p) return;
            App.Settings.CrosshairPresets.Remove(p);
            App.Settings.Save();
            RefreshPresets();
        }

        private void OutlineSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string hex }) S.OutlineColor = hex;
        }

        private void PickImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Scegli un'immagine per il mirino",
                Filter = "Immagini PNG (*.png)|*.png|Tutte le immagini|*.png;*.bmp;*.gif;*.jpg;*.jpeg"
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true)
            {
                S.ImagePath = dlg.FileName;
                S.Shape = CrosshairShape.Image;
            }
        }

        private void Center_Click(object sender, RoutedEventArgs e)
        {
            S.OffsetX = 0;
            S.OffsetY = 0;
        }
    }
}
