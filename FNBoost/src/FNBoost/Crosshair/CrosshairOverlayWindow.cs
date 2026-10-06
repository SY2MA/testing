using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FNBoost.Core;

namespace FNBoost.Crosshair
{
    /// <summary>
    /// Finestra trasparente, sempre in primo piano e "click-through" che disegna il mirino
    /// al centro del monitor scelto.
    ///
    /// Perché è il metodo più sicuro rispetto all'anti-cheat:
    ///  • è una normale finestra di Windows di un altro processo (come un widget o l'OSD del monitor);
    ///  • NON inietta DLL, NON aggancia DirectX, NON legge/scrive la memoria del gioco;
    ///  • NON apre handle verso il processo di Fortnite e non invia input;
    ///  • i click la attraversano (WS_EX_TRANSPARENT) e non prende mai il focus (WS_EX_NOACTIVATE).
    /// </summary>
    public sealed class CrosshairOverlayWindow : Window
    {
        private const int WM_DISPLAYCHANGE = 0x007E;

        private readonly CrosshairSettings _settings;
        private readonly CrosshairCanvas _canvas;
        private readonly Border _label;
        private readonly TextBlock _labelText;
        private readonly DispatcherTimer _rgbTimer;
        private HwndSource? _source;
        private IntPtr _hwnd;
        private bool _repositioning;
        private bool _closed;
        private bool _labelActive;
        private int _labelSeq;

        public CrosshairOverlayWindow(CrosshairSettings settings)
        {
            _settings = settings;
            Title = "FN Boost Crosshair";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Focusable = false;
            IsHitTestVisible = false;
            SizeToContent = SizeToContent.Manual;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -10000;
            Top = -10000;
            Width = 64;
            Height = 64;

            _canvas = new CrosshairCanvas(settings);
            // Nome del preset: piccola etichetta che appare sotto al mirino e svanisce (vedi ShowPresetLabel).
            _labelText = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 280
            };
            _label = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0B, 0x0F, 0x18)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 3, 9, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                Child = _labelText
            };
            var root = new Grid { IsHitTestVisible = false };
            root.Children.Add(_canvas);
            root.Children.Add(_label);
            Content = root;

            // Timer del colore cangiante: gira SOLO con RGB attivo e finestra visibile, si ferma alla chiusura.
            _rgbTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _rgbTimer.Tick += OnRgbTick;
            IsVisibleChanged += OnVisibleChanged;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
            ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW |
                  Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
            // Cambio di risoluzione / monitor collegati: si ricentra da solo.
            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(WndProc);
            Reposition();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_DISPLAYCHANGE && !_closed) Dispatcher.BeginInvoke(new Action(Reposition));
            return IntPtr.Zero;
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            if (!_repositioning && !_closed) Dispatcher.BeginInvoke(new Action(Reposition));
        }

        protected override void OnClosed(EventArgs e)
        {
            _closed = true;
            _rgbTimer.Stop();
            _rgbTimer.Tick -= OnRgbTick;
            IsVisibleChanged -= OnVisibleChanged;
            _label.BeginAnimation(OpacityProperty, null);
            // L'hook vive e muore con l'HwndSource della finestra (già in distruzione qui): basta lasciarlo andare.
            _source = null;
            base.OnClosed(e);
        }

        /// <summary>Centra la finestra sul monitor (in pixel fisici), aggiorna effetti e ridisegna.</summary>
        public void Reposition()
        {
            if (_hwnd == IntPtr.Zero || _closed) return;
            var mon = Monitors.Find(_settings.Monitor);
            if (mon == null) return;

            _repositioning = true;
            try
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                int ext = CrosshairRenderer.Extent(_settings);
                int w = ext * 2, h = ext * 2;
                int labelTop = 0;
                if (_labelActive)
                {
                    // La finestra si allarga solo mentre l'etichetta è visibile, poi torna minima.
                    _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    int lw = (int)Math.Ceiling(_label.DesiredSize.Width * dpi.DpiScaleX) + 8;
                    int lh = (int)Math.Ceiling(_label.DesiredSize.Height * dpi.DpiScaleY);
                    labelTop = ext + (int)Math.Round(4 * dpi.DpiScaleY);
                    h = Math.Max(h, 2 * (labelTop + lh + 4));
                    w = Math.Max(w, lw);
                }
                if (w % 2 == 1) w++;
                if (h % 2 == 1) h++;
                w = Math.Max(w, 32);
                h = Math.Max(h, 32);
                _canvas.CenterX = w / 2;
                _canvas.CenterY = h / 2;
                if (_labelActive) _label.Margin = new Thickness(0, (h / 2 + labelTop) / dpi.DpiScaleY, 0, 0);

                // Centro dello schermo: per 1920 px è la colonna 960 (come il reticolo del gioco).
                int x = mon.Left + mon.Width / 2 - w / 2 + _settings.OffsetX;
                int y = mon.Top + mon.Height / 2 - h / 2 + _settings.OffsetY;

                Width = w / dpi.DpiScaleX;
                Height = h / dpi.DpiScaleY;
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
                UpdateEffects();
                _canvas.InvalidateVisual();
            }
            finally
            {
                _repositioning = false;
            }
        }

        /// <summary>Riafferma il "sempre in primo piano" (alcune app lo scavalcano).</summary>
        public void EnsureTopmost()
        {
            if (_hwnd == IntPtr.Zero || _closed) return;
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        /// <summary>Mostra il nome del preset sotto al mirino per ~1,2 s con dissolvenza finale.</summary>
        public void ShowPresetLabel(string name)
        {
            if (_closed || string.IsNullOrWhiteSpace(name)) return;
            int seq = ++_labelSeq;
            _labelText.Text = name;
            _labelActive = true;
            _label.Visibility = Visibility.Visible;
            Reposition();

            var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1.2) };
            anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.7))));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2))));
            anim.Completed += (_, _) =>
            {
                // Se nel frattempo è stato richiesto un altro preset, questa animazione è superata.
                if (seq != _labelSeq || _closed) return;
                _labelActive = false;
                _label.BeginAnimation(OpacityProperty, null);
                _label.Visibility = Visibility.Collapsed;
                Reposition();
            };
            _label.BeginAnimation(OpacityProperty, anim);
        }

        private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateRgbTimer();

        /// <summary>Bagliore + avvio/arresto del ciclo RGB in base alle impostazioni correnti.</summary>
        private void UpdateEffects()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            if (!_settings.RgbCycle) _canvas.ColorOverride = null;
            _canvas.Effect = CrosshairRenderer.UpdateGlow(_canvas.Effect, _settings, _canvas.ColorOverride, 1.0 / dpi.DpiScaleX);
            UpdateRgbTimer();
        }

        private void UpdateRgbTimer()
        {
            bool run = !_closed && _settings.RgbCycle && IsVisible;
            if (run && !_rgbTimer.IsEnabled) _rgbTimer.Start();
            else if (!run && _rgbTimer.IsEnabled) _rgbTimer.Stop();
        }

        private void OnRgbTick(object? sender, EventArgs e)
        {
            if (_closed || !_settings.RgbCycle)
            {
                _rgbTimer.Stop();
                return;
            }
            // Colore solo a runtime: NON viene scritto nelle impostazioni (niente salvataggi a raffica).
            _canvas.ColorOverride = CrosshairRenderer.RgbAt(CrosshairRenderer.Seconds, _settings.RgbSpeed);
            if (_settings.Glow)
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                _canvas.Effect = CrosshairRenderer.UpdateGlow(_canvas.Effect, _settings, _canvas.ColorOverride, 1.0 / dpi.DpiScaleX);
            }
            _canvas.InvalidateVisual();
        }
    }

    /// <summary>Elemento che disegna il mirino in pixel fisici, indipendentemente dal ridimensionamento DPI.</summary>
    public sealed class CrosshairCanvas : FrameworkElement
    {
        private readonly CrosshairSettings _settings;

        /// <summary>Colonna del pixel centrale (pixel fisici).</summary>
        public int CenterX { get; set; } = 32;
        /// <summary>Riga del pixel centrale (pixel fisici).</summary>
        public int CenterY { get; set; } = 32;

        /// <summary>Compatibilità v1: imposta lo stesso centro su entrambi gli assi.</summary>
        public int CenterPx
        {
            get => CenterX;
            set => CenterX = CenterY = value;
        }

        /// <summary>Colore principale a runtime (ciclo RGB). null = colore delle impostazioni.</summary>
        public Color? ColorOverride { get; set; }

        public CrosshairCanvas(CrosshairSettings settings)
        {
            _settings = settings;
            SnapsToDevicePixels = true;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            dc.PushTransform(new ScaleTransform(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY));
            CrosshairRenderer.Draw(dc, _settings, CenterX, CenterY, ColorOverride);
            dc.Pop();
        }
    }
}
