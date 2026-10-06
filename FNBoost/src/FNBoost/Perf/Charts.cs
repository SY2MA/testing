using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FNBoost.Perf
{
    /// <summary>
    /// Utilità comuni dei grafici: colori del tema (con valori di riserva), testi e scale "tonde".
    /// I grafici disegnano tutto in OnRender con geometrie congelate: niente elementi visuali per punto.
    /// </summary>
    internal static class ChartKit
    {
        public static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        public static Brush Res(string key, Color fallback)
        {
            if (Application.Current?.TryFindResource(key) is Brush b) return b;
            var sb = new SolidColorBrush(fallback);
            sb.Freeze();
            return sb;
        }

        public static Brush Text => Res("TextBrush", Color.FromRgb(0xE8, 0xED, 0xF5));
        public static Brush Muted => Res("MutedBrush", Color.FromRgb(0x8D, 0x98, 0xAD));
        public static Brush Accent => Res("AccentBrush", Color.FromRgb(0x7C, 0x5C, 0xFF));
        public static Brush Accent2 => Res("Accent2Brush", Color.FromRgb(0x00, 0xD1, 0xFF));
        public static Brush Ok => Res("OkBrush", Color.FromRgb(0x3F, 0xD0, 0x7A));
        public static Brush Warn => Res("WarnBrush", Color.FromRgb(0xF2, 0xB8, 0x4B));
        public static Brush Bad => Res("BadBrush", Color.FromRgb(0xFF, 0x5D, 0x6C));

        private static Pen? _grid;

        /// <summary>Linee della griglia (bianco molto tenue): stessa penna per tutti i grafici.</summary>
        public static Pen GridPen
        {
            get
            {
                if (_grid != null) return _grid;
                var p = new Pen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), 1);
                p.Freeze();
                return _grid = p;
            }
        }

        public static Pen MakePen(Brush brush, double thickness, bool dashed = false)
        {
            var p = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (dashed) p.DashStyle = new DashStyle(new double[] { 4, 3 }, 0);
            p.Freeze();
            return p;
        }

        public static Brush WithOpacity(Brush brush, double opacity)
        {
            if (brush is SolidColorBrush sc)
            {
                var c = sc.Color;
                var b = new SolidColorBrush(Color.FromArgb((byte)Math.Round(c.A * opacity), c.R, c.G, c.B));
                b.Freeze();
                return b;
            }
            var clone = brush.CloneCurrentValue();
            clone.Opacity = opacity;
            clone.Freeze();
            return clone;
        }

        public static FormattedText Txt(Visual v, string s, double size, Brush brush, bool bold = false)
        {
            var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                Face, size, brush, VisualTreeHelper.GetDpi(v).PixelsPerDip);
            if (bold) ft.SetFontWeight(FontWeights.SemiBold);
            return ft;
        }

        /// <summary>Passo "tondo" (1, 2, 2,5, 5 × 10^k) vicino a raw.</summary>
        public static double NiceStep(double raw)
        {
            if (!(raw > 0) || double.IsInfinity(raw)) return 1;
            double exp = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double f = raw / exp;
            double nice = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10;
            return nice * exp;
        }

        public static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        /// <summary>Formatta un valore con un formato .NET; formato vuoto o non valido = intero.</summary>
        public static string Format(double v, string? format)
        {
            try
            {
                return v.ToString(string.IsNullOrEmpty(format) ? "0" : format, CultureInfo.CurrentCulture);
            }
            catch (FormatException)
            {
                return v.ToString("0", CultureInfo.CurrentCulture);
            }
        }

        public static void DrawEmpty(DrawingContext dc, Visual v, Size size, string text = "Nessun dato")
        {
            var ft = Txt(v, text, 12, Muted);
            dc.DrawText(ft, new Point(Math.Max(0, (size.Width - ft.Width) / 2), Math.Max(0, (size.Height - ft.Height) / 2)));
        }
    }

    /// <summary>
    /// Grafico a linee leggero: una o due serie, griglia con etichette, linea di riferimento (es. refresh o limite FPS)
    /// e area sfumata sotto la serie principale. Con migliaia di punti decima a una coppia min/max per colonna di pixel.
    /// I valori NaN interrompono la linea (es. GPU non disponibile).
    /// </summary>
    public sealed class LineChart : FrameworkElement
    {
        public static readonly DependencyProperty ValuesProperty = Dp<IReadOnlyList<double>?>(nameof(Values), null);
        public static readonly DependencyProperty Values2Property = Dp<IReadOnlyList<double>?>(nameof(Values2), null);
        public static readonly DependencyProperty MinYProperty = Dp(nameof(MinY), double.NaN);
        public static readonly DependencyProperty MaxYProperty = Dp(nameof(MaxY), double.NaN);
        public static readonly DependencyProperty ReferenceValueProperty = Dp(nameof(ReferenceValue), double.NaN);
        public static readonly DependencyProperty ReferenceLabelProperty = Dp<string?>(nameof(ReferenceLabel), null);
        public static readonly DependencyProperty StrokeProperty = Dp<Brush?>(nameof(Stroke), null);
        public static readonly DependencyProperty Stroke2Property = Dp<Brush?>(nameof(Stroke2), null);
        public static readonly DependencyProperty FillProperty = Dp<Brush?>(nameof(Fill), null);
        public static readonly DependencyProperty XLabelProperty = Dp<string?>(nameof(XLabel), null);
        public static readonly DependencyProperty ValueFormatProperty = Dp<string?>(nameof(ValueFormat), "0");
        public static readonly DependencyProperty LegendProperty = Dp<string?>(nameof(Legend), null);
        public static readonly DependencyProperty Legend2Property = Dp<string?>(nameof(Legend2), null);

        private static DependencyProperty Dp<T>(string name, T def) =>
            DependencyProperty.Register(name, typeof(T), typeof(LineChart),
                new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

        public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
        /// <summary>Seconda serie opzionale (es. 1% low), disegnata sottile.</summary>
        public IReadOnlyList<double>? Values2 { get => (IReadOnlyList<double>?)GetValue(Values2Property); set => SetValue(Values2Property, value); }
        /// <summary>NaN = 0.</summary>
        public double MinY { get => (double)GetValue(MinYProperty); set => SetValue(MinYProperty, value); }
        /// <summary>NaN = automatico, arrotondato a un valore "tondo".</summary>
        public double MaxY { get => (double)GetValue(MaxYProperty); set => SetValue(MaxYProperty, value); }
        /// <summary>Linea orizzontale tratteggiata (NaN = nessuna).</summary>
        public double ReferenceValue { get => (double)GetValue(ReferenceValueProperty); set => SetValue(ReferenceValueProperty, value); }
        public string? ReferenceLabel { get => (string?)GetValue(ReferenceLabelProperty); set => SetValue(ReferenceLabelProperty, value); }
        public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
        public Brush? Stroke2 { get => (Brush?)GetValue(Stroke2Property); set => SetValue(Stroke2Property, value); }
        /// <summary>Riempimento sotto la serie principale (null = sfumatura automatica dal colore della linea).</summary>
        public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
        public string? XLabel { get => (string?)GetValue(XLabelProperty); set => SetValue(XLabelProperty, value); }
        /// <summary>Formato .NET delle etichette dell'asse Y (es. "0", "0.0", "0'%'").</summary>
        public string? ValueFormat { get => (string?)GetValue(ValueFormatProperty); set => SetValue(ValueFormatProperty, value); }
        public string? Legend { get => (string?)GetValue(LegendProperty); set => SetValue(LegendProperty, value); }
        public string? Legend2 { get => (string?)GetValue(Legend2Property); set => SetValue(Legend2Property, value); }

        // Buffer riutilizzati tra un disegno e l'altro (il grafico dal vivo si ridisegna 4 volte al secondo).
        private readonly List<Point> _pts = new();

        public LineChart()
        {
            SnapsToDevicePixels = true;
            ClipToBounds = true;
            MinHeight = 60;
        }

        protected override Size MeasureOverride(Size availableSize) =>
            new(double.IsInfinity(availableSize.Width) ? 300 : 0, double.IsInfinity(availableSize.Height) ? 140 : 0);

        protected override void OnRender(DrawingContext dc)
        {
            var size = RenderSize;
            if (size.Width < 40 || size.Height < 40) return;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(size)); // area per i tooltip

            var v1 = Values;
            var v2 = Values2;
            bool has1 = HasFinite(v1), has2 = HasFinite(v2);
            if (!has1 && !has2)
            {
                ChartKit.DrawEmpty(dc, this, size);
                return;
            }

            // ---- Scala Y ----
            double dataMax = double.MinValue, dataMin = double.MaxValue;
            Scan(v1, ref dataMin, ref dataMax);
            Scan(v2, ref dataMin, ref dataMax);
            double refV = ReferenceValue;
            if (ChartKit.Finite(refV)) dataMax = Math.Max(dataMax, refV);

            double minY = ChartKit.Finite(MinY) ? MinY : Math.Min(0, dataMin);
            double maxY;
            double step;
            if (ChartKit.Finite(MaxY) && MaxY > minY)
            {
                maxY = MaxY;
                step = ChartKit.NiceStep((maxY - minY) / 4);
            }
            else
            {
                double top = Math.Max(dataMax * 1.08, minY + 1);
                step = ChartKit.NiceStep((top - minY) / 4);
                maxY = minY + Math.Ceiling((top - minY) / step) * step;
            }
            if (!(maxY > minY)) maxY = minY + 1;

            // ---- Margini: etichette a sinistra, legenda/asse X in basso ----
            var fmt = ValueFormat;
            var muted = ChartKit.Muted;
            double labelW = 0;
            var labels = new List<(double y, FormattedText ft)>();
            for (double t = minY; t <= maxY + step * 0.001; t += step)
            {
                var ft = ChartKit.Txt(this, ChartKit.Format(t, fmt), 10.5, muted);
                labels.Add((t, ft));
                labelW = Math.Max(labelW, ft.Width);
                if (labels.Count > 12) break;
            }
            double left = Math.Ceiling(labelW) + 8, right = 6, top0 = 8;
            bool footer = !string.IsNullOrEmpty(XLabel) || !string.IsNullOrEmpty(Legend) || !string.IsNullOrEmpty(Legend2);
            double bottom = footer ? 20 : 6;
            var plot = new Rect(left, top0, Math.Max(10, size.Width - left - right), Math.Max(10, size.Height - top0 - bottom));
            double Y(double v) => plot.Bottom - (Math.Clamp(v, minY, maxY) - minY) / (maxY - minY) * plot.Height;

            // ---- Griglia ----
            foreach (var (val, ft) in labels)
            {
                double y = Math.Round(Y(val)) + 0.5;
                dc.DrawLine(ChartKit.GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                dc.DrawText(ft, new Point(left - 6 - ft.Width, y - ft.Height / 2));
            }

            // ---- Serie ----
            var stroke1 = Stroke ?? ChartKit.Accent2;
            var stroke2 = Stroke2 ?? ChartKit.Warn;
            if (has1)
            {
                var fill = Fill ?? DefaultFill(stroke1);
                DrawSeries(dc, v1!, plot, Y, ChartKit.MakePen(stroke1, 1.8), fill);
            }
            if (has2) DrawSeries(dc, v2!, plot, Y, ChartKit.MakePen(stroke2, 1.2), null);

            // ---- Riferimento ----
            if (ChartKit.Finite(refV) && refV >= minY && refV <= maxY)
            {
                double y = Math.Round(Y(refV)) + 0.5;
                dc.DrawLine(ChartKit.MakePen(ChartKit.WithOpacity(ChartKit.Text, 0.55), 1, dashed: true),
                    new Point(plot.Left, y), new Point(plot.Right, y));
                if (!string.IsNullOrEmpty(ReferenceLabel))
                {
                    var ft = ChartKit.Txt(this, ReferenceLabel!, 10.5, ChartKit.Text);
                    double ly = y - ft.Height - 1 < plot.Top ? y + 2 : y - ft.Height - 1;
                    dc.DrawRectangle(ChartKit.WithOpacity(Brushes.Black, 0.45), null,
                        new Rect(plot.Right - ft.Width - 8, ly, ft.Width + 6, ft.Height));
                    dc.DrawText(ft, new Point(plot.Right - ft.Width - 5, ly));
                }
            }

            // ---- Piè di pagina: asse X a sinistra, legenda a destra ----
            if (footer)
            {
                double fy = size.Height - 16;
                if (!string.IsNullOrEmpty(XLabel))
                    dc.DrawText(ChartKit.Txt(this, XLabel!, 10.5, muted), new Point(plot.Left, fy));
                double x = plot.Right;
                if (has2 && !string.IsNullOrEmpty(Legend2)) x = DrawLegend(dc, Legend2!, stroke2, x, fy);
                if (has1 && !string.IsNullOrEmpty(Legend)) DrawLegend(dc, Legend!, stroke1, x - 10, fy);
            }
        }

        private double DrawLegend(DrawingContext dc, string text, Brush brush, double rightX, double y)
        {
            var ft = ChartKit.Txt(this, text, 10.5, ChartKit.Muted);
            double tx = rightX - ft.Width;
            dc.DrawRoundedRectangle(brush, null, new Rect(tx - 14, y + ft.Height / 2 - 2, 10, 4), 2, 2);
            dc.DrawText(ft, new Point(tx, y));
            return tx - 18;
        }

        private static Brush DefaultFill(Brush stroke)
        {
            var c = stroke is SolidColorBrush sc ? sc.Color : Color.FromRgb(0x00, 0xD1, 0xFF);
            var g = new LinearGradientBrush(Color.FromArgb(0x55, c.R, c.G, c.B), Color.FromArgb(0x00, c.R, c.G, c.B), 90);
            g.Freeze();
            return g;
        }

        private static bool HasFinite(IReadOnlyList<double>? v)
        {
            if (v == null) return false;
            for (int i = 0; i < v.Count; i++)
                if (ChartKit.Finite(v[i])) return true;
            return false;
        }

        private static void Scan(IReadOnlyList<double>? v, ref double min, ref double max)
        {
            if (v == null) return;
            for (int i = 0; i < v.Count; i++)
            {
                double x = v[i];
                if (!ChartKit.Finite(x)) continue;
                if (x > max) max = x;
                if (x < min) min = x;
            }
        }

        /// <summary>
        /// Una serie: con più punti che pixel si usa una coppia min/max per colonna (la forma resta fedele, picchi compresi).
        /// Ogni tratto continuo (senza NaN) diventa una figura; l'area sotto si riempie per tratto.
        /// </summary>
        private void DrawSeries(DrawingContext dc, IReadOnlyList<double> v, Rect plot, Func<double, double> Y, Pen pen, Brush? fill)
        {
            int n = v.Count;
            if (n == 0) return;
            _pts.Clear();
            double denom = Math.Max(1, n - 1);
            int cols = Math.Max(1, (int)plot.Width);

            var line = new StreamGeometry();
            var area = fill != null ? new StreamGeometry() : null;
            using (var lc = line.Open())
            using (var ac = area?.Open())
            {
                void Flush()
                {
                    if (_pts.Count == 0) return;
                    if (_pts.Count == 1) _pts.Add(new Point(_pts[0].X + 1, _pts[0].Y));
                    lc.BeginFigure(_pts[0], false, false);
                    for (int i = 1; i < _pts.Count; i++) lc.LineTo(_pts[i], true, true);
                    if (ac != null)
                    {
                        ac.BeginFigure(new Point(_pts[0].X, plot.Bottom), true, true);
                        for (int i = 0; i < _pts.Count; i++) ac.LineTo(_pts[i], false, false);
                        ac.LineTo(new Point(_pts[^1].X, plot.Bottom), false, false);
                    }
                    _pts.Clear();
                }

                if (n <= cols * 2)
                {
                    for (int i = 0; i < n; i++)
                    {
                        double x = v[i];
                        if (!ChartKit.Finite(x))
                        {
                            Flush();
                            continue;
                        }
                        double px = n == 1 ? plot.Left + plot.Width / 2 : plot.Left + i / denom * plot.Width;
                        _pts.Add(new Point(px, Y(x)));
                    }
                }
                else
                {
                    // Decimazione: per ogni colonna di pixel min e max dei punti che ci cadono.
                    for (int c = 0; c < cols; c++)
                    {
                        int a = (int)((long)c * n / cols), b = (int)((long)(c + 1) * n / cols);
                        double mn = double.MaxValue, mx = double.MinValue;
                        int iMn = -1, iMx = -1;
                        for (int i = a; i < b; i++)
                        {
                            double x = v[i];
                            if (!ChartKit.Finite(x)) continue;
                            if (x < mn) { mn = x; iMn = i; }
                            if (x > mx) { mx = x; iMx = i; }
                        }
                        if (iMn < 0)
                        {
                            Flush();
                            continue;
                        }
                        double px = plot.Left + (c + 0.5) / cols * plot.Width;
                        // L'ordine min/max segue l'ordine temporale, così la linea non "torna indietro".
                        if (iMn <= iMx)
                        {
                            _pts.Add(new Point(px, Y(mn)));
                            if (iMx != iMn) _pts.Add(new Point(px, Y(mx)));
                        }
                        else
                        {
                            _pts.Add(new Point(px, Y(mx)));
                            _pts.Add(new Point(px, Y(mn)));
                        }
                    }
                }
                Flush();
            }
            line.Freeze();
            if (area != null)
            {
                area.Freeze();
                dc.DrawGeometry(fill, null, area);
            }
            dc.DrawGeometry(null, pen, line);
        }
    }

    /// <summary>
    /// Grafico dei frametime recenti: una barra per frame (o per colonna di pixel se i frame sono di più).
    /// Colori: normale = Accent2, picco (≥ 2,5× la mediana) = Warn, ≥ 50 ms = Bad. Scala Y = max(P99 × 1,3; obiettivo × 1,5).
    /// </summary>
    public sealed class FrametimeGraph : FrameworkElement
    {
        public static readonly DependencyProperty FrametimesProperty = DependencyProperty.Register(nameof(Frametimes),
            typeof(float[]), typeof(FrametimeGraph), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TargetMsProperty = DependencyProperty.Register(nameof(TargetMs),
            typeof(double), typeof(FrametimeGraph), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TargetLabelProperty = DependencyProperty.Register(nameof(TargetLabel),
            typeof(string), typeof(FrametimeGraph), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>Frametime in ms, dal più vecchio al più recente.</summary>
        public float[]? Frametimes { get => (float[]?)GetValue(FrametimesProperty); set => SetValue(FrametimesProperty, value); }
        /// <summary>Linea obiettivo (es. 1000 / refresh), NaN = nessuna.</summary>
        public double TargetMs { get => (double)GetValue(TargetMsProperty); set => SetValue(TargetMsProperty, value); }
        public string? TargetLabel { get => (string?)GetValue(TargetLabelProperty); set => SetValue(TargetLabelProperty, value); }

        private const double SpikeFactor = 2.5;
        private const double BadMs = 50;
        private float[] _sorted = Array.Empty<float>();

        public FrametimeGraph()
        {
            SnapsToDevicePixels = true;
            ClipToBounds = true;
            MinHeight = 60;
        }

        protected override Size MeasureOverride(Size availableSize) =>
            new(double.IsInfinity(availableSize.Width) ? 300 : 0, double.IsInfinity(availableSize.Height) ? 140 : 0);

        protected override void OnRender(DrawingContext dc)
        {
            var size = RenderSize;
            if (size.Width < 40 || size.Height < 40) return;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(size));
            var ft = Frametimes;
            int n = ft?.Length ?? 0;
            if (ft == null || n < 2)
            {
                ChartKit.DrawEmpty(dc, this, size, "In attesa di frame…");
                return;
            }

            // Mediana e P99 su una copia riutilizzata (600 valori: costo trascurabile).
            if (_sorted.Length < n) _sorted = new float[Math.Max(n, 64)];
            Array.Copy(ft, _sorted, n);
            Array.Sort(_sorted, 0, n);
            double median = _sorted[n / 2];
            double p99 = _sorted[Math.Min(n - 1, (int)Math.Ceiling(n * 0.99) - 1)];
            double target = TargetMs;
            double top = Math.Max(p99 * 1.3, ChartKit.Finite(target) && target > 0 ? target * 1.5 : 0);
            top = Math.Max(top, median * 1.5);
            if (!(top > 0)) top = 20;
            double step = ChartKit.NiceStep(top / 3);
            top = Math.Ceiling(top / step) * step;

            var muted = ChartKit.Muted;
            double labelW = 0;
            var labels = new List<(double v, FormattedText t)>();
            for (double t = 0; t <= top + step * 0.001; t += step)
            {
                var f = ChartKit.Txt(this, ChartKit.Format(t, step < 1 ? "0.0" : "0") + " ms", 10.5, muted);
                labels.Add((t, f));
                labelW = Math.Max(labelW, f.Width);
            }
            double left = Math.Ceiling(labelW) + 8;
            var plot = new Rect(left, 8, Math.Max(10, size.Width - left - 6), Math.Max(10, size.Height - 14));
            double Y(double v) => plot.Bottom - Math.Min(v, top) / top * plot.Height;

            foreach (var (v, t) in labels)
            {
                double y = Math.Round(Y(v)) + 0.5;
                dc.DrawLine(ChartKit.GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                dc.DrawText(t, new Point(left - 6 - t.Width, y - t.Height / 2));
            }

            // Barre raggruppate per colore: tre geometrie in tutto.
            int cols = Math.Max(1, (int)plot.Width);
            int bars = Math.Min(n, cols);
            double bw = plot.Width / bars;
            double spike = median * SpikeFactor;
            var gNormal = new StreamGeometry();
            var gWarn = new StreamGeometry();
            var gBad = new StreamGeometry();
            using (var cN = gNormal.Open())
            using (var cW = gWarn.Open())
            using (var cB = gBad.Open())
            {
                for (int b = 0; b < bars; b++)
                {
                    int a = (int)((long)b * n / bars), e = (int)((long)(b + 1) * n / bars);
                    float mx = 0;
                    for (int i = a; i < e; i++)
                        if (ft[i] > mx) mx = ft[i];
                    if (!(mx > 0)) continue;
                    var ctx = mx >= BadMs ? cB : mx >= spike ? cW : cN;
                    double x0 = plot.Left + b * bw;
                    double x1 = Math.Max(x0 + 1, plot.Left + (b + 1) * bw - (bw >= 3 ? 1 : 0));
                    double y = Y(mx);
                    ctx.BeginFigure(new Point(x0, plot.Bottom), true, true);
                    ctx.LineTo(new Point(x0, y), false, false);
                    ctx.LineTo(new Point(x1, y), false, false);
                    ctx.LineTo(new Point(x1, plot.Bottom), false, false);
                }
            }
            gNormal.Freeze();
            gWarn.Freeze();
            gBad.Freeze();
            dc.DrawGeometry(ChartKit.WithOpacity(ChartKit.Accent2, 0.75), null, gNormal);
            dc.DrawGeometry(ChartKit.Warn, null, gWarn);
            dc.DrawGeometry(ChartKit.Bad, null, gBad);

            if (ChartKit.Finite(target) && target > 0 && target <= top)
            {
                double y = Math.Round(Y(target)) + 0.5;
                dc.DrawLine(ChartKit.MakePen(ChartKit.WithOpacity(ChartKit.Text, 0.55), 1, dashed: true),
                    new Point(plot.Left, y), new Point(plot.Right, y));
                if (!string.IsNullOrEmpty(TargetLabel))
                {
                    var t = ChartKit.Txt(this, TargetLabel!, 10.5, ChartKit.Text);
                    dc.DrawText(t, new Point(plot.Left + 4, y - t.Height - 1 < plot.Top ? y + 2 : y - t.Height - 1));
                }
            }

            // Ultimo frametime in alto a destra.
            var cur = ChartKit.Txt(this, ChartKit.Format(ft[n - 1], "0.0") + " ms", 12, ChartKit.Text, bold: true);
            var box = new Rect(plot.Right - cur.Width - 10, plot.Top, cur.Width + 8, cur.Height + 2);
            dc.DrawRoundedRectangle(ChartKit.WithOpacity(Brushes.Black, 0.5), null, box, 4, 4);
            dc.DrawText(cur, new Point(box.Left + 4, box.Top + 1));
        }
    }

    /// <summary>
    /// Istogramma dei frametime di una sessione: quanti frame cadono in ogni intervallo di ms.
    /// Marca la media e l'1% low. I conteggi si calcolano una volta sola quando cambiano i dati (O(n), anche con milioni di frame).
    /// </summary>
    public sealed class Histogram : FrameworkElement
    {
        public static readonly DependencyProperty FrametimesProperty = DependencyProperty.Register(nameof(Frametimes),
            typeof(float[]), typeof(Histogram), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));

        public float[]? Frametimes { get => (float[]?)GetValue(FrametimesProperty); set => SetValue(FrametimesProperty, value); }

        // Istogramma fine (0,1 ms per cella fino a 500 ms) da cui si ricavano le colonne visibili.
        private const double FineStep = 0.1;
        private const int FineBins = 5000;
        private int[]? _fine;
        private int _overflow;
        private long _total;
        private double _avgMs;
        private double _low1Ms;

        public Histogram()
        {
            SnapsToDevicePixels = true;
            ClipToBounds = true;
            MinHeight = 60;
        }

        protected override Size MeasureOverride(Size availableSize) =>
            new(double.IsInfinity(availableSize.Width) ? 300 : 0, double.IsInfinity(availableSize.Height) ? 140 : 0);

        private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
            ((Histogram)d).Compute((float[]?)e.NewValue);

        private void Compute(float[]? data)
        {
            _fine = null;
            _overflow = 0;
            _total = 0;
            if (data == null || data.Length < 2) return;
            var fine = new int[FineBins];
            double sum = 0;
            long count = 0;
            int overflow = 0;
            foreach (var f in data)
            {
                if (!(f > 0) || float.IsInfinity(f)) continue;
                sum += f;
                count++;
                int i = (int)(f / FineStep);
                if (i >= FineBins) overflow++;
                else fine[i]++;
            }
            if (count < 2) return;
            _fine = fine;
            _overflow = overflow;
            _total = count;
            _avgMs = sum / count;

            // 1% low = media dell'1% dei frame più lenti (approssimata sulle celle da 0,1 ms; i frame oltre 500 ms contano 500).
            long need = Math.Max(1, count / 100), taken = 0;
            double worstSum = 0;
            if (overflow > 0)
            {
                long t = Math.Min(need, overflow);
                worstSum += t * FineBins * FineStep;
                taken += t;
            }
            for (int i = FineBins - 1; i >= 0 && taken < need; i--)
            {
                if (fine[i] == 0) continue;
                long t = Math.Min(need - taken, fine[i]);
                worstSum += t * (i + 0.5) * FineStep;
                taken += t;
            }
            _low1Ms = taken > 0 ? worstSum / taken : _avgMs;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var size = RenderSize;
            if (size.Width < 60 || size.Height < 50) return;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(size));
            var fine = _fine;
            if (fine == null || _total < 2)
            {
                ChartKit.DrawEmpty(dc, this, size);
                return;
            }

            // Intervallo visibile: dallo 0,5° al 99,5° percentile (con un po' di margine); il resto finisce nelle colonne agli estremi.
            double lo = PercentileMs(fine, 0.005), hi = PercentileMs(fine, 0.995);
            hi = Math.Max(hi * 1.15, _low1Ms * 1.1);
            lo = Math.Max(0, lo * 0.9);
            if (hi - lo < 2) hi = lo + 2;

            double bottomH = 30;
            var plot = new Rect(6, 22, Math.Max(10, size.Width - 12), Math.Max(10, size.Height - 22 - bottomH));
            int cols = Math.Clamp((int)(plot.Width / 7), 10, 80);
            double colMs = (hi - lo) / cols;
            var counts = new long[cols];
            for (int i = 0; i < FineBins; i++)
            {
                if (fine[i] == 0) continue;
                double ms = (i + 0.5) * FineStep;
                int c = (int)((ms - lo) / colMs);
                counts[Math.Clamp(c, 0, cols - 1)] += fine[i];
            }
            counts[cols - 1] += _overflow;
            long maxCount = 1;
            foreach (var c in counts) maxCount = Math.Max(maxCount, c);

            double X(double ms) => plot.Left + Math.Clamp((ms - lo) / (hi - lo), 0, 1) * plot.Width;

            var geo = new StreamGeometry();
            double cw = plot.Width / cols;
            using (var ctx = geo.Open())
            {
                for (int c = 0; c < cols; c++)
                {
                    if (counts[c] == 0) continue;
                    double h = Math.Max(1, (double)counts[c] / maxCount * plot.Height);
                    double x0 = plot.Left + c * cw, x1 = x0 + Math.Max(1, cw - 1.5);
                    ctx.BeginFigure(new Point(x0, plot.Bottom), true, true);
                    ctx.LineTo(new Point(x0, plot.Bottom - h), false, false);
                    ctx.LineTo(new Point(x1, plot.Bottom - h), false, false);
                    ctx.LineTo(new Point(x1, plot.Bottom), false, false);
                }
            }
            geo.Freeze();
            dc.DrawLine(ChartKit.GridPen, new Point(plot.Left, plot.Bottom + 0.5), new Point(plot.Right, plot.Bottom + 0.5));
            dc.DrawGeometry(ChartKit.WithOpacity(ChartKit.Accent2, 0.7), null, geo);

            // Etichette asse X (ms + FPS equivalenti).
            var muted = ChartKit.Muted;
            double step = ChartKit.NiceStep((hi - lo) / 5);
            for (double t = Math.Ceiling(lo / step) * step; t <= hi; t += step)
            {
                double x = Math.Round(X(t)) + 0.5;
                dc.DrawLine(ChartKit.GridPen, new Point(x, plot.Bottom), new Point(x, plot.Bottom + 4));
                var lbl = ChartKit.Txt(this, ChartKit.Format(t, step < 1 ? "0.0" : "0") + " ms", 10.5, muted);
                double lx = Math.Clamp(x - lbl.Width / 2, 0, size.Width - lbl.Width);
                dc.DrawText(lbl, new Point(lx, plot.Bottom + 5));
            }

            // Marcatori: media e 1% low (espressi in FPS, come nel resto dell'app).
            Marker(dc, plot, X(_avgMs), ChartKit.Ok, $"media {ChartKit.Format(1000 / _avgMs, "0")} FPS", alignRight: false);
            Marker(dc, plot, X(_low1Ms), ChartKit.Warn, $"1% low {ChartKit.Format(1000 / _low1Ms, "0")} FPS", alignRight: true);
        }

        private void Marker(DrawingContext dc, Rect plot, double x, Brush brush, string text, bool alignRight)
        {
            x = Math.Round(x) + 0.5;
            dc.DrawLine(ChartKit.MakePen(brush, 1.5, dashed: true), new Point(x, plot.Top - 4), new Point(x, plot.Bottom));
            var ft = ChartKit.Txt(this, text, 11, brush, bold: true);
            double tx = alignRight ? x + 4 : x - ft.Width - 4;
            if (tx < 0) tx = x + 4;
            if (tx + ft.Width > RenderSize.Width) tx = x - ft.Width - 4;
            dc.DrawText(ft, new Point(Math.Max(0, tx), 2));
        }

        private double PercentileMs(int[] fine, double p)
        {
            long target = (long)Math.Ceiling(_total * p), acc = 0;
            for (int i = 0; i < fine.Length; i++)
            {
                acc += fine[i];
                if (acc >= target) return (i + 0.5) * FineStep;
            }
            return FineBins * FineStep;
        }
    }
}
