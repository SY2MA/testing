using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using FNBoost.Core;

// Questo file non dipende da WPF (solo System.* + ObservableObject): viene compilato anche dai test.

namespace FNBoost.Crosshair
{
    /// <summary>
    /// Modello "rapido" del mirino. Dalla v2 il disegno è descritto dai livelli (linee interne/esterne,
    /// punto, cerchio, immagine): Shape resta come modello scelto nella pagina e serve a convertire
    /// le impostazioni salvate dalla v1 (vedi <see cref="CrosshairSettings.NormalizeLegacy"/>).
    /// Nuovi valori vanno aggiunti SOLO in fondo.
    /// </summary>
    public enum CrosshairShape { Cross, CrossDot, Dot, Circle, CircleDot, TShape, XShape, Image }

    /// <summary>
    /// Impostazioni del mirino. Notifica ogni modifica così l'overlay si aggiorna dal vivo.
    /// Tutti i setter numerici limitano il valore al proprio intervallo (anche quando arriva da JSON o da un codice).
    /// </summary>
    public sealed class CrosshairSettings : ObservableObject, IJsonOnDeserializing
    {
        /// <summary>Versione corrente dello schema dei livelli.</summary>
        public const int CurrentSchema = 2;

        /// <summary>Proprietà che NON fanno parte dell'aspetto (non copiate da CopyFrom né esportate nei codici).</summary>
        public static readonly IReadOnlyCollection<string> NonAppearanceProperties = new[]
        {
            nameof(Enabled), nameof(Monitor), nameof(OffsetX), nameof(OffsetY), nameof(ImagePath),
            nameof(OnlyWhenFortniteFocused), nameof(HideWhenCursorVisible), nameof(HideWhileAiming),
            nameof(AimToggleMode), nameof(ShowPresetName)
        };

        // ---- Stato e schema ----
        private bool _enabled;
        private int _schemaVersion = CurrentSchema; // nuove installazioni: già v2

        // ---- Generali ----
        private CrosshairShape _shape = CrosshairShape.CrossDot;
        private string _color = "#00FF66";
        private double _opacity = 1.0;
        private int _rotation;
        private bool _rgbCycle;
        private double _rgbSpeed = 1.0;

        // ---- Linee interne ----
        private bool _innerShow = true;
        private int _length = 7;
        private int _thickness = 2;
        private int _gap = 4;
        private double _innerOpacity = 1.0;

        // ---- Linee esterne ----
        private bool _outerShow;
        private int _outerLength = 4;
        private int _outerThickness = 2;
        private int _outerGap = 14;
        private double _outerOpacity = 0.6;

        // ---- Bracci ----
        private bool _armTop = true;
        private bool _armBottom = true;
        private bool _armLeft = true;
        private bool _armRight = true;

        // ---- Punto ----
        private bool _dotShow = true;
        private int _dotSize = 2;
        private bool _dotRound;
        private string _dotColor = "";
        private double _dotOpacity = 1.0;

        // ---- Cerchio ----
        private bool _circleShow;
        private int _circleRadius = 12;
        private int _circleThickness = 2;
        private string _circleColor = "";
        private double _circleOpacity = 1.0;

        // ---- Bordo ----
        private bool _outline = true;
        private int _outlineThickness = 1;
        private string _outlineColor = "#000000";
        private double _outlineOpacity = 1.0;

        // ---- Bagliore ----
        private bool _glow;
        private string _glowColor = "";
        private int _glowRadius = 6;

        // ---- Immagine ----
        private bool _useImage;
        private string _imagePath = "";
        private double _imageScale = 1.0;

        // ---- Posizione e visibilità (non fanno parte dell'aspetto) ----
        private int _offsetX;
        private int _offsetY;
        private string _monitor = "";
        private bool _onlyWhenFortniteFocused;
        private bool _hideWhenCursorVisible;
        private bool _hideWhileAiming;
        private bool _aimToggleMode;
        private bool _showPresetName = true;

        [JsonIgnore]
        public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

        /// <summary>0 = impostazioni salvate dalla v1 (campo assente nel JSON), 2 = modello a livelli.</summary>
        public int SchemaVersion { get => _schemaVersion; set => Set(ref _schemaVersion, Math.Max(0, value)); }

        /// <summary>Ultimo modello scelto (vedi <see cref="ApplyTemplate"/>). Da solo non cambia i livelli.</summary>
        public CrosshairShape Shape { get => _shape; set => Set(ref _shape, value); }
        public string Color { get => _color; set => Set(ref _color, value ?? ""); }
        /// <summary>Opacità globale: moltiplica quella di ogni livello.</summary>
        public double Opacity { get => _opacity; set => Set(ref _opacity, ClampD(value, 0.1, 1.0)); }
        /// <summary>Rotazione in gradi (0-359) di linee e immagine attorno al centro esatto.</summary>
        public int Rotation { get => _rotation; set => Set(ref _rotation, ((value % 360) + 360) % 360); }
        /// <summary>Colore cangiante: il colore animato è solo a runtime, non viene mai salvato.</summary>
        public bool RgbCycle { get => _rgbCycle; set => Set(ref _rgbCycle, value); }
        /// <summary>Giri completi dell'arcobaleno ogni 10 secondi.</summary>
        public double RgbSpeed { get => _rgbSpeed; set => Set(ref _rgbSpeed, ClampD(value, 0.1, 5.0)); }

        public bool InnerShow { get => _innerShow; set => Set(ref _innerShow, value); }
        public int Length { get => _length; set => Set(ref _length, Math.Clamp(value, 1, 80)); }
        public int Thickness { get => _thickness; set => Set(ref _thickness, Math.Clamp(value, 1, 12)); }
        public int Gap { get => _gap; set => Set(ref _gap, Math.Clamp(value, 0, 60)); }
        public double InnerOpacity { get => _innerOpacity; set => Set(ref _innerOpacity, ClampD(value, 0.0, 1.0)); }

        public bool OuterShow { get => _outerShow; set => Set(ref _outerShow, value); }
        public int OuterLength { get => _outerLength; set => Set(ref _outerLength, Math.Clamp(value, 1, 80)); }
        public int OuterThickness { get => _outerThickness; set => Set(ref _outerThickness, Math.Clamp(value, 1, 12)); }
        /// <summary>Distanza dal centro dell'inizio delle linee esterne.</summary>
        public int OuterGap { get => _outerGap; set => Set(ref _outerGap, Math.Clamp(value, 0, 120)); }
        public double OuterOpacity { get => _outerOpacity; set => Set(ref _outerOpacity, ClampD(value, 0.0, 1.0)); }

        public bool ArmTop { get => _armTop; set => Set(ref _armTop, value); }
        public bool ArmBottom { get => _armBottom; set => Set(ref _armBottom, value); }
        public bool ArmLeft { get => _armLeft; set => Set(ref _armLeft, value); }
        public bool ArmRight { get => _armRight; set => Set(ref _armRight, value); }

        public bool DotShow { get => _dotShow; set => Set(ref _dotShow, value); }
        public int DotSize { get => _dotSize; set => Set(ref _dotSize, Math.Clamp(value, 1, 16)); }
        public bool DotRound { get => _dotRound; set => Set(ref _dotRound, value); }
        /// <summary>Vuoto = colore principale.</summary>
        public string DotColor { get => _dotColor; set => Set(ref _dotColor, value ?? ""); }
        public double DotOpacity { get => _dotOpacity; set => Set(ref _dotOpacity, ClampD(value, 0.0, 1.0)); }

        public bool CircleShow { get => _circleShow; set => Set(ref _circleShow, value); }
        public int CircleRadius { get => _circleRadius; set => Set(ref _circleRadius, Math.Clamp(value, 2, 120)); }
        public int CircleThickness { get => _circleThickness; set => Set(ref _circleThickness, Math.Clamp(value, 1, 12)); }
        /// <summary>Vuoto = colore principale.</summary>
        public string CircleColor { get => _circleColor; set => Set(ref _circleColor, value ?? ""); }
        public double CircleOpacity { get => _circleOpacity; set => Set(ref _circleOpacity, ClampD(value, 0.0, 1.0)); }

        public bool Outline { get => _outline; set => Set(ref _outline, value); }
        public int OutlineThickness { get => _outlineThickness; set => Set(ref _outlineThickness, Math.Clamp(value, 1, 4)); }
        public string OutlineColor { get => _outlineColor; set => Set(ref _outlineColor, value ?? ""); }
        public double OutlineOpacity { get => _outlineOpacity; set => Set(ref _outlineOpacity, ClampD(value, 0.0, 1.0)); }

        public bool Glow { get => _glow; set => Set(ref _glow, value); }
        /// <summary>Vuoto = colore principale.</summary>
        public string GlowColor { get => _glowColor; set => Set(ref _glowColor, value ?? ""); }
        public int GlowRadius { get => _glowRadius; set => Set(ref _glowRadius, Math.Clamp(value, 1, 20)); }

        /// <summary>Se true e il file si carica, al posto di linee/cerchio si disegna l'immagine (più il punto se attivo).</summary>
        public bool UseImage { get => _useImage; set => Set(ref _useImage, value); }
        public string ImagePath { get => _imagePath; set => Set(ref _imagePath, value ?? ""); }
        public double ImageScale { get => _imageScale; set => Set(ref _imageScale, ClampD(value, 0.25, 4.0)); }

        public int OffsetX { get => _offsetX; set => Set(ref _offsetX, Math.Clamp(value, -200, 200)); }
        public int OffsetY { get => _offsetY; set => Set(ref _offsetY, Math.Clamp(value, -200, 200)); }
        /// <summary>Nome dispositivo del monitor (es. \\.\DISPLAY1). Vuoto = principale.</summary>
        public string Monitor { get => _monitor; set => Set(ref _monitor, value ?? ""); }

        public bool OnlyWhenFortniteFocused { get => _onlyWhenFortniteFocused; set => Set(ref _onlyWhenFortniteFocused, value); }
        /// <summary>Nasconde il mirino quando il cursore del mouse è visibile (menu, inventario, mappa).</summary>
        public bool HideWhenCursorVisible { get => _hideWhenCursorVisible; set => Set(ref _hideWhenCursorVisible, value); }
        /// <summary>Nasconde il mirino mentre si mira (tasto destro).</summary>
        public bool HideWhileAiming { get => _hideWhileAiming; set => Set(ref _hideWhileAiming, value); }
        /// <summary>Mira "alternata" di Fortnite: un clic entra in mira, il successivo esce.</summary>
        public bool AimToggleMode { get => _aimToggleMode; set => Set(ref _aimToggleMode, value); }
        /// <summary>Mostra per un attimo il nome del preset sotto al mirino quando lo cambi con la scorciatoia.</summary>
        public bool ShowPresetName { get => _showPresetName; set => Set(ref _showPresetName, value); }

        /// <summary>Come Math.Clamp ma un NaN (ammesso dal JSON delle impostazioni) diventa il valore di riserva.</summary>
        private static double ClampD(double v, double min, double max, double fallback = 1.0) =>
            double.IsNaN(v) ? fallback : Math.Clamp(v, min, max);

        /// <summary>
        /// System.Text.Json chiama questo metodo prima di leggere le proprietà: se il JSON non contiene
        /// "SchemaVersion" (file della v1) il valore resta 0 e le impostazioni vengono riconosciute come legacy.
        /// </summary>
        void IJsonOnDeserializing.OnDeserializing() => _schemaVersion = 0;

        /// <summary>
        /// Converte le impostazioni della v1 (solo Shape + dimensioni) nel modello a livelli riproducendo
        /// esattamente l'aspetto di prima. Non fa nulla se sono già v2. Va chiamato dopo AppSettings.Load:
        /// lo fa il costruttore di CrosshairService per il mirino principale; i preset vengono convertiti
        /// in modo "pigro" da <see cref="CrosshairPreset.NormalizeAll"/> (pagina Mirino, NextPreset) e da CopyFrom.
        /// </summary>
        public void NormalizeLegacy()
        {
            if (_schemaVersion >= CurrentSchema) return;
            var shape = Enum.IsDefined(_shape) ? _shape : CrosshairShape.CrossDot;

            // Nella v1 tutto ciò che esiste solo nella v2 era assente.
            OuterShow = false;
            DotRound = false;
            DotColor = "";
            CircleColor = "";
            GlowColor = "";
            Glow = false;
            RgbCycle = false;
            InnerOpacity = 1;
            DotOpacity = 1;
            CircleOpacity = 1;
            OutlineOpacity = 1;
            // Nella v1 il cerchio usava lo spessore delle linee.
            CircleThickness = Thickness;

            ApplyTemplate(shape);
            SchemaVersion = CurrentSchema;
        }

        /// <summary>Imposta i livelli secondo un modello rapido (usato dalla pagina e dalla conversione v1).</summary>
        public void ApplyTemplate(CrosshairShape shape)
        {
            Shape = shape;
            bool lines = shape is CrosshairShape.Cross or CrosshairShape.CrossDot or CrosshairShape.TShape or CrosshairShape.XShape;
            InnerShow = lines;
            DotShow = shape is CrosshairShape.CrossDot or CrosshairShape.Dot or CrosshairShape.CircleDot;
            CircleShow = shape is CrosshairShape.Circle or CrosshairShape.CircleDot;
            UseImage = shape == CrosshairShape.Image;
            OuterShow = false;
            Rotation = shape == CrosshairShape.XShape ? 45 : 0;
            if (lines)
            {
                ArmTop = shape != CrosshairShape.TShape;
                ArmBottom = ArmLeft = ArmRight = true;
            }
        }

        public CrosshairSettings Clone()
        {
            var c = new CrosshairSettings();
            c.CopyFrom(this);
            return c;
        }

        /// <summary>Copia l'aspetto (non stato, monitor, posizione e opzioni di visualizzazione).</summary>
        public void CopyFrom(CrosshairSettings o)
        {
            // Un preset salvato dalla v1 va prima convertito, altrimenti i suoi livelli sarebbero quelli predefiniti.
            o.NormalizeLegacy();

            Shape = o.Shape;
            Color = o.Color;
            Opacity = o.Opacity;
            Rotation = o.Rotation;
            RgbCycle = o.RgbCycle;
            RgbSpeed = o.RgbSpeed;

            InnerShow = o.InnerShow;
            Length = o.Length;
            Thickness = o.Thickness;
            Gap = o.Gap;
            InnerOpacity = o.InnerOpacity;

            OuterShow = o.OuterShow;
            OuterLength = o.OuterLength;
            OuterThickness = o.OuterThickness;
            OuterGap = o.OuterGap;
            OuterOpacity = o.OuterOpacity;

            ArmTop = o.ArmTop;
            ArmBottom = o.ArmBottom;
            ArmLeft = o.ArmLeft;
            ArmRight = o.ArmRight;

            DotShow = o.DotShow;
            DotSize = o.DotSize;
            DotRound = o.DotRound;
            DotColor = o.DotColor;
            DotOpacity = o.DotOpacity;

            CircleShow = o.CircleShow;
            CircleRadius = o.CircleRadius;
            CircleThickness = o.CircleThickness;
            CircleColor = o.CircleColor;
            CircleOpacity = o.CircleOpacity;

            Outline = o.Outline;
            OutlineThickness = o.OutlineThickness;
            OutlineColor = o.OutlineColor;
            OutlineOpacity = o.OutlineOpacity;

            Glow = o.Glow;
            GlowColor = o.GlowColor;
            GlowRadius = o.GlowRadius;

            UseImage = o.UseImage;
            ImagePath = o.ImagePath;
            ImageScale = o.ImageScale;

            SchemaVersion = CurrentSchema;
        }
    }

    public sealed class CrosshairPreset
    {
        public string Name { get; set; } = "";
        public CrosshairSettings Settings { get; set; } = new();
        public override string ToString() => Name;

        /// <summary>
        /// Converte (una volta sola, è idempotente) i preset salvati dalla v1 e ripara voci nulle del JSON.
        /// App.xaml.cs non chiama nulla dopo AppSettings.Load: lo fanno la pagina Mirino al caricamento e
        /// CrosshairService.NextPreset, mentre CopyFrom converte comunque il preset sorgente.
        /// </summary>
        public static void NormalizeAll(List<CrosshairPreset>? list)
        {
            if (list == null) return;
            list.RemoveAll(p => p == null);
            foreach (var p in list)
            {
                p.Name ??= "";
                p.Settings ??= new CrosshairSettings();
                p.Settings.NormalizeLegacy();
            }
        }

        private static CrosshairPreset P(string name, CrosshairShape template, Action<CrosshairSettings> cfg)
        {
            var s = new CrosshairSettings();
            s.ApplyTemplate(template);
            cfg(s);
            return new CrosshairPreset { Name = name, Settings = s };
        }

        public static List<CrosshairPreset> BuiltIn() => new()
        {
            P("Croce + punto verde", CrosshairShape.CrossDot, s =>
            {
                s.Color = "#00FF66"; s.Length = 7; s.Thickness = 2; s.Gap = 4; s.DotSize = 2;
            }),
            P("Ciano con linee esterne", CrosshairShape.Cross, s =>
            {
                s.Color = "#00E5FF"; s.Length = 6; s.Thickness = 2; s.Gap = 4;
                s.OuterShow = true; s.OuterLength = 4; s.OuterThickness = 2; s.OuterGap = 14; s.OuterOpacity = 0.6;
            }),
            P("Punto minimo", CrosshairShape.Dot, s =>
            {
                s.Color = "#FF2D55"; s.DotSize = 4; s.DotRound = true;
            }),
            P("Cerchio + punto", CrosshairShape.CircleDot, s =>
            {
                s.Color = "#FFFFFF"; s.CircleRadius = 14; s.CircleThickness = 1; s.DotSize = 2;
            }),
            P("T bianca", CrosshairShape.TShape, s =>
            {
                s.Color = "#FFFFFF"; s.Length = 7; s.Thickness = 2; s.Gap = 4;
            }),
            P("X magenta", CrosshairShape.XShape, s =>
            {
                s.Color = "#FF4DFF"; s.Length = 7; s.Thickness = 2; s.Gap = 4;
            }),
            P("Tattico (stile Valorant)", CrosshairShape.Cross, s =>
            {
                s.Color = "#FFFFFF"; s.Length = 4; s.Thickness = 2; s.Gap = 3;
                s.OuterShow = true; s.OuterLength = 2; s.OuterThickness = 2; s.OuterGap = 10; s.OuterOpacity = 0.5;
                s.OutlineOpacity = 0.5;
            }),
            P("Giallo alta visibilità", CrosshairShape.CrossDot, s =>
            {
                s.Color = "#FFFF00"; s.Length = 8; s.Thickness = 2; s.Gap = 3; s.DotSize = 2;
                s.Outline = true; s.OutlineThickness = 2; s.OutlineColor = "#000000";
            }),
            P("Arcobaleno RGB", CrosshairShape.CrossDot, s =>
            {
                s.Color = "#FF0000"; s.Length = 6; s.Thickness = 2; s.Gap = 3; s.RgbCycle = true; s.RgbSpeed = 1;
            }),
            P("Ciano luminoso", CrosshairShape.CrossDot, s =>
            {
                s.Color = "#00E5FF"; s.Length = 6; s.Thickness = 2; s.Gap = 4;
                s.Outline = false; s.Glow = true; s.GlowRadius = 6;
            }),
            P("Cerchio ampio + croce", CrosshairShape.Cross, s =>
            {
                s.Color = "#00FF66"; s.Length = 5; s.Thickness = 1; s.Gap = 3;
                s.CircleShow = true; s.CircleRadius = 18; s.CircleThickness = 1; s.CircleOpacity = 0.7;
            }),
        };
    }
}
