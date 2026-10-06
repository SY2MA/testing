using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace FNBoost.Crosshair
{
    /// <summary>
    /// Disegna il mirino in unità = pixel fisici. Senza rotazione le linee sono allineate ai pixel
    /// (EdgeMode.Aliased) per essere nitide come un mirino di gioco; con rotazione si usa l'antialiasing.
    ///
    /// Ordine dei livelli: prima TUTTI i bordi (esterne, interne, cerchio, punto), poi tutti i riempimenti
    /// nello stesso ordine, così nessun bordo finisce sopra a un colore.
    /// Ogni livello ha la sua opacità (raggruppata con PushOpacity: le sovrapposizioni non si scuriscono),
    /// moltiplicata dall'opacità globale.
    ///
    /// Bagliore: NON viene disegnato qui ma con un DropShadowEffect (ShadowDepth 0) applicato all'elemento
    /// che contiene il mirino (vedi <see cref="UpdateGlow"/>). È un effetto pixel-shader calcolato dalla GPU
    /// solo quando il disegno cambia: costa molto meno di un alone disegnato a cerchi concentrici e segue
    /// la forma esatta del mirino (anche dell'immagine PNG).
    /// </summary>
    public static class CrosshairRenderer
    {
        private static string? _imgPath;
        private static BitmapSource? _img;
        private static bool _imgFailed;
        private static readonly Dictionary<string, Color?> ColorCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        /// <summary>Secondi dall'avvio: base comune per l'animazione RGB (overlay e anteprima restano in sincrono).</summary>
        public static double Seconds => Clock.Elapsed.TotalSeconds;

        public static Color ParseColor(string hex, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            if (ColorCache.TryGetValue(hex, out var cached)) return cached ?? fallback;
            Color? c;
            try
            {
                c = (Color)ColorConverter.ConvertFromString(hex.Trim());
            }
            catch
            {
                c = null; // memorizzato anche l'errore: niente eccezioni a ogni fotogramma mentre si scrive
            }
            if (ColorCache.Count > 256) ColorCache.Clear();
            ColorCache[hex] = c;
            return c ?? fallback;
        }

        /// <summary>Colore principale effettivo (quello animato se il ciclo RGB è attivo).</summary>
        public static Color MainColor(CrosshairSettings s, Color? mainOverride) =>
            mainOverride ?? ParseColor(s.Color, Colors.Lime);

        /// <summary>Colore dell'arcobaleno al tempo indicato: RgbSpeed = giri completi ogni 10 secondi.</summary>
        public static Color RgbAt(double seconds, double speed)
        {
            double hue = (seconds * speed / 10.0 % 1.0) * 360.0;
            return FromHsv(hue, 1, 1);
        }

        public static Color FromHsv(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s;
            double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
            double m = v - c;
            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        private static BitmapSource? LoadImage(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (_imgPath == path) return _imgFailed ? null : _img;
            _imgPath = path;
            _img = null;
            _imgFailed = true;
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
                _img = bmp;
                _imgFailed = false;
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Dimenticata l'immagine in cache (es. dopo aver scelto di nuovo lo stesso file modificato).</summary>
        public static void ResetImageCache()
        {
            _imgPath = null;
            _img = null;
            _imgFailed = false;
        }

        /// <summary>true se l'immagine personalizzata è attiva e il file si carica.</summary>
        public static bool ImageActive(CrosshairSettings s) => s.UseImage && LoadImage(s.ImagePath) != null;

        /// <summary>
        /// Raggio (in pixel) necessario a contenere il mirino: tiene conto di tutti i livelli, del bordo,
        /// del bagliore e della rotazione (in quel caso si usa il raggio dell'angolo più lontano).
        /// </summary>
        public static int Extent(CrosshairSettings s)
        {
            int o = s.Outline ? s.OutlineThickness : 0;
            bool rotated = s.Rotation != 0;
            double r = s.DotSize;
            var img = s.UseImage ? LoadImage(s.ImagePath) : null;
            if (img != null)
            {
                double w = img.PixelWidth * s.ImageScale, h = img.PixelHeight * s.ImageScale;
                r = Math.Max(r, rotated ? Math.Sqrt(w * w + h * h) / 2 : Math.Max(w, h) / 2);
            }
            else
            {
                if (s.InnerShow) r = Math.Max(r, ArmReach(s.Gap, s.Length, s.Thickness, rotated));
                if (s.OuterShow) r = Math.Max(r, ArmReach(s.OuterGap, s.OuterLength, s.OuterThickness, rotated));
                if (s.CircleShow) r = Math.Max(r, s.CircleRadius + s.CircleThickness);
            }
            double glow = s.Glow ? GlowBlurPx(s) : 0;
            return (int)Math.Ceiling(r + o + glow) + 4;
        }

        private static double ArmReach(int gap, int len, int t, bool rotated)
        {
            double along = gap + len + 1;
            double across = t / 2.0 + 1;
            return rotated ? Math.Sqrt(along * along + across * across) : Math.Max(along, across);
        }

        /// <summary>Raggio di sfocatura del bagliore in pixel fisici.</summary>
        public static double GlowBlurPx(CrosshairSettings s) => s.GlowRadius * 2.0;

        /// <summary>
        /// Crea/aggiorna l'effetto bagliore da assegnare a Visual.Effect (null = nessun bagliore).
        /// <paramref name="pxToUnits"/> converte i pixel del mirino nelle unità dell'elemento (1/DPI nell'overlay,
        /// lo zoom nell'anteprima). Riusa l'istanza esistente per non creare oggetti a ogni fotogramma RGB.
        /// </summary>
        public static Effect? UpdateGlow(Effect? current, CrosshairSettings? s, Color? mainOverride, double pxToUnits)
        {
            if (s == null || !s.Glow) return null;
            var fx = current as DropShadowEffect ?? new DropShadowEffect
            {
                ShadowDepth = 0,
                Opacity = 1,
                RenderingBias = RenderingBias.Performance
            };
            var main = MainColor(s, mainOverride);
            var c = string.IsNullOrWhiteSpace(s.GlowColor) ? main : ParseColor(s.GlowColor, main);
            if (fx.Color != c) fx.Color = c;
            double blur = Math.Min(GlowBlurPx(s) * pxToUnits, 200);
            if (Math.Abs(fx.BlurRadius - blur) > 0.01) fx.BlurRadius = blur;
            return fx;
        }

        /// <param name="cx">Colonna del pixel centrale.</param>
        /// <param name="cy">Riga del pixel centrale.</param>
        public static void Draw(DrawingContext dc, CrosshairSettings s, int cx, int cy) => Draw(dc, s, cx, cy, null);

        /// <param name="mainOverride">Colore principale a runtime (ciclo RGB); null = quello delle impostazioni.</param>
        public static void Draw(DrawingContext dc, CrosshairSettings s, int cx, int cy, Color? mainOverride)
        {
            var main = MainColor(s, mainOverride);
            var fill = Freeze(new SolidColorBrush(main));
            var dotFill = string.IsNullOrWhiteSpace(s.DotColor) ? fill : Freeze(new SolidColorBrush(ParseColor(s.DotColor, main)));
            var circleFill = string.IsNullOrWhiteSpace(s.CircleColor) ? fill : Freeze(new SolidColorBrush(ParseColor(s.CircleColor, main)));
            var stroke = Freeze(new SolidColorBrush(ParseColor(s.OutlineColor, Colors.Black)));
            int o = s.Outline && s.OutlineOpacity > 0 ? s.OutlineThickness : 0;

            var img = s.UseImage ? LoadImage(s.ImagePath) : null;
            bool imageMode = img != null;
            bool anyArm = s.ArmTop || s.ArmBottom || s.ArmLeft || s.ArmRight;
            bool inner = !imageMode && s.InnerShow && anyArm && s.InnerOpacity > 0;
            bool outer = !imageMode && s.OuterShow && anyArm && s.OuterOpacity > 0;
            bool circle = !imageMode && s.CircleShow && s.CircleOpacity > 0;
            bool dot = s.DotShow && s.DotOpacity > 0;
            double dotOpacity = s.DotOpacity;
            // Immagine scelta ma non caricabile e nessun altro livello: almeno un punto, come nella v1.
            if (s.UseImage && !imageMode && !inner && !outer && !circle && !dot)
            {
                dot = true;
                dotOpacity = 1;
            }

            bool rotated = s.Rotation != 0;
            double mid = s.Thickness % 2 == 1 ? 0.5 : 0;
            var pivot = new Point(cx + mid, cy + mid); // centro geometrico esatto delle linee interne
            var innerRects = inner ? ArmRects(s, cx, cy, s.Thickness, s.Length, s.Gap) : null;
            var outerRects = outer ? ArmRects(s, cx, cy, s.OuterThickness, s.OuterLength, s.OuterGap) : null;

            dc.PushOpacity(s.Opacity);

            // ---- 1) Bordi, sotto a tutto ----
            if (o > 0)
            {
                if (outerRects != null) Lines(dc, outerRects, stroke, o, s.OuterOpacity * s.OutlineOpacity, rotated, s.Rotation, pivot);
                if (innerRects != null) Lines(dc, innerRects, stroke, o, s.InnerOpacity * s.OutlineOpacity, rotated, s.Rotation, pivot);
                if (circle) Ring(dc, s, pivot, stroke, s.CircleThickness + 2 * o, s.CircleOpacity * s.OutlineOpacity);
                if (dot) Dot(dc, s, cx, cy, stroke, o, dotOpacity * s.OutlineOpacity);
            }

            // ---- 2) Riempimenti ----
            if (img != null) DrawImage(dc, s, img, cx, cy);
            if (outerRects != null) Lines(dc, outerRects, fill, 0, s.OuterOpacity, rotated, s.Rotation, pivot);
            if (innerRects != null) Lines(dc, innerRects, fill, 0, s.InnerOpacity, rotated, s.Rotation, pivot);
            if (circle) Ring(dc, s, pivot, circleFill, s.CircleThickness, s.CircleOpacity);
            if (dot) Dot(dc, s, cx, cy, dotFill, 0, dotOpacity);

            dc.Pop();
        }

        private static SolidColorBrush Freeze(SolidColorBrush b)
        {
            b.Freeze();
            return b;
        }

        private static bool PushOpacity(DrawingContext dc, double opacity)
        {
            if (opacity >= 0.999) return false;
            dc.PushOpacity(Math.Max(0, opacity));
            return true;
        }

        /// <summary>Rettangoli dei bracci attivi. Con spessore dispari il pixel centrale fa da "nucleo".</summary>
        private static List<Rect> ArmRects(CrosshairSettings s, int cx, int cy, int t, int len, int gap)
        {
            int lineStartX = cx - t / 2;    // colonna iniziale delle linee verticali
            int lineStartY = cy - t / 2;    // riga iniziale delle linee orizzontali
            int core = t % 2 == 1 ? 1 : 0;
            var r = new List<Rect>(4);
            if (s.ArmRight) r.Add(new Rect(cx + core + gap, lineStartY, len, t));
            if (s.ArmLeft) r.Add(new Rect(cx - gap - len, lineStartY, len, t));
            if (s.ArmBottom) r.Add(new Rect(lineStartX, cy + core + gap, t, len));
            if (s.ArmTop) r.Add(new Rect(lineStartX, cy - gap - len, t, len));
            return r;
        }

        private static void Lines(DrawingContext dc, List<Rect> rects, Brush brush, int inflate, double opacity,
            bool rotated, int angle, Point pivot)
        {
            if (rects.Count == 0 || opacity <= 0) return;
            bool pushed = PushOpacity(dc, opacity);
            if (rotated) dc.PushTransform(new RotateTransform(angle, pivot.X, pivot.Y));

            var group = new DrawingGroup();
            if (!rotated) RenderOptions.SetEdgeMode(group, EdgeMode.Aliased); // 0° = pixel perfetti
            using (var ctx = group.Open())
            {
                foreach (var rc in rects)
                {
                    var r = rc;
                    if (inflate > 0) r.Inflate(inflate, inflate);
                    ctx.DrawRectangle(brush, null, r);
                }
            }
            dc.DrawDrawing(group);

            if (rotated) dc.Pop();
            if (pushed) dc.Pop();
        }

        private static void Ring(DrawingContext dc, CrosshairSettings s, Point center, Brush brush, double thickness, double opacity)
        {
            if (opacity <= 0) return;
            bool pushed = PushOpacity(dc, opacity);
            var pen = new Pen(brush, thickness);
            pen.Freeze();
            dc.DrawEllipse(null, pen, center, s.CircleRadius, s.CircleRadius);
            if (pushed) dc.Pop();
        }

        private static void Dot(DrawingContext dc, CrosshairSettings s, int cx, int cy, Brush brush, int inflate, double opacity)
        {
            if (opacity <= 0) return;
            bool pushed = PushOpacity(dc, opacity);
            int d = s.DotSize;
            var rc = new Rect(cx - d / 2, cy - d / 2, d, d);
            if (s.DotRound && d > 2)
            {
                var c = new Point(rc.X + d / 2.0, rc.Y + d / 2.0);
                dc.DrawEllipse(brush, null, c, d / 2.0 + inflate, d / 2.0 + inflate);
            }
            else
            {
                if (inflate > 0) rc.Inflate(inflate, inflate);
                var group = new DrawingGroup();
                RenderOptions.SetEdgeMode(group, EdgeMode.Aliased);
                using (var ctx = group.Open()) ctx.DrawRectangle(brush, null, rc);
                dc.DrawDrawing(group);
            }
            if (pushed) dc.Pop();
        }

        private static void DrawImage(DrawingContext dc, CrosshairSettings s, BitmapSource img, int cx, int cy)
        {
            double w = Math.Round(img.PixelWidth * s.ImageScale);
            double h = Math.Round(img.PixelHeight * s.ImageScale);
            var rect = new Rect(Math.Round(cx + 0.5 - w / 2), Math.Round(cy + 0.5 - h / 2), w, h);
            bool rotated = s.Rotation != 0;
            var group = new DrawingGroup();
            RenderOptions.SetBitmapScalingMode(group, rotated ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor);
            using (var ctx = group.Open()) ctx.DrawImage(img, rect);
            if (rotated) dc.PushTransform(new RotateTransform(s.Rotation, rect.X + w / 2, rect.Y + h / 2));
            dc.DrawDrawing(group);
            if (rotated) dc.Pop();
        }
    }
}
