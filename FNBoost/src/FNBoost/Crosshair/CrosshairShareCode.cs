using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

// Non dipende da WPF (solo System.*): viene compilato anche dai test.

namespace FNBoost.Crosshair
{
    /// <summary>
    /// Codici di condivisione del mirino: "FNB1-" + base64url(Deflate(JSON compatto UTF-8)).
    /// Contengono solo l'aspetto: niente monitor, spostamento, regole di visibilità e soprattutto
    /// niente percorso dell'immagine (rivelerebbe cartelle e nome utente del PC).
    /// </summary>
    public static class CrosshairShareCode
    {
        public const string Prefix = "FNB1-";
        /// <summary>Lunghezza massima accettata per un codice incollato.</summary>
        public const int MaxCodeLength = 4096;
        /// <summary>Limite al JSON decompresso (protezione da "zip bomb").</summary>
        private const int MaxJsonBytes = 32 * 1024;

        private static readonly HashSet<string> Excluded = new(CrosshairSettings.NonAppearanceProperties, StringComparer.Ordinal);

        private static readonly Regex HexColor = new("^#(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.CultureInvariant);
        private static readonly Regex NamedColor = new("^[A-Za-z]{3,24}$", RegexOptions.CultureInvariant);

        /// <summary>Stesse proprietà della classe, ma senza quelle che non sono "aspetto" (in scrittura E in lettura).</summary>
        private static readonly JsonSerializerOptions Opts = new()
        {
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter() },
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers =
                {
                    ti =>
                    {
                        if (ti.Type != typeof(CrosshairSettings)) return;
                        for (int i = ti.Properties.Count - 1; i >= 0; i--)
                            if (Excluded.Contains(ti.Properties[i].Name)) ti.Properties.RemoveAt(i);
                    }
                }
            }
        };

        public static string Export(CrosshairSettings s)
        {
            var copy = new CrosshairSettings();
            copy.CopyFrom(s);
            copy.ImagePath = "";
            var json = JsonSerializer.SerializeToUtf8Bytes(copy, Opts);

            using var ms = new MemoryStream();
            using (var z = new DeflateStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
                z.Write(json, 0, json.Length);
            return Prefix + ToBase64Url(ms.ToArray());
        }

        public static bool TryImport(string code, out CrosshairSettings? result, out string error)
        {
            result = null;
            error = "";
            code = (code ?? "").Trim();
            if (code.Length == 0)
            {
                error = "Incolla prima un codice.";
                return false;
            }
            if (code.Length > MaxCodeLength)
            {
                error = "Il codice è troppo lungo: non sembra un codice di FN Boost.";
                return false;
            }
            if (!code.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Formato non riconosciuto: il codice deve iniziare con {Prefix}";
                return false;
            }

            var data = FromBase64Url(code.Substring(Prefix.Length));
            if (data == null || data.Length == 0)
            {
                error = "Codice non valido o incompleto (copialo di nuovo per intero).";
                return false;
            }

            byte[] json;
            try
            {
                json = Inflate(data);
            }
            catch (Exception)
            {
                error = "Codice danneggiato: impossibile decomprimerlo.";
                return false;
            }

            CrosshairSettings? s;
            try
            {
                s = JsonSerializer.Deserialize<CrosshairSettings>(json, Opts);
            }
            catch (Exception)
            {
                error = "Codice danneggiato: contenuto non leggibile.";
                return false;
            }
            if (s == null)
            {
                error = "Codice vuoto.";
                return false;
            }

            // Pulizia: i setter hanno già limitato i numeri, restano enum, colori e campi locali.
            if (!Enum.IsDefined(s.Shape)) s.Shape = CrosshairShape.CrossDot;
            s.NormalizeLegacy();
            s.Color = CleanColor(s.Color, "#00FF66");
            s.OutlineColor = CleanColor(s.OutlineColor, "#000000");
            s.DotColor = CleanColor(s.DotColor, "");
            s.CircleColor = CleanColor(s.CircleColor, "");
            s.GlowColor = CleanColor(s.GlowColor, "");
            s.ImagePath = ""; // mai un percorso locale da un codice
            s.Enabled = false;

            result = s;
            return true;
        }

        private static string CleanColor(string? c, string fallback)
        {
            c = (c ?? "").Trim();
            if (c.Length == 0) return fallback;
            return HexColor.IsMatch(c) || NamedColor.IsMatch(c) ? c : fallback;
        }

        private static byte[] Inflate(byte[] data)
        {
            using var input = new MemoryStream(data);
            using var z = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buf = new byte[4096];
            int n;
            while ((n = z.Read(buf, 0, buf.Length)) > 0)
            {
                if (output.Length + n > MaxJsonBytes) throw new InvalidDataException("Contenuto troppo grande");
                output.Write(buf, 0, n);
            }
            return output.ToArray();
        }

        private static string ToBase64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[]? FromBase64Url(string s)
        {
            var b = new StringBuilder(s.Length + 3);
            foreach (var ch in s)
            {
                if (char.IsWhiteSpace(ch)) continue; // codici spezzati su più righe in chat
                b.Append(ch switch { '-' => '+', '_' => '/', _ => ch });
            }
            if (b.Length % 4 == 1) return null;
            while (b.Length % 4 != 0) b.Append('=');
            var buffer = new byte[b.Length * 3 / 4];
            return Convert.TryFromBase64String(b.ToString(), buffer, out var written) ? buffer.AsSpan(0, written).ToArray() : null;
        }
    }
}
