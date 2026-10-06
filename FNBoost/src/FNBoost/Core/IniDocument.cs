using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FNBoost.Core
{
    /// <summary>
    /// Editor INI che modifica solo le righe richieste e conserva tutto il resto
    /// (ordine, commenti, codifica e fine riga), così il file resta identico a quello di Fortnite.
    /// </summary>
    public sealed class IniDocument
    {
        private readonly List<string> _lines;
        private readonly Encoding _encoding;
        private readonly string _newLine;
        private readonly bool _endsWithNewLine;

        private IniDocument(List<string> lines, Encoding encoding, string newLine, bool endsWithNewLine)
        {
            _lines = lines;
            _encoding = encoding;
            _newLine = newLine;
            _endsWithNewLine = endsWithNewLine;
        }

        public static IniDocument Load(string path)
        {
            var bytes = File.ReadAllBytes(path);
            Encoding enc;
            int skip;
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { enc = new UnicodeEncoding(false, true); skip = 2; }
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { enc = new UnicodeEncoding(true, true); skip = 2; }
            else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { enc = new UTF8Encoding(true); skip = 3; }
            else { enc = new UTF8Encoding(false); skip = 0; }

            var text = enc.GetString(bytes, skip, bytes.Length - skip);
            var newLine = text.Contains("\r\n") ? "\r\n" : "\n";
            var endsWithNewLine = text.EndsWith("\n", StringComparison.Ordinal);
            var lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
            if (endsWithNewLine && lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return new IniDocument(lines, enc, newLine, endsWithNewLine);
        }

        public void Save(string path)
        {
            var text = string.Join(_newLine, _lines) + (_endsWithNewLine ? _newLine : "");
            var tmp = path + ".fnboost.tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                var preamble = _encoding.GetPreamble();
                fs.Write(preamble, 0, preamble.Length);
                var data = _encoding.GetBytes(text);
                fs.Write(data, 0, data.Length);
            }
            File.Move(tmp, path, true);
        }

        private static bool IsSection(string line, out string name)
        {
            var t = line.Trim();
            if (t.Length > 2 && t[0] == '[' && t[^1] == ']')
            {
                name = t[1..^1];
                return true;
            }
            name = "";
            return false;
        }

        /// <summary>Restituisce (inizio, fine esclusa) delle righe della sezione, oppure null.</summary>
        private (int start, int end)? FindSection(string section)
        {
            for (int i = 0; i < _lines.Count; i++)
            {
                if (IsSection(_lines[i], out var name) && string.Equals(name, section, StringComparison.OrdinalIgnoreCase))
                {
                    int j = i + 1;
                    while (j < _lines.Count && !IsSection(_lines[j], out _)) j++;
                    return (i + 1, j);
                }
            }
            return null;
        }

        private int FindKey(string section, string key)
        {
            var range = FindSection(section);
            if (range == null) return -1;
            for (int i = range.Value.start; i < range.Value.end; i++)
            {
                var line = _lines[i];
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(line[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        public string? Get(string section, string key)
        {
            var idx = FindKey(section, key);
            if (idx < 0) return null;
            var line = _lines[idx];
            return line[(line.IndexOf('=') + 1)..].Trim();
        }

        public void Set(string section, string key, string value)
        {
            var idx = FindKey(section, key);
            if (idx >= 0)
            {
                _lines[idx] = $"{key}={value}";
                return;
            }
            var range = FindSection(section);
            if (range == null)
            {
                if (_lines.Count > 0 && _lines[^1].Trim().Length > 0) _lines.Add("");
                _lines.Add($"[{section}]");
                _lines.Add($"{key}={value}");
                _lines.Add("");
                return;
            }
            // Inserisce dopo l'ultima riga non vuota della sezione.
            int insertAt = range.Value.end;
            while (insertAt > range.Value.start && _lines[insertAt - 1].Trim().Length == 0) insertAt--;
            _lines.Insert(insertAt, $"{key}={value}");
        }
    }
}
