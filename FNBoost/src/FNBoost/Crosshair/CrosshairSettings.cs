using System.Collections.Generic;
using System.Text.Json.Serialization;
using FNBoost.Core;

namespace FNBoost.Crosshair
{
    public enum CrosshairShape { Cross, CrossDot, Dot, Circle, CircleDot, TShape, XShape, Image }

    /// <summary>Impostazioni del mirino. Notifica ogni modifica così l'overlay si aggiorna dal vivo.</summary>
    public sealed class CrosshairSettings : ObservableObject
    {
        private bool _enabled;
        private CrosshairShape _shape = CrosshairShape.CrossDot;
        private string _color = "#00FF66";
        private double _opacity = 1.0;
        private int _length = 7;
        private int _thickness = 2;
        private int _gap = 4;
        private int _dotSize = 2;
        private int _circleRadius = 12;
        private bool _outline = true;
        private int _outlineThickness = 1;
        private string _outlineColor = "#000000";
        private int _offsetX;
        private int _offsetY;
        private string _monitor = "";
        private string _imagePath = "";
        private double _imageScale = 1.0;
        private bool _onlyWhenFortniteFocused;

        [JsonIgnore]
        public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

        public CrosshairShape Shape { get => _shape; set => Set(ref _shape, value); }
        public string Color { get => _color; set => Set(ref _color, value); }
        public double Opacity { get => _opacity; set => Set(ref _opacity, System.Math.Clamp(value, 0.1, 1.0)); }
        public int Length { get => _length; set => Set(ref _length, System.Math.Clamp(value, 1, 80)); }
        public int Thickness { get => _thickness; set => Set(ref _thickness, System.Math.Clamp(value, 1, 12)); }
        public int Gap { get => _gap; set => Set(ref _gap, System.Math.Clamp(value, 0, 60)); }
        public int DotSize { get => _dotSize; set => Set(ref _dotSize, System.Math.Clamp(value, 1, 16)); }
        public int CircleRadius { get => _circleRadius; set => Set(ref _circleRadius, System.Math.Clamp(value, 2, 120)); }
        public bool Outline { get => _outline; set => Set(ref _outline, value); }
        public int OutlineThickness { get => _outlineThickness; set => Set(ref _outlineThickness, System.Math.Clamp(value, 1, 4)); }
        public string OutlineColor { get => _outlineColor; set => Set(ref _outlineColor, value); }
        public int OffsetX { get => _offsetX; set => Set(ref _offsetX, System.Math.Clamp(value, -200, 200)); }
        public int OffsetY { get => _offsetY; set => Set(ref _offsetY, System.Math.Clamp(value, -200, 200)); }
        /// <summary>Nome dispositivo del monitor (es. \\.\DISPLAY1). Vuoto = principale.</summary>
        public string Monitor { get => _monitor; set => Set(ref _monitor, value ?? ""); }
        public string ImagePath { get => _imagePath; set => Set(ref _imagePath, value ?? ""); }
        public double ImageScale { get => _imageScale; set => Set(ref _imageScale, System.Math.Clamp(value, 0.25, 4.0)); }
        public bool OnlyWhenFortniteFocused { get => _onlyWhenFortniteFocused; set => Set(ref _onlyWhenFortniteFocused, value); }

        public CrosshairSettings Clone()
        {
            var c = new CrosshairSettings();
            c.CopyFrom(this);
            return c;
        }

        /// <summary>Copia l'aspetto (non stato, monitor e opzioni di visualizzazione).</summary>
        public void CopyFrom(CrosshairSettings o)
        {
            Shape = o.Shape;
            Color = o.Color;
            Opacity = o.Opacity;
            Length = o.Length;
            Thickness = o.Thickness;
            Gap = o.Gap;
            DotSize = o.DotSize;
            CircleRadius = o.CircleRadius;
            Outline = o.Outline;
            OutlineThickness = o.OutlineThickness;
            OutlineColor = o.OutlineColor;
            ImagePath = o.ImagePath;
            ImageScale = o.ImageScale;
        }
    }

    public sealed class CrosshairPreset
    {
        public string Name { get; set; } = "";
        public CrosshairSettings Settings { get; set; } = new();
        public override string ToString() => Name;

        private static CrosshairPreset P(string name, CrosshairShape shape, string color, int len, int thick, int gap,
            int dot = 2, int radius = 12, bool outline = true) =>
            new()
            {
                Name = name,
                Settings = new CrosshairSettings
                {
                    Shape = shape, Color = color, Length = len, Thickness = thick, Gap = gap,
                    DotSize = dot, CircleRadius = radius, Outline = outline
                }
            };

        public static List<CrosshairPreset> BuiltIn() => new()
        {
            P("Croce + punto verde", CrosshairShape.CrossDot, "#00FF66", 7, 2, 4),
            P("Croce classica ciano", CrosshairShape.Cross, "#00E5FF", 8, 2, 5),
            P("Punto rosso", CrosshairShape.Dot, "#FF2D55", 0, 1, 0, dot: 4),
            P("Croce gialla sottile", CrosshairShape.Cross, "#FFFF00", 5, 1, 3),
            P("Cerchio + punto", CrosshairShape.CircleDot, "#FFFFFF", 0, 2, 0, dot: 2, radius: 14),
            P("T bianca", CrosshairShape.TShape, "#FFFFFF", 7, 2, 4),
            P("X magenta", CrosshairShape.XShape, "#FF4DFF", 7, 2, 4),
        };
    }
}
