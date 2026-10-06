using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace FNBoost.Crosshair
{
    /// <summary>Anteprima ingrandita del mirino su uno sfondo che simula una scena di gioco.</summary>
    public sealed class CrosshairPreview : FrameworkElement
    {
        public static readonly DependencyProperty SettingsProperty = DependencyProperty.Register(
            nameof(Settings), typeof(CrosshairSettings), typeof(CrosshairPreview),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSettingsChanged));

        public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
            nameof(Zoom), typeof(double), typeof(CrosshairPreview),
            new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(
            nameof(Scene), typeof(int), typeof(CrosshairPreview),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

        public CrosshairSettings? Settings
        {
            get => (CrosshairSettings?)GetValue(SettingsProperty);
            set => SetValue(SettingsProperty, value);
        }

        public double Zoom
        {
            get => (double)GetValue(ZoomProperty);
            set => SetValue(ZoomProperty, value);
        }

        /// <summary>0 = cielo, 1 = vegetazione, 2 = neve, 3 = scuro.</summary>
        public int Scene
        {
            get => (int)GetValue(SceneProperty);
            set => SetValue(SceneProperty, value);
        }

        public CrosshairPreview()
        {
            ClipToBounds = true;
            SnapsToDevicePixels = true;
        }

        private static void OnSettingsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var p = (CrosshairPreview)d;
            if (e.OldValue is CrosshairSettings o) o.PropertyChanged -= p.OnSettingPropertyChanged;
            if (e.NewValue is CrosshairSettings n) n.PropertyChanged += p.OnSettingPropertyChanged;
        }

        private void OnSettingPropertyChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

        protected override void OnRender(DrawingContext dc)
        {
            var w = ActualWidth;
            var h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            DrawScene(dc, w, h);

            var s = Settings;
            if (s == null) return;
            var z = Math.Max(1, Math.Round(Zoom));
            dc.PushTransform(new ScaleTransform(z, z));
            int cx = (int)Math.Floor(w / z / 2);
            int cy = (int)Math.Floor(h / z / 2);
            CrosshairRenderer.Draw(dc, s, cx, cy);
            dc.Pop();
        }

        private void DrawScene(DrawingContext dc, double w, double h)
        {
            Color top, bottom, ground, block;
            switch (Scene)
            {
                case 1: top = Color.FromRgb(0x7F, 0xB4, 0xE6); bottom = Color.FromRgb(0xC8, 0xE3, 0xF5); ground = Color.FromRgb(0x3E, 0x7A, 0x2E); block = Color.FromRgb(0x5C, 0x4A, 0x36); break;
                case 2: top = Color.FromRgb(0xB9, 0xD3, 0xEA); bottom = Color.FromRgb(0xEE, 0xF4, 0xFA); ground = Color.FromRgb(0xF2, 0xF5, 0xF8); block = Color.FromRgb(0x8A, 0x9B, 0xAD); break;
                case 3: top = Color.FromRgb(0x0B, 0x10, 0x1C); bottom = Color.FromRgb(0x1D, 0x24, 0x38); ground = Color.FromRgb(0x14, 0x18, 0x22); block = Color.FromRgb(0x2B, 0x33, 0x47); break;
                default: top = Color.FromRgb(0x3F, 0x7C, 0xC9); bottom = Color.FromRgb(0xF0, 0xC0, 0x8A); ground = Color.FromRgb(0x6B, 0x8F, 0x4E); block = Color.FromRgb(0x9C, 0x6B, 0x4A); break;
            }
            var horizon = h * 0.62;
            var sky = new LinearGradientBrush(top, bottom, 90);
            dc.DrawRectangle(sky, null, new Rect(0, 0, w, horizon));
            dc.DrawRectangle(new SolidColorBrush(ground), null, new Rect(0, horizon, w, h - horizon));
            var b = new SolidColorBrush(block);
            dc.DrawRectangle(b, null, new Rect(w * 0.08, horizon - h * 0.22, w * 0.14, h * 0.22));
            dc.DrawRectangle(b, null, new Rect(w * 0.70, horizon - h * 0.32, w * 0.18, h * 0.32));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), null, new Rect(w * 0.72, horizon - h * 0.26, w * 0.05, h * 0.08));
            // Sagoma al centro per valutare la visibilità sul bersaglio
            var target = new SolidColorBrush(Color.FromArgb(0xDD, 0x30, 0x2A, 0x40));
            dc.DrawEllipse(target, null, new Point(w / 2, horizon - h * 0.17), w * 0.025, w * 0.025);
            dc.DrawRoundedRectangle(target, null, new Rect(w / 2 - w * 0.03, horizon - h * 0.14, w * 0.06, h * 0.16), 6, 6);
        }
    }
}
