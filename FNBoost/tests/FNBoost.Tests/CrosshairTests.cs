using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FNBoost.Crosshair;

namespace FNBoost.Tests
{
    /// <summary>Test del mirino v2: codici di condivisione e conversione delle impostazioni v1.</summary>
    internal static class CrosshairTests
    {
        public static void RunAll()
        {
            Console.WriteLine("CrosshairShareCode");
            T.Run("andata e ritorno di tutte le proprietà dell'aspetto", ShareRoundtrip);
            T.Run("percorso immagine e opzioni locali mai nel codice", ShareStripsLocalData);
            T.Run("codici non validi, troppo lunghi o \"zip bomb\" rifiutati", ShareRejectsInvalid);
            T.Run("colori non validi sostituiti, prefisso e spazi tollerati", ShareSanitizes);

            Console.WriteLine("CrosshairSettings.NormalizeLegacy");
            T.Run("ogni forma della v1 diventa i livelli giusti", LegacyAllShapes);
            T.Run("v2 non toccata, conversione idempotente, forma sconosciuta", LegacyEdgeCases);
            T.Run("preset predefiniti già v2 e NormalizeAll", BuiltInPresets);
        }

        // Stesse opzioni di AppSettings (enum come stringhe).
        private static readonly JsonSerializerOptions SettingsOpts = new()
        {
            Converters = { new JsonStringEnumConverter() },
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        private static PropertyInfo[] AppearanceProps() =>
            typeof(CrosshairSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
                .Where(p => !CrosshairSettings.NonAppearanceProperties.Contains(p.Name))
                .ToArray();

        private static CrosshairSettings Custom()
        {
            var s = new CrosshairSettings
            {
                Shape = CrosshairShape.CircleDot,
                Color = "#12AB34",
                Opacity = 0.8,
                Rotation = 30,
                RgbCycle = true,
                RgbSpeed = 2.5,
                InnerShow = true,
                Length = 11,
                Thickness = 3,
                Gap = 6,
                InnerOpacity = 0.7,
                OuterShow = true,
                OuterLength = 9,
                OuterThickness = 4,
                OuterGap = 25,
                OuterOpacity = 0.3,
                ArmTop = false,
                ArmBottom = true,
                ArmLeft = false,
                ArmRight = true,
                DotShow = true,
                DotSize = 5,
                DotRound = true,
                DotColor = "#FF00FF",
                DotOpacity = 0.6,
                CircleShow = true,
                CircleRadius = 33,
                CircleThickness = 2,
                CircleColor = "#80FFFFFF",
                CircleOpacity = 0.5,
                Outline = false,
                OutlineThickness = 3,
                OutlineColor = "#101010",
                OutlineOpacity = 0.4,
                Glow = true,
                GlowColor = "Red",
                GlowRadius = 12,
                UseImage = false,
                ImageScale = 1.75
            };
            return s;
        }

        private static void ShareRoundtrip()
        {
            var src = Custom();
            var code = CrosshairShareCode.Export(src);
            T.True(code.StartsWith(CrosshairShareCode.Prefix, StringComparison.Ordinal), "prefisso FNB1-");
            T.True(code.Length <= CrosshairShareCode.MaxCodeLength, $"lunghezza {code.Length} entro il limite");
            T.True(code.All(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'), "solo caratteri base64url");

            T.True(CrosshairShareCode.TryImport(code, out var dst, out var err), "import riuscito: " + err);
            T.True(dst != null, "risultato non nullo");
            if (dst == null) return;
            foreach (var p in AppearanceProps())
            {
                if (p.Name == nameof(CrosshairSettings.ImagePath)) continue;
                T.Equal(p.GetValue(src)?.ToString(), p.GetValue(dst)?.ToString(), p.Name);
            }
            T.Equal(CrosshairSettings.CurrentSchema, dst.SchemaVersion, "SchemaVersion");
            T.True(!dst.Enabled, "mai acceso da un codice");

            // Esportare di nuovo dà lo stesso codice (deterministico).
            T.Equal(code, CrosshairShareCode.Export(dst), "codice stabile");

            // Anche un mirino predefinito fa andata e ritorno.
            var def = new CrosshairSettings();
            T.True(CrosshairShareCode.TryImport(CrosshairShareCode.Export(def), out var def2, out _), "predefinito");
            if (def2 != null)
                foreach (var p in AppearanceProps())
                    T.Equal(p.GetValue(def)?.ToString(), p.GetValue(def2)?.ToString(), "predefinito " + p.Name);
        }

        /// <summary>Decodifica a mano un codice (per controllare cosa contiene davvero).</summary>
        private static string DecodeJson(string code)
        {
            var b64 = code.Substring(CrosshairShareCode.Prefix.Length).Replace('-', '+').Replace('_', '/');
            while (b64.Length % 4 != 0) b64 += "=";
            using var input = new MemoryStream(Convert.FromBase64String(b64));
            using var z = new DeflateStream(input, CompressionMode.Decompress);
            using var r = new StreamReader(z, Encoding.UTF8);
            return r.ReadToEnd();
        }

        private static string Encode(string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            using var ms = new MemoryStream();
            using (var z = new DeflateStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
                z.Write(bytes, 0, bytes.Length);
            return CrosshairShareCode.Prefix + Convert.ToBase64String(ms.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static void ShareStripsLocalData()
        {
            var src = Custom();
            src.UseImage = true;
            src.ImagePath = @"C:\Users\MarioRossi\Pictures\mirino-segreto.png";
            src.Monitor = @"\\.\DISPLAY2";
            src.OffsetX = 17;
            src.OffsetY = -9;
            src.OnlyWhenFortniteFocused = true;
            src.HideWhenCursorVisible = true;
            src.HideWhileAiming = true;
            src.AimToggleMode = true;
            src.ShowPresetName = false;
            src.Enabled = true;

            var code = CrosshairShareCode.Export(src);
            var json = DecodeJson(code);
            T.True(!json.Contains("MarioRossi", StringComparison.Ordinal), "nome utente assente dal JSON");
            T.True(!json.Contains("mirino-segreto", StringComparison.Ordinal), "nome file assente dal JSON");
            foreach (var name in CrosshairSettings.NonAppearanceProperties)
                T.True(!json.Contains("\"" + name + "\"", StringComparison.Ordinal), $"proprietà locale {name} assente");
            T.True(json.Contains("\"UseImage\":true", StringComparison.Ordinal), "UseImage resta (aspetto)");

            T.True(CrosshairShareCode.TryImport(code, out var dst, out var err), "import: " + err);
            if (dst == null) return;
            T.Equal("", dst.ImagePath, "ImagePath vuoto");
            T.Equal("", dst.Monitor, "Monitor predefinito");
            T.Equal(0, dst.OffsetX, "OffsetX predefinito");
            T.Equal(0, dst.OffsetY, "OffsetY predefinito");
            T.True(!dst.OnlyWhenFortniteFocused && !dst.HideWhenCursorVisible && !dst.HideWhileAiming && !dst.AimToggleMode,
                "regole di visibilità non importate");
            T.True(dst.ShowPresetName, "ShowPresetName predefinito");
            T.True(!dst.Enabled, "Enabled falso");

            // Un codice costruito a mano con percorso e proprietà locali: vengono ignorati comunque.
            var forged = Encode("{\"SchemaVersion\":2,\"Color\":\"#FFFFFF\",\"ImagePath\":\"C:\\\\x.png\",\"Monitor\":\"\\\\\\\\.\\\\DISPLAY9\"," +
                                "\"OffsetX\":150,\"Enabled\":true,\"HideWhileAiming\":true}");
            T.True(CrosshairShareCode.TryImport(forged, out var f, out err), "codice forgiato leggibile: " + err);
            if (f == null) return;
            T.Equal("", f.ImagePath, "ImagePath forgiato scartato");
            T.Equal("", f.Monitor, "Monitor forgiato scartato");
            T.Equal(0, f.OffsetX, "OffsetX forgiato scartato");
            T.True(!f.Enabled && !f.HideWhileAiming, "Enabled/HideWhileAiming forgiati scartati");
            T.Equal("#FFFFFF", f.Color, "Color letto");
        }

        private static void ShareRejectsInvalid()
        {
            void Reject(string? code, string what)
            {
                bool ok = CrosshairShareCode.TryImport(code!, out var r, out var err);
                T.True(!ok, what + " rifiutato");
                T.True(r == null, what + ": nessun risultato");
                T.True(!string.IsNullOrWhiteSpace(err), what + ": messaggio d'errore presente");
            }

            Reject("", "vuoto");
            Reject("   ", "solo spazi");
            Reject(null, "null");
            Reject("ciao", "testo qualsiasi");
            Reject("FNB2-AAAA", "prefisso sconosciuto");
            Reject("FNB1-", "solo prefisso");
            Reject("FNB1-!!!!", "base64 non valido");
            Reject("FNB1-A", "base64 troncato");
            Reject("FNB1-" + Convert.ToBase64String(Encoding.UTF8.GetBytes("questo non è deflate")).TrimEnd('='), "dati non compressi");
            Reject(Encode("non è json"), "JSON non valido");
            Reject(Encode("null"), "JSON null");
            Reject(Encode("[1,2,3]"), "JSON array");
            Reject(CrosshairShareCode.Prefix + new string('A', CrosshairShareCode.MaxCodeLength), "troppo lungo");

            // "Zip bomb": pochi byte compressi che diventano più di 32 KB di JSON.
            var bomb = Encode("{\"Color\":\"#FFFFFF\"," + new string(' ', 200_000) + "\"Length\":5}");
            T.True(bomb.Length < CrosshairShareCode.MaxCodeLength, "la bomba sta nel limite di lunghezza");
            Reject(bomb, "contenuto decompresso troppo grande");

            // Un codice valido troncato a metà.
            var good = CrosshairShareCode.Export(Custom());
            Reject(good.Substring(0, good.Length / 2), "codice troncato");
        }

        private static void ShareSanitizes()
        {
            var code = Encode("{\"SchemaVersion\":2,\"Shape\":\"Dot\",\"Color\":\"url(http://evil)\",\"DotColor\":\"#GGGGGG\"," +
                              "\"OutlineColor\":\"\",\"GlowColor\":\"Lime\",\"CircleColor\":\"#11223344\",\"Length\":9999,\"Opacity\":-5}");
            T.True(CrosshairShareCode.TryImport(code, out var s, out var err), "import: " + err);
            if (s == null) return;
            T.Equal("#00FF66", s.Color, "colore non valido → predefinito");
            T.Equal("", s.DotColor, "DotColor non valido → vuoto (colore principale)");
            T.Equal("#000000", s.OutlineColor, "OutlineColor vuoto → nero");
            T.Equal("Lime", s.GlowColor, "colore con nome accettato");
            T.Equal("#11223344", s.CircleColor, "ARGB accettato");
            T.Equal(80, s.Length, "Length limitata");
            T.Near(0.1, s.Opacity, 1e-9, "Opacity limitata");

            // Prefisso in minuscolo e codice spezzato su più righe (copiato dalla chat).
            var good = CrosshairShareCode.Export(Custom());
            var messy = "  fnb1-" + good.Substring(5, 10) + "\n " + good.Substring(15) + "  ";
            T.True(CrosshairShareCode.TryImport(messy, out var m, out err), "prefisso minuscolo e a capo: " + err);
            if (m != null) T.Equal("#12AB34", m.Color, "contenuto letto");
        }

        // ================= Conversione v1 =================

        /// <summary>JSON come lo salvava la v1 (niente SchemaVersion né livelli).</summary>
        private static CrosshairSettings V1(CrosshairShape shape, int thickness = 3)
        {
            var json = $"{{\"Shape\":\"{shape}\",\"Color\":\"#FF4400\",\"Length\":9,\"Thickness\":{thickness},\"Gap\":5," +
                       "\"DotSize\":3,\"CircleRadius\":15,\"Outline\":true,\"OutlineThickness\":1,\"OutlineColor\":\"#000000\"," +
                       "\"Opacity\":0.9,\"OffsetX\":4,\"OffsetY\":-2,\"Monitor\":\"\",\"OnlyWhenFortniteFocused\":true}";
            var s = JsonSerializer.Deserialize<CrosshairSettings>(json, SettingsOpts);
            if (s == null) throw new InvalidOperationException("deserializzazione v1 fallita");
            return s;
        }

        private static void LegacyAllShapes()
        {
            foreach (CrosshairShape shape in Enum.GetValues(typeof(CrosshairShape)))
            {
                var s = V1(shape);
                T.Equal(0, s.SchemaVersion, $"{shape}: JSON v1 riconosciuto (SchemaVersion 0)");
                // Valori "v2" sporchi che la conversione deve azzerare.
                s.Glow = true;
                s.RgbCycle = true;
                s.OuterShow = true;
                s.DotColor = "#123456";
                s.NormalizeLegacy();

                string w = shape.ToString();
                T.Equal(CrosshairSettings.CurrentSchema, s.SchemaVersion, w + ": SchemaVersion");
                T.Equal(shape, s.Shape, w + ": Shape");
                bool lines = shape is CrosshairShape.Cross or CrosshairShape.CrossDot or CrosshairShape.TShape or CrosshairShape.XShape;
                bool dot = shape is CrosshairShape.CrossDot or CrosshairShape.Dot or CrosshairShape.CircleDot;
                bool circle = shape is CrosshairShape.Circle or CrosshairShape.CircleDot;
                T.Equal(lines, s.InnerShow, w + ": InnerShow");
                T.Equal(dot, s.DotShow, w + ": DotShow");
                T.Equal(circle, s.CircleShow, w + ": CircleShow");
                T.Equal(shape == CrosshairShape.Image, s.UseImage, w + ": UseImage");
                T.Equal(shape == CrosshairShape.XShape ? 45 : 0, s.Rotation, w + ": Rotation");
                T.Equal(false, s.OuterShow, w + ": OuterShow");
                T.Equal(false, s.Glow, w + ": Glow");
                T.Equal(false, s.RgbCycle, w + ": RgbCycle");
                T.Equal("", s.DotColor, w + ": DotColor");
                T.Equal(3, s.CircleThickness, w + ": il cerchio v1 usava lo spessore delle linee");
                if (lines)
                {
                    T.Equal(shape != CrosshairShape.TShape, s.ArmTop, w + ": ArmTop");
                    T.True(s.ArmBottom && s.ArmLeft && s.ArmRight, w + ": altri bracci");
                }
                // Le dimensioni e il resto della v1 restano identici.
                T.Equal("#FF4400", s.Color, w + ": Color");
                T.Equal(9, s.Length, w + ": Length");
                T.Equal(3, s.Thickness, w + ": Thickness");
                T.Equal(5, s.Gap, w + ": Gap");
                T.Equal(3, s.DotSize, w + ": DotSize");
                T.Equal(15, s.CircleRadius, w + ": CircleRadius");
                T.Near(0.9, s.Opacity, 1e-9, w + ": Opacity");
                T.Equal(4, s.OffsetX, w + ": OffsetX");
                T.True(s.OnlyWhenFortniteFocused, w + ": opzioni di visibilità conservate");
                T.Near(1, s.InnerOpacity, 1e-9, w + ": InnerOpacity");
                T.Near(1, s.OutlineOpacity, 1e-9, w + ": OutlineOpacity");

                // CopyFrom da un preset v1 converte prima la sorgente.
                var dst = new CrosshairSettings();
                var src = V1(shape);
                dst.CopyFrom(src);
                T.Equal(CrosshairSettings.CurrentSchema, src.SchemaVersion, w + ": sorgente convertita da CopyFrom");
                T.Equal(dot, dst.DotShow, w + ": CopyFrom DotShow");
                T.Equal(circle, dst.CircleShow, w + ": CopyFrom CircleShow");
            }
        }

        private static void LegacyEdgeCases()
        {
            // Impostazioni già v2: NormalizeLegacy non deve cambiare nulla.
            var v2 = JsonSerializer.Deserialize<CrosshairSettings>(
                "{\"SchemaVersion\":2,\"Shape\":\"Dot\",\"InnerShow\":true,\"DotShow\":false,\"Glow\":true,\"Rotation\":10}", SettingsOpts)!;
            v2.NormalizeLegacy();
            T.True(v2.InnerShow && !v2.DotShow && v2.Glow, "v2 lasciata com'è");
            T.Equal(10, v2.Rotation, "Rotation v2 lasciata");

            // Nuove impostazioni (nessun JSON) partono già v2.
            T.Equal(CrosshairSettings.CurrentSchema, new CrosshairSettings().SchemaVersion, "nuove impostazioni già v2");

            // Idempotente: una seconda chiamata non tocca le modifiche fatte dopo la conversione.
            var s = V1(CrosshairShape.Cross);
            s.NormalizeLegacy();
            s.Glow = true;
            s.OuterShow = true;
            s.NormalizeLegacy();
            T.True(s.Glow && s.OuterShow, "seconda conversione senza effetti");

            // Forma numerica sconosciuta (file modificato a mano): diventa croce + punto.
            var odd = JsonSerializer.Deserialize<CrosshairSettings>("{\"Shape\":99,\"Thickness\":2}", SettingsOpts)!;
            odd.NormalizeLegacy();
            T.True(odd.InnerShow && odd.DotShow && !odd.CircleShow, "forma sconosciuta → croce + punto");
            T.Equal(CrosshairSettings.CurrentSchema, odd.SchemaVersion, "forma sconosciuta: SchemaVersion");
        }

        private static void BuiltInPresets()
        {
            var list = CrosshairPreset.BuiltIn();
            T.True(list.Count >= 11, $"almeno 11 preset (trovati {list.Count})");
            T.True(list.All(p => p.Settings.SchemaVersion == CrosshairSettings.CurrentSchema), "tutti già v2");
            T.Equal(list.Count, list.Select(p => p.Name).Distinct().Count(), "nomi unici");
            foreach (var p in list)
            {
                T.True(CrosshairShareCode.TryImport(CrosshairShareCode.Export(p.Settings), out var r, out var err), p.Name + ": codice valido " + err);
            }

            // NormalizeAll ripara voci nulle e converte i preset v1.
            var mixed = new System.Collections.Generic.List<CrosshairPreset>
            {
                null!,
                new() { Name = "vecchio", Settings = V1(CrosshairShape.Circle) },
                new() { Name = null!, Settings = null! }
            };
            CrosshairPreset.NormalizeAll(mixed);
            T.Equal(2, mixed.Count, "voce nulla rimossa");
            T.True(mixed[0].Settings.CircleShow && !mixed[0].Settings.InnerShow, "preset v1 convertito");
            T.True(mixed[1].Settings != null && mixed[1].Name != null, "preset senza dati riparato");
        }
    }
}
