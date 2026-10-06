using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FNBoost.Crosshair
{
    /// <summary>
    /// Disegna il mirino in unità = pixel fisici. Le linee sono allineate ai pixel
    /// (EdgeMode.Aliased) per essere nitide come un mirino di gioco.
    /// </summary>
    public static class CrosshairRenderer
    {
        private static string? _imgPath;
        private static BitmapSource? _img;

        public static Color ParseColor(string hex, Color fallback)
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(hex);
            }
            catch
            {
                return fallback;
            }
        }

        private static BitmapSource? LoadImage(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            if (_imgPath == path && _img != null) return _img;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                _imgPath = path;
                _img = bmp;
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Raggio (in pixel) necessario a contenere il mirino.</summary>
        public static int Extent(CrosshairSettings s)
        {
            int o = s.Outline ? s.OutlineThickness : 0;
            int e = 4;
            switch (s.Shape)
            {
                case CrosshairShape.Cross:
                case CrosshairShape.CrossDot:
                case CrosshairShape.TShape:
                    e = s.Gap + s.Length + s.Thickness + o;
                    break;
                case CrosshairShape.XShape:
                    e = (int)Math.Ceiling((s.Gap + s.Length + s.Thickness) * 1.42) + o;
                    break;
                case CrosshairShape.Dot:
                    e = s.DotSize + o;
                    break;
                case CrosshairShape.Circle:
                case CrosshairShape.CircleDot:
                    e = s.CircleRadius + s.Thickness + o;
                    break;
                case CrosshairShape.Image:
                    var img = LoadImage(s.ImagePath);
                    if (img != null) e = (int)Math.Ceiling(Math.Max(img.PixelWidth, img.PixelHeight) * s.ImageScale / 2);
                    break;
            }
            return Math.Max(e, s.DotSize + o) + 4;
        }

        /// <param name="cx">Colonna del pixel centrale.</param>
        /// <param name="cy">Riga del pixel centrale.</param>
        public static void Draw(DrawingContext dc, CrosshairSettings s, int cx, int cy)
        {
            var color = ParseColor(s.Color, Colors.Lime);
            var outlineColor = ParseColor(s.OutlineColor, Colors.Black);
            var fill = new SolidColorBrush(color);
            var stroke = new SolidColorBrush(outlineColor);
            fill.Freeze();
            stroke.Freeze();

            dc.PushOpacity(s.Opacity);
            int o = s.Outline ? s.OutlineThickness : 0;

            switch (s.Shape)
            {
                case CrosshairShape.Cross:
                    DrawRects(dc, CrossRects(s, cx, cy, top: true), fill, stroke, o);
                    break;
                case CrosshairShape.CrossDot:
                    var rects = CrossRects(s, cx, cy, top: true);
                    rects.Add(DotRect(s, cx, cy));
                    DrawRects(dc, rects, fill, stroke, o);
                    break;
                case CrosshairShape.TShape:
                    DrawRects(dc, CrossRects(s, cx, cy, top: false), fill, stroke, o);
                    break;
                case CrosshairShape.Dot:
                    DrawRects(dc, new List<Rect> { DotRect(s, cx, cy) }, fill, stroke, o);
                    break;
                case CrosshairShape.XShape:
                {
                    double mid = s.Thickness % 2 == 1 ? 0.5 : 0;
                    dc.PushTransform(new RotateTransform(45, cx + mid, cy + mid));
                    DrawRects(dc, CrossRects(s, cx, cy, top: true), fill, stroke, o, aliased: false);
                    dc.Pop();
                    break;
                }
                case CrosshairShape.Circle:
                case CrosshairShape.CircleDot:
                {
                    double mid = s.Thickness % 2 == 1 ? 0.5 : 0;
                    var center = new Point(cx + mid, cy + mid);
                    if (o > 0)
                    {
                        var penO = new Pen(stroke, s.Thickness + 2 * o);
                        penO.Freeze();
                        dc.DrawEllipse(null, penO, center, s.CircleRadius, s.CircleRadius);
                    }
                    var pen = new Pen(fill, s.Thickness);
                    pen.Freeze();
                    dc.DrawEllipse(null, pen, center, s.CircleRadius, s.CircleRadius);
                    if (s.Shape == CrosshairShape.CircleDot)
                        DrawRects(dc, new List<Rect> { DotRect(s, cx, cy) }, fill, stroke, o);
                    break;
                }
                case CrosshairShape.Image:
                {
                    var img = LoadImage(s.ImagePath);
                    if (img == null)
                    {
                        DrawRects(dc, new List<Rect> { DotRect(s, cx, cy) }, fill, stroke, o);
                        break;
                    }
                    double w = Math.Round(img.PixelWidth * s.ImageScale);
                    double h = Math.Round(img.PixelHeight * s.ImageScale);
                    var group = new DrawingGroup();
                    RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.NearestNeighbor);
                    using (var ctx = group.Open())
                        ctx.DrawImage(img, new Rect(Math.Round(cx + 0.5 - w / 2), Math.Round(cy + 0.5 - h / 2), w, h));
                    dc.DrawDrawing(group);
                    break;
                }
            }
            dc.Pop();
        }

        private static List<Rect> CrossRects(CrosshairSettings s, int cx, int cy, bool top)
        {
            int t = s.Thickness;
            bool odd = t % 2 == 1;
            int lineStartX = cx - t / 2;    // colonna iniziale delle linee verticali
            int lineStartY = cy - t / 2;    // riga iniziale delle linee orizzontali
            int core = odd ? 1 : 0;
            int len = s.Length;
            int gap = s.Gap;

            var r = new List<Rect>
            {
                new(cx + core + gap, lineStartY, len, t),          // destra
                new(cx - gap - len, lineStartY, len, t),           // sinistra
                new(lineStartX, cy + core + gap, t, len),          // basso
            };
            if (top) r.Add(new Rect(lineStartX, cy - gap - len, t, len)); // alto
            return r;
        }

        private static Rect DotRect(CrosshairSettings s, int cx, int cy)
        {
            int d = s.DotSize;
            return new Rect(cx - d / 2, cy - d / 2, d, d);
        }

        private static void DrawRects(DrawingContext dc, List<Rect> rects, Brush fill, Brush outline, int o, bool aliased = true)
        {
            var group = new DrawingGroup();
            if (aliased) RenderOptions.SetEdgeMode(group, EdgeMode.Aliased);
            using (var ctx = group.Open())
            {
                if (o > 0)
                {
                    foreach (var rc in rects)
                    {
                        var big = rc;
                        big.Inflate(o, o);
                        ctx.DrawRectangle(outline, null, big);
                    }
                }
                foreach (var rc in rects) ctx.DrawRectangle(fill, null, rc);
            }
            dc.DrawDrawing(group);
        }
    }
}
