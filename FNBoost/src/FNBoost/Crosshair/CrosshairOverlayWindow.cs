using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
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
        private readonly CrosshairSettings _settings;
        private readonly CrosshairCanvas _canvas;
        private IntPtr _hwnd;
        private bool _repositioning;

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
            Content = _canvas;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
            ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW |
                  Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
            Reposition();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            if (!_repositioning) Dispatcher.BeginInvoke(new Action(Reposition));
        }

        /// <summary>Centra la finestra sul monitor (in pixel fisici) e ridisegna.</summary>
        public void Reposition()
        {
            if (_hwnd == IntPtr.Zero) return;
            var mon = Monitors.Find(_settings.Monitor);
            if (mon == null) return;

            _repositioning = true;
            try
            {
                int size = CrosshairRenderer.Extent(_settings) * 2;
                if (size % 2 == 1) size++;
                size = Math.Max(size, 32);
                _canvas.CenterPx = size / 2;

                // Centro dello schermo: per 1920 px è la colonna 960 (come il reticolo del gioco).
                int x = mon.Left + mon.Width / 2 - size / 2 + _settings.OffsetX;
                int y = mon.Top + mon.Height / 2 - size / 2 + _settings.OffsetY;

                var dpi = VisualTreeHelper.GetDpi(this);
                Width = size / dpi.DpiScaleX;
                Height = size / dpi.DpiScaleY;
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, size, size, Native.SWP_NOACTIVATE);
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
            if (_hwnd == IntPtr.Zero) return;
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    /// <summary>Elemento che disegna il mirino in pixel fisici, indipendentemente dal ridimensionamento DPI.</summary>
    public sealed class CrosshairCanvas : FrameworkElement
    {
        private readonly CrosshairSettings _settings;
        public int CenterPx { get; set; } = 32;

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
            CrosshairRenderer.Draw(dc, _settings, CenterPx, CenterPx);
            dc.Pop();
        }
    }
}
