using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FNBoost.Crosshair
{
    /// <summary>
    /// Anteprima del mirino: vista ingrandita su uno sfondo (scena simulata o screenshot dell'utente)
    /// più un riquadro 1:1 nell'angolo con la dimensione reale in pixel dello schermo.
    ///
    /// Struttura: lo sfondo è disegnato in OnRender; il mirino ingrandito e il riquadro 1:1 sono Visual figli,
    /// così il bagliore (DropShadowEffect) si applica solo al mirino e non alla scena, e l'animazione RGB
    /// ridisegna solo il mirino senza ricalcolare lo sfondo.
    /// </summary>
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

        public static readonly DependencyProperty BackgroundImagePathProperty = DependencyProperty.Register(
            nameof(BackgroundImagePath), typeof(string), typeof(CrosshairPreview),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender, OnBackgroundChanged));

        public static readonly DependencyProperty ShowInsetProperty = DependencyProperty.Register(
            nameof(ShowInset), typeof(bool), typeof(CrosshairPreview),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty InsetSizeProperty = DependencyProperty.Register(
            nameof(InsetSize), typeof(double), typeof(CrosshairPreview),
            new FrameworkPropertyMetadata(110.0, FrameworkPropertyMetadataOptions.AffectsRender));

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

        /// <summary>Screenshot dell'utente da usare come sfondo (vuoto = scena simulata).</summary>
        public string BackgroundImagePath
        {
            get => (string)GetValue(BackgroundImagePathProperty);
            set => SetValue(BackgroundImagePathProperty, value);
        }

        /// <summary>Mostra il riquadro 1:1 (dimensione reale) nell'angolo in basso a destra.</summary>
        public bool ShowInset
        {
            get => (bool)GetValue(ShowInsetProperty);
            set => SetValue(ShowInsetProperty, value);
        }

        /// <summary>Lato del riquadro 1:1 in DIP.</summary>
        public double InsetSize
        {
            get => (double)GetValue(InsetSizeProperty);
            set => SetValue(InsetSizeProperty, value);
        }

        /// <summary>true se lo screenshot indicato in BackgroundImagePath è stato caricato.</summary>
        public bool HasBackgroundImage => _bg != null;

        private readonly DrawingVisual _zoomed = new();
        private readonly ContainerVisual _insetHost = new();
        private readonly DrawingVisual _insetBg = new();
        private readonly DrawingVisual _insetXhair = new();
        private readonly DispatcherTimer _rgbTimer;
        private BitmapSource? _bg;
        private CrosshairSettings? _attached;
        private Color? _override;

        public CrosshairPreview()
        {
            ClipToBounds = true;
            SnapsToDevicePixels = true;
            _insetHost.Children.Add(_insetBg);
            _insetHost.Children.Add(_insetXhair);
            AddVisualChild(_zoomed);
            AddVisualChild(_insetHost);

            _rgbTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _rgbTimer.Tick += OnRgbTick;
            // Ci si abbona alle impostazioni solo mentre l'anteprima è nella pagina: nessun riferimento resta appeso.
            Loaded += (_, _) => Attach();
            Unloaded += (_, _) => Detach();
            IsVisibleChanged += (_, _) => UpdateRgbTimer();
        }

        protected override int VisualChildrenCount => 2;

        protected override Visual GetVisualChild(int index) => index switch
        {
            0 => _zoomed,
            1 => _insetHost,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };

        private static void OnSettingsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var p = (CrosshairPreview)d;
            if (p.IsLoaded) p.Attach();
        }

        private void Attach()
        {
            var s = Settings;
            if (!ReferenceEquals(_attached, s))
            {
                if (_attached != null) _attached.PropertyChanged -= OnSettingPropertyChanged;
                _attached = s;
                if (s != null) s.PropertyChanged += OnSettingPropertyChanged;
            }
            UpdateRgbTimer();
            InvalidateVisual();
        }

        private void Detach()
        {
            if (_attached != null) _attached.PropertyChanged -= OnSettingPropertyChanged;
            _attached = null;
            _rgbTimer.Stop();
        }

        private void OnSettingPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CrosshairSettings.RgbCycle)) UpdateRgbTimer();
            InvalidateVisual();
        }

        private static void OnBackgroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var p = (CrosshairPreview)d;
            p._bg = LoadBackground(e.NewValue as string);
        }

        private static BitmapSource? LoadBackground(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                if (!File.Exists(path)) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private void UpdateRgbTimer()
        {
            bool run = _attached is { RgbCycle: true } && IsLoaded && IsVisible;
            if (run && !_rgbTimer.IsEnabled) _rgbTimer.Start();
            else if (!run)
            {
                if (_rgbTimer.IsEnabled) _rgbTimer.Stop();
                if (_override != null)
                {
                    _override = null;
                    InvalidateVisual();
                }
            }
        }

        private void OnRgbTick(object? sender, EventArgs e)
        {
            var s = _attached;
            if (s == null || !s.RgbCycle)
            {
                UpdateRgbTimer();
                return;
            }
            _override = CrosshairRenderer.RgbAt(CrosshairRenderer.Seconds, s.RgbSpeed);
            RenderCrosshair(); // solo il mirino, lo sfondo resta com'è
        }

        protected override void OnRender(DrawingContext dc)
        {
            var w = ActualWidth;
            var h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            if (_bg != null) DrawScreenshot(dc, _bg, w, h);
            else DrawScene(dc, w, h, Scene);
            RenderCrosshair();
        }

        private void RenderCrosshair()
        {
            var w = ActualWidth;
            var h = ActualHeight;
            var s = Settings;
            var color = _override;
            if (s != null && !s.RgbCycle) color = null;

            // ---- Vista ingrandita: 1 pixel del mirino = Zoom DIP ----
            var z = Math.Max(1, Math.Round(double.IsNaN(Zoom) ? 3 : Zoom));
            using (var dc = _zoomed.RenderOpen())
            {
                if (s != null && w > 0 && h > 0)
                {
                    dc.PushTransform(new ScaleTransform(z, z));
                    int cx = (int)Math.Floor(w / z / 2);
                    int cy = (int)Math.Floor(h / z / 2);
                    CrosshairRenderer.Draw(dc, s, cx, cy, color);
                    dc.Pop();
                }
            }
            _zoomed.Effect = CrosshairRenderer.UpdateGlow(_zoomed.Effect, s, color, z);

            // ---- Riquadro 1:1: pixel fisici reali, come apparirà sullo schermo ----
            double size = Math.Max(48, InsetSize);
            bool inset = s != null && ShowInset && w >= size * 1.8 && h >= size + 30;
            if (!inset)
            {
                using (_insetBg.RenderOpen()) { }
                using (_insetXhair.RenderOpen()) { }
                _insetXhair.Effect = null;
                _insetHost.Clip = null;
                return;
            }

            var dpi = VisualTreeHelper.GetDpi(this);
            // Origine allineata ai pixel del dispositivo per restare nitidi.
            double x = Math.Round((w - size - 10) * dpi.DpiScaleX) / dpi.DpiScaleX;
            double y = Math.Round((h - size - 10) * dpi.DpiScaleY) / dpi.DpiScaleY;
            var box = new Rect(x, y, size, size);
            _insetHost.Clip = new RectangleGeometry(box, 6, 6);
            int pw = (int)Math.Round(size * dpi.DpiScaleX);
            int ph = (int)Math.Round(size * dpi.DpiScaleY);

            using (var dc = _insetBg.RenderOpen())
            {
                dc.PushTransform(new TranslateTransform(x, y));
                if (_bg != null)
                {
                    // Centro dello screenshot a dimensione reale (1 pixel immagine = 1 pixel schermo).
                    dc.PushTransform(new ScaleTransform(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY));
                    var group = new DrawingGroup();
                    RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.NearestNeighbor);
                    using (var g = group.Open())
                        g.DrawImage(_bg, new Rect(Math.Round(pw / 2.0 - _bg.PixelWidth / 2.0), Math.Round(ph / 2.0 - _bg.PixelHeight / 2.0),
                            _bg.PixelWidth, _bg.PixelHeight));
                    dc.DrawDrawing(group);
                    dc.Pop();
                }
                else
                {
                    DrawScene(dc, size, size, Scene);
                }
                var border = new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), 1);
                border.Freeze();
                dc.DrawRoundedRectangle(null, border, new Rect(0.5, 0.5, size - 1, size - 1), 6, 6);
                var label = new FormattedText("1:1", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 10, Brushes.White, dpi.PixelsPerDip);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), null,
                    new Rect(4, 4, label.Width + 8, label.Height + 2), 4, 4);
                dc.DrawText(label, new Point(8, 5));
                dc.Pop();
            }

            using (var dc = _insetXhair.RenderOpen())
            {
                dc.PushTransform(new TranslateTransform(x, y));
                dc.PushTransform(new ScaleTransform(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY));
                CrosshairRenderer.Draw(dc, s!, pw / 2, ph / 2, color);
                dc.Pop();
                dc.Pop();
            }
            _insetXhair.Effect = CrosshairRenderer.UpdateGlow(_insetXhair.Effect, s, color, 1.0 / dpi.DpiScaleX);
        }

        /// <summary>Screenshot "uniform to fill": riempie tutta l'anteprima mantenendo le proporzioni.</summary>
        private static void DrawScreenshot(DrawingContext dc, BitmapSource img, double w, double h)
        {
            double iw = img.PixelWidth, ih = img.PixelHeight;
            if (iw <= 0 || ih <= 0) return;
            double scale = Math.Max(w / iw, h / ih);
            double dw = iw * scale, dh = ih * scale;
            var group = new DrawingGroup();
            RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.HighQuality);
            using (var g = group.Open()) g.DrawImage(img, new Rect((w - dw) / 2, (h - dh) / 2, dw, dh));
            dc.DrawDrawing(group);
        }

        private static void DrawScene(DrawingContext dc, double w, double h, int scene)
        {
            Color top, bottom, ground, block;
            switch (scene)
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
