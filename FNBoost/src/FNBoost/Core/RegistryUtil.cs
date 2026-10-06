using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;

namespace FNBoost.Core
{
    /// <summary>Fotografia di un valore di registro, usata per il backup e il ripristino esatto.</summary>
    public sealed class RegValueSnapshot
    {
        public string Hive { get; set; } = "HKCU";
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Existed { get; set; }
        public string? Kind { get; set; }
        public string? Data { get; set; }
    }

    public static class RegistryUtil
    {
        public static RegistryKey OpenBase(string hive) => hive switch
        {
            "HKLM" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
            "HKCU" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
            _ => throw new ArgumentException("Hive non supportato: " + hive)
        };

        public static (bool exists, RegistryValueKind kind, object? value) Read(string hive, string path, string name)
        {
            using var root = OpenBase(hive);
            using var key = root.OpenSubKey(path, false);
            if (key == null) return (false, RegistryValueKind.Unknown, null);
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value == null) return (false, RegistryValueKind.Unknown, null);
            return (true, key.GetValueKind(name), value);
        }

        public static void Write(string hive, string path, string name, RegistryValueKind kind, object value)
        {
            using var root = OpenBase(hive);
            using var key = root.CreateSubKey(path, true)
                            ?? throw new InvalidOperationException($"Impossibile aprire {hive}\\{path}");
            key.SetValue(name, value, kind);
        }

        public static void Delete(string hive, string path, string name)
        {
            using var root = OpenBase(hive);
            using var key = root.OpenSubKey(path, true);
            key?.DeleteValue(name, false);
        }

        /// <summary>Elimina le chiavi vuote risalendo fino a <paramref name="stopAt"/> (escluso).</summary>
        public static void PruneEmptyKeys(string hive, string path, string stopAt)
        {
            using var root = OpenBase(hive);
            var current = path.TrimEnd('\\');
            while (current.Length > stopAt.Length &&
                   current.StartsWith(stopAt, StringComparison.OrdinalIgnoreCase))
            {
                using (var key = root.OpenSubKey(current, false))
                {
                    if (key == null)
                    {
                        // già assente: risali
                    }
                    else if (key.ValueCount > 0 || key.SubKeyCount > 0)
                    {
                        return;
                    }
                }
                root.DeleteSubKey(current, false);
                var idx = current.LastIndexOf('\\');
                if (idx <= 0) return;
                current = current[..idx];
            }
        }

        public static RegValueSnapshot Snapshot(string hive, string path, string name)
        {
            var (exists, kind, value) = Read(hive, path, name);
            return new RegValueSnapshot
            {
                Hive = hive,
                Path = path,
                Name = name,
                Existed = exists,
                Kind = exists ? kind.ToString() : null,
                Data = exists ? Serialize(kind, value) : null
            };
        }

        public static void Restore(RegValueSnapshot s, string? pruneUpTo = null)
        {
            if (!s.Existed)
            {
                Delete(s.Hive, s.Path, s.Name);
                if (pruneUpTo != null) PruneEmptyKeys(s.Hive, s.Path, pruneUpTo);
                return;
            }
            var kind = Enum.Parse<RegistryValueKind>(s.Kind!);
            Write(s.Hive, s.Path, s.Name, kind, Deserialize(kind, s.Data!));
        }

        public static string Serialize(RegistryValueKind kind, object? value)
        {
            switch (kind)
            {
                case RegistryValueKind.DWord:
                    return unchecked((uint)Convert.ToInt32(value, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture);
                case RegistryValueKind.QWord:
                    return unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture);
                case RegistryValueKind.MultiString:
                    return JsonSerializer.Serialize((value as string[]) ?? Array.Empty<string>());
                case RegistryValueKind.Binary:
                    return Convert.ToBase64String((value as byte[]) ?? Array.Empty<byte>());
                default:
                    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            }
        }

        public static object Deserialize(RegistryValueKind kind, string data)
        {
            switch (kind)
            {
                case RegistryValueKind.DWord:
                    return unchecked((int)uint.Parse(data, CultureInfo.InvariantCulture));
                case RegistryValueKind.QWord:
                    return unchecked((long)ulong.Parse(data, CultureInfo.InvariantCulture));
                case RegistryValueKind.MultiString:
                    return JsonSerializer.Deserialize<string[]>(data) ?? Array.Empty<string>();
                case RegistryValueKind.Binary:
                    return Convert.FromBase64String(data);
                default:
                    return data;
            }
        }

        /// <summary>Confronta un valore letto con quello desiderato, normalizzando i tipi.</summary>
        public static bool ValueEquals(RegistryValueKind kind, object? current, object desired)
        {
            if (current == null) return false;
            if (kind == RegistryValueKind.MultiString)
            {
                var a = (current as string[]) ?? Array.Empty<string>();
                var b = (desired as string[]) ?? Array.Empty<string>();
                return a.Where(x => x.Length > 0).SequenceEqual(b.Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);
            }
            try
            {
                return string.Equals(Serialize(kind, current), Serialize(kind, desired), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static int? ReadDword(string hive, string path, string name)
        {
            try
            {
                var (exists, kind, value) = Read(hive, path, name);
                if (!exists || kind != RegistryValueKind.DWord) return null;
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        public static string? ReadString(string hive, string path, string name)
        {
            try
            {
                var (exists, _, value) = Read(hive, path, name);
                return exists ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
