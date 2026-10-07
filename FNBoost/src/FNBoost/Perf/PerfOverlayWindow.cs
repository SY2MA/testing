using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using FNBoost.Core;
using FNBoost.Crosshair;

namespace FNBoost.Perf
{
    /// <summary>
    /// Contatore FPS in gioco: finestra trasparente, sempre in primo piano e "click-through", posizionata
    /// in un angolo del monitor scelto. Stesso approccio sicuro del mirino (vedi CrosshairOverlayWindow):
    /// è una normale finestra di FN Boost, non tocca il gioco, non prende il focus e i click la attraversano.
    /// I numeri arrivano dal PerfService (ETW, come PresentMon / Xbox Game Bar).
    /// </summary>
    public sealed class PerfOverlayWindow : Window
    {
        private const int WM_DISPLAYCHANGE = 0x007E;

        private readonly PerfOverlaySettings _settings;
        private readonly PerfOverlayCanvas _canvas;
        private HwndSource? _source;
        private IntPtr _hwnd;
        private MonitorInfo? _monitor;
        private LiveSnapshot? _snap;
        private bool _repositioning;
        private bool _closed;
        private int _w, _h;          // dimensione attuale in pixel fisici
        private double _growW;       // larghezza minima (DIP): cresce e non si restringe, così l'overlay non "balla"

        public PerfOverlayWindow(PerfOverlaySettings settings)
        {
            _settings = settings;
            Title = "FN Boost FPS";
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
            Width = 40;
            Height = 20;

            _canvas = new PerfOverlayCanvas();
            TextOptions.SetTextRenderingMode(_canvas, TextRenderingMode.Grayscale);
            Content = _canvas;
            _canvas.ApplyColors(settings);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
            ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW |
                  Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(WndProc);
            RefreshLayout(refreshMonitor: true);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_DISPLAYCHANGE && !_closed)
                Dispatcher.BeginInvoke(new Action(() => RefreshLayout(refreshMonitor: true)));
            return IntPtr.Zero;
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            if (!_repositioning && !_closed)
                Dispatcher.BeginInvoke(new Action(() => RefreshLayout(refreshMonitor: false, force: true)));
        }

        protected override void OnClosed(EventArgs e)
        {
            _closed = true;
            _source?.RemoveHook(WndProc);
            _source = null;
            base.OnClosed(e);
        }

        /// <summary>Nuovi dati dal vivo (~4 Hz).</summary>
        public void Update(LiveSnapshot snap)
        {
            // Passando da "in attesa" ai numeri (o viceversa) la larghezza riparte da zero.
            if (_snap == null || _snap.HasData != snap.HasData || _snap.GameFocused != snap.GameFocused) _growW = 0;
            _snap = snap;
            RefreshLayout(refreshMonitor: false);
        }

        /// <summary>Impostazioni cambiate: colori, testo, dimensioni, angolo o monitor.</summary>
        public void ApplySettings()
        {
            _growW = 0;
            _canvas.ApplyColors(_settings);
            RefreshLayout(refreshMonitor: true, force: true);
        }

        /// <summary>Riafferma il "sempre in primo piano" (alcune app lo scavalcano).</summary>
        public void EnsureTopmost()
        {
            if (_hwnd == IntPtr.Zero || _closed) return;
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        /// <summary>Ricalcola il contenuto; sposta/ridimensiona la finestra solo se la dimensione in pixel cambia.</summary>
        public void RefreshLayout(bool refreshMonitor, bool force = false)
        {
            if (_hwnd == IntPtr.Zero || _closed) return;
            if (refreshMonitor || _monitor == null) _monitor = Monitors.Find(_settings.Monitor);
            var mon = _monitor;
            if (mon == null) return;

            var dpi = VisualTreeHelper.GetDpi(this);
            var size = _canvas.Build(_snap, _settings, dpi.PixelsPerDip, _growW);
            _growW = Math.Max(_growW, size.Width);
            int w = Math.Max(8, (int)Math.Ceiling(size.Width * dpi.DpiScaleX));
            int h = Math.Max(8, (int)Math.Ceiling(size.Height * dpi.DpiScaleY));

            if (force || refreshMonitor || w != _w || h != _h)
            {
                _repositioning = true;
                try
                {
                    _w = w;
                    _h = h;
                    int ox = _settings.OffsetX, oy = _settings.OffsetY;
                    bool right = _settings.Corner is OverlayCorner.TopRight or OverlayCorner.BottomRight;
                    bool bottom = _settings.Corner is OverlayCorner.BottomLeft or OverlayCorner.BottomRight;
                    int x = right ? mon.Left + mon.Width - w - ox : mon.Left + ox;
                    int y = bottom ? mon.Top + mon.Height - h - oy : mon.Top + oy;
                    // Sempre dentro al monitor, anche con spostamenti esagerati.
                    x = Math.Clamp(x, mon.Left, Math.Max(mon.Left, mon.Left + mon.Width - w));
                    y = Math.Clamp(y, mon.Top, Math.Max(mon.Top, mon.Top + mon.Height - h));

                    Width = w / dpi.DpiScaleX;
                    Height = h / dpi.DpiScaleY;
                    Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
                }
                finally
                {
                    _repositioning = false;
                }
            }
            _canvas.InvalidateVisual();
        }
    }

    /// <summary>
    /// Disegna il contenuto dell'overlay (sfondo arrotondato, righe di testo con contorno, mini grafico).
    /// Il layout si calcola in <see cref="Build"/> (4 volte al secondo) e OnRender si limita a disegnarlo.
    /// </summary>
    internal sealed class PerfOverlayCanvas : FrameworkElement
    {
        private static readonly Typeface Regular = new(new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private static readonly Typeface Bold = new(new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        private readonly List<(FormattedText Text, Point At, Geometry? Outline)> _items = new();
        private Size _size;
        private StreamGeometry? _spark;
        private Rect _sparkRect;
        private Brush _text = Brushes.White;
        private Brush _label = Brushes.White;
        private Brush _accent = Brushes.Cyan;
        private Brush _bg = Brushes.Transparent;
        private Pen _outline = new(Brushes.Black, 2);
        private Pen _sparkPen = new(Brushes.Cyan, 1.2);
        private Pen _sparkBase = new(Brushes.Gray, 1);

        public PerfOverlayCanvas()
        {
            IsHitTestVisible = false;
            SnapsToDevicePixels = true;
        }

        public void ApplyColors(PerfOverlaySettings s)
        {
            var text = ParseColor(s.TextColor, Colors.White);
            var accent = ParseColor(s.AccentColor, Color.FromRgb(0x00, 0xE5, 0xFF));
            _text = Frozen(new SolidColorBrush(text));
            _label = Frozen(new SolidColorBrush(Color.FromArgb(0xB8, text.R, text.G, text.B)));
            _accent = Frozen(new SolidColorBrush(accent));
            byte a = (byte)Math.Round(Math.Clamp(s.BackgroundOpacity, 0, 1) * 255);
            _bg = Frozen(new SolidColorBrush(Color.FromArgb(a, 0x0B, 0x0F, 0x18)));
            // Contorno scuro attorno al testo: con sfondo quasi trasparente è più marcato, per leggere su qualsiasi scena.
            double ow = Math.Max(1.5, s.FontSize / (s.BackgroundOpacity < 0.35 ? 5.5 : 8));
            _outline = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0xC8, 0, 0, 0))), ow) { LineJoin = PenLineJoin.Round });
            _sparkPen = Frozen(new Pen(_accent, 1.3) { LineJoin = PenLineJoin.Round });
            _sparkBase = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x50, text.R, text.G, text.B))), 1));
        }

        private static T Frozen<T>(T f) where T : Freezable
        {
            f.Freeze();
            return f;
        }

        private static Color ParseColor(string? hex, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            try
            {
                return ColorConverter.ConvertFromString(hex.Trim()) is Color c ? c : fallback;
            }
            catch (Exception)
            {
                // FormatException, NotSupportedException e InvalidOperationException (es. "sc#1" incompleto).
                return fallback;
            }
        }

        private static string N0(double v) => v > 0 && ChartKit.Finite(v) ? v.ToString("0", CultureInfo.CurrentCulture) : "–";

        private FormattedText Ft(string s, double size, Brush brush, bool bold, double ppd) =>
            new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? Bold : Regular, size, brush, ppd);

        private void Add(FormattedText ft, Point at) =>
            _items.Add((ft, at, ft.BuildGeometry(at)));

        /// <summary>Prepara il disegno e restituisce la dimensione necessaria (DIP).</summary>
        public Size Build(LiveSnapshot? snap, PerfOverlaySettings s, double ppd, double minWidth)
        {
            _items.Clear();
            _spark = null;
            double fs = s.FontSize;
            double pad = Math.Round(Math.Max(5, fs * 0.55));

            if (snap == null || !snap.HasData)
            {
                string msg = snap?.Status switch
                {
                    CaptureStatus.Stopped => "FPS: misurazione ferma",
                    CaptureStatus.Error => "FPS: misurazione non disponibile",
                    _ => "FPS: in attesa del gioco…"
                };
                var ft = Ft(msg, Math.Max(9, fs * 0.8), _text, false, ppd);
                Add(ft, new Point(pad, pad * 0.7));
                return Finish(new Size(ft.Width + pad * 2, ft.Height + pad * 1.4), minWidth);
            }
            if (!snap.GameFocused)
            {
                // Gioco in secondo piano (es. mentre configuri FN Boost): Fortnite scende da solo a ~30 FPS,
                // mostrarli sembrerebbe un problema del PC. Le statistiche riprendono al ritorno nel gioco.
                var ft = Ft("FPS: gioco fuori fuoco", Math.Max(9, fs * 0.8), _text, false, ppd);
                Add(ft, new Point(pad, pad * 0.7));
                return Finish(new Size(ft.Width + pad * 2, ft.Height + pad * 1.4), minWidth);
            }

            var w = snap.Window;
            string fps = N0(snap.CurrentFps);
            string ftMs = snap.LastFrametimeMs > 0 ? snap.LastFrametimeMs.ToString("0.0", CultureInfo.CurrentCulture) + " ms" : "–";
            string cpuGpu = $"{snap.CpuPercent:0}%" + (snap.GpuPercent is { } g ? $" / {g:0}%" : " / –");

            if (s.Compact)
                return BuildCompact(snap, s, ppd, pad, fps, ftMs, cpuGpu, minWidth);

            // ---- Layout a righe: etichetta a sinistra, valore allineato a destra ----
            double y = pad * 0.7;
            double width = 0;
            if (s.ShowFps)
            {
                var big = Ft(fps, fs * 1.9, _accent, true, ppd);
                var unit = Ft("FPS", fs * 0.8, _label, true, ppd);
                Add(big, new Point(pad, y));
                Add(unit, new Point(pad + big.Width + fs * 0.3, y + big.Baseline - unit.Baseline));
                width = big.Width + fs * 0.3 + unit.Width;
                y += big.Height * 0.95;
            }

            var rows = new List<(string Label, string Value, Brush? Color)>();
            if (s.ShowAvg) rows.Add(("Media", N0(w.AvgFps), null));
            if (s.Show1Low) rows.Add(("1% low", N0(w.Low1Fps), LowBrush(w.Low1Fps, w.AvgFps)));
            if (s.Show01Low) rows.Add(("0,1% low", N0(w.Low01Fps), LowBrush(w.Low01Fps, w.AvgFps * 0.8)));
            if (s.ShowMinMax) rows.Add(("Min / Max", $"{N0(w.MinFps)} / {N0(w.MaxFps)}", null));
            if (s.ShowFrametime) rows.Add(("Frametime", ftMs, null));
            if (s.ShowCpuGpu) rows.Add(("CPU / GPU", cpuGpu, null));
            if (s.ShowNet && NetValues(snap) is { } net) rows.Add(("Ping", net.Text, net.Color));

            if (rows.Count > 0)
            {
                var labels = new List<FormattedText>(rows.Count);
                var values = new List<FormattedText>(rows.Count);
                double lw = 0, vw = 0;
                foreach (var r in rows)
                {
                    var l = Ft(r.Label, fs * 0.82, _label, false, ppd);
                    var v = Ft(r.Value, fs, r.Color ?? _text, true, ppd);
                    labels.Add(l);
                    values.Add(v);
                    lw = Math.Max(lw, l.Width);
                    vw = Math.Max(vw, v.Width);
                }
                double gap = fs * 1.1;
                double rowsW = Math.Max(lw + gap + vw, minWidth - pad * 2);
                width = Math.Max(width, rowsW);
                double colRight = pad + Math.Max(rowsW, width);
                for (int i = 0; i < rows.Count; i++)
                {
                    var l = labels[i];
                    var v = values[i];
                    double rh = Math.Max(l.Height, v.Height);
                    Add(v, new Point(colRight - v.Width, y));
                    Add(l, new Point(pad, y + v.Baseline - l.Baseline));
                    y += rh * 1.02;
                }
            }

            if (s.ShowGraph && snap.RecentFrametimes.Length >= 2)
            {
                double gw = Math.Max(width, fs * 9);
                double gh = Math.Round(fs * 2.2);
                y += fs * 0.25;
                BuildSpark(snap.RecentFrametimes, new Rect(pad, y, gw, gh));
                width = Math.Max(width, gw);
                y += gh;
            }

            return Finish(new Size(width + pad * 2, y + pad * 0.8), minWidth);
        }

        private static readonly Brush WarnBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0xB8, 0x4B)));
        private static readonly Brush BadBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x5D, 0x6C)));

        /// <summary>
        /// Riga di rete: "24 ms · jitter 2 · perdita 0%" e solo "24 ms" per il layout compatto.
        /// Colore: giallo/rosso se ping, jitter o perdita superano le soglie (80/120 ms, 10/25 ms, 1/3%).
        /// </summary>
        private static (string Text, string Short, Brush? Color)? NetValues(LiveSnapshot snap)
        {
            var game = snap.Net?.Game;
            if (NetDisplay.PingValues(game) is not { } text || NetDisplay.PingMs(game) is not { } ms) return null;
            Brush? color = NetDisplay.Level(game) switch { 2 => BadBrush, 1 => WarnBrush, _ => null };
            return (text, ms.ToString("0", CultureInfo.CurrentCulture) + " ms", color);
        }

        private static Brush? LowBrush(double low, double reference)
        {
            // Il valore diventa giallo se crolla rispetto alla media (si nota subito uno stutter).
            if (!(low > 0) || !(reference > 0)) return null;
            return low < reference * 0.5 ? WarnBrush : null;
        }

        private Size BuildCompact(LiveSnapshot snap, PerfOverlaySettings s, double ppd, double pad,
            string fps, string ftMs, string cpuGpu, double minWidth)
        {
            var w = snap.Window;
            double fs = s.FontSize;
            var parts = new List<FormattedText>();
            if (s.ShowFps)
            {
                parts.Add(Ft(fps, fs * 1.15, _accent, true, ppd));
                parts.Add(Ft(" FPS", fs * 0.8, _label, true, ppd));
            }
            void Pair(string label, string value)
            {
                if (parts.Count > 0) parts.Add(Ft("   ", fs * 0.8, _label, false, ppd));
                parts.Add(Ft(label + " ", fs * 0.8, _label, false, ppd));
                parts.Add(Ft(value, fs, _text, true, ppd));
            }
            if (s.ShowAvg) Pair("media", N0(w.AvgFps));
            if (s.Show1Low) Pair("1%", N0(w.Low1Fps));
            if (s.Show01Low) Pair("0,1%", N0(w.Low01Fps));
            if (s.ShowMinMax) Pair("min/max", $"{N0(w.MinFps)}/{N0(w.MaxFps)}");
            if (s.ShowFrametime) Pair("", ftMs);
            if (s.ShowCpuGpu) Pair("CPU/GPU", cpuGpu);
            if (parts.Count == 0) parts.Add(Ft(fps + " FPS", fs, _accent, true, ppd));
            if (s.ShowNet && NetValues(snap) is { } net)
            {
                parts.Add(Ft("  ·  ", fs * 0.8, _label, false, ppd));
                parts.Add(Ft(net.Short, fs, net.Color ?? _text, true, ppd));
            }

            double baseline = 0, height = 0, x = pad;
            foreach (var p in parts) baseline = Math.Max(baseline, p.Baseline);
            foreach (var p in parts)
            {
                double py = pad * 0.5 + baseline - p.Baseline;
                Add(p, new Point(x, py));
                x += p.WidthIncludingTrailingWhitespace;
                height = Math.Max(height, py + p.Height);
            }
            return Finish(new Size(x + pad, height + pad * 0.5), minWidth);
        }

        /// <summary>Mini grafico dei frametime recenti (ultimi ~200 frame), picchi tagliati in alto.</summary>
        private void BuildSpark(float[] ft, Rect r)
        {
            int count = Math.Min(ft.Length, 200);
            int start = ft.Length - count;
            double max = 0, sum = 0;
            for (int i = start; i < ft.Length; i++)
            {
                sum += ft[i];
                if (ft[i] > max) max = ft[i];
            }
            double avg = sum / count;
            double top = Math.Clamp(max * 1.1, avg * 1.6, avg * 5);
            if (!(top > 0)) return;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                for (int i = 0; i < count; i++)
                {
                    double x = r.Left + (count == 1 ? 0 : i * r.Width / (count - 1));
                    double y = r.Bottom - Math.Min(ft[start + i], top) / top * r.Height;
                    if (i == 0) c.BeginFigure(new Point(x, y), false, false);
                    else c.LineTo(new Point(x, y), true, true);
                }
            }
            g.Freeze();
            _spark = g;
            _sparkRect = r;
        }

        private Size Finish(Size size, double minWidth)
        {
            _size = new Size(Math.Ceiling(Math.Max(size.Width, minWidth)), Math.Ceiling(size.Height));
            return _size;
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_size.Width <= 0) return;
            var r = new Rect(0, 0, _size.Width, _size.Height);
            dc.DrawRoundedRectangle(_bg, null, r, 7, 7);
            if (_spark != null)
            {
                dc.DrawLine(_sparkBase, new Point(_sparkRect.Left, _sparkRect.Bottom + 0.5), new Point(_sparkRect.Right, _sparkRect.Bottom + 0.5));
                dc.DrawGeometry(null, _outline, _spark);
                dc.DrawGeometry(null, _sparkPen, _spark);
            }
            foreach (var (text, at, outline) in _items)
            {
                if (outline != null) dc.DrawGeometry(null, _outline, outline);
                dc.DrawText(text, at);
            }
        }
    }
}
