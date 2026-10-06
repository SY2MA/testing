using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

// Questo file non dipende da WPF né da altre classi di FNBoost.Core: viene compilato anche dal progetto di test su Linux.

namespace FNBoost.Perf
{
    /// <summary>
    /// Archivio delle sessioni registrate: per ogni sessione un file &lt;id&gt;.json (metadati, statistiche,
    /// campioni al secondo) e un file &lt;id&gt;.ft con tutti i frametime (float32 little-endian).
    /// Thread-safe.
    /// </summary>
    public sealed class PerfSessionStore
    {
        private static readonly JsonSerializerOptions Opts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        private readonly string _dir;
        private readonly int _maxSessions;
        private readonly object _gate = new();
        private PerfSession[]? _cache;

        /// <summary>Notifica che l'elenco è cambiato (può arrivare da qualsiasi thread).</summary>
        public event Action? Changed;

        public PerfSessionStore(string directory, int maxSessions)
        {
            _dir = directory;
            _maxSessions = Math.Max(1, maxSessions);
        }

        /// <summary>%LOCALAPPDATA%\FNBoost\sessions</summary>
        public static string DefaultDirectory { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FNBoost", "sessions");

        public string DirectoryPath => _dir;

        /// <summary>Sessioni salvate, dalla più recente (solo metadati: i frametime si caricano a parte).</summary>
        public IReadOnlyList<PerfSession> List()
        {
            lock (_gate)
            {
                return _cache ??= LoadAll();
            }
        }

        public void Save(PerfSession session, float[] frametimes)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (string.IsNullOrWhiteSpace(session.Id)) session.Id = NewId(session.StartedAt == default ? DateTime.Now : session.StartedAt);
            var id = SafeId(session.Id);
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(_dir);
                WriteAtomic(FtPath(id), ToBytes(frametimes ?? Array.Empty<float>()));
                WriteAtomic(JsonPath(id), JsonSerializer.SerializeToUtf8Bytes(session, Opts));
                _cache = null;
                Prune();
                _cache = null;
            }
            Changed?.Invoke();
        }

        public float[]? LoadFrametimes(string id)
        {
            try
            {
                var path = FtPath(SafeId(id));
                byte[] bytes;
                lock (_gate)
                {
                    if (!File.Exists(path)) return null;
                    bytes = File.ReadAllBytes(path);
                }
                return FromBytes(bytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lettura frametime {id}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Riscrive solo i metadati (es. etichetta o note modificate dall'utente).</summary>
        public void UpdateMeta(PerfSession session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.Id)) return;
            var id = SafeId(session.Id);
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(_dir);
                WriteAtomic(JsonPath(id), JsonSerializer.SerializeToUtf8Bytes(session, Opts));
                _cache = null;
            }
            Changed?.Invoke();
        }

        public void Delete(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            var safe = SafeId(id);
            lock (_gate)
            {
                TryDelete(JsonPath(safe));
                TryDelete(FtPath(safe));
                _cache = null;
            }
            Changed?.Invoke();
        }

        /// <summary>Id ordinabile: yyyyMMdd-HHmmss + suffisso casuale breve.</summary>
        public static string NewId(DateTime startedAt) =>
            $"{startedAt:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N").Substring(0, 4)}";

        // ---- interni ----

        private string JsonPath(string id) => Path.Combine(_dir, id + ".json");
        private string FtPath(string id) => Path.Combine(_dir, id + ".ft");

        /// <summary>Evita che un id manipolato esca dalla cartella delle sessioni.</summary>
        private static string SafeId(string id)
        {
            var chars = id.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray();
            return chars.Length == 0 ? "sessione" : new string(chars);
        }

        private PerfSession[] LoadAll()
        {
            var list = new List<PerfSession>();
            try
            {
                if (!System.IO.Directory.Exists(_dir)) return Array.Empty<PerfSession>();
                foreach (var file in System.IO.Directory.EnumerateFiles(_dir, "*.json"))
                {
                    try
                    {
                        var s = JsonSerializer.Deserialize<PerfSession>(File.ReadAllBytes(file), Opts);
                        if (s == null) continue;
                        if (string.IsNullOrWhiteSpace(s.Id)) s.Id = Path.GetFileNameWithoutExtension(file);
                        s.Stats ??= new FrameStatsResult();
                        s.Seconds ??= new List<SecondSample>();
                        s.ActiveTweaks ??= new List<string>();
                        list.Add(s);
                    }
                    catch (Exception ex)
                    {
                        // Un file danneggiato non deve bloccare l'elenco.
                        System.Diagnostics.Debug.WriteLine($"Sessione illeggibile {file}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Elenco sessioni: {ex.Message}");
            }
            return list.OrderByDescending(s => s.StartedAt).ThenByDescending(s => s.Id, StringComparer.Ordinal).ToArray();
        }

        /// <summary>Elimina le sessioni più vecchie oltre il limite (chiamare sotto lock).</summary>
        private void Prune()
        {
            var all = LoadAll();
            for (int i = _maxSessions; i < all.Length; i++)
            {
                var id = SafeId(all[i].Id);
                TryDelete(JsonPath(id));
                TryDelete(FtPath(id));
            }
        }

        private static void WriteAtomic(string path, byte[] data)
        {
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, true);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Eliminazione {path}: {ex.Message}");
            }
        }

        private static byte[] ToBytes(float[] values)
        {
            var bytes = new byte[values.Length * 4];
            if (BitConverter.IsLittleEndian)
            {
                MemoryMarshal.AsBytes(values.AsSpan()).CopyTo(bytes);
            }
            else
            {
                for (int i = 0; i < values.Length; i++)
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
            }
            return bytes;
        }

        private static float[] FromBytes(byte[] bytes)
        {
            int n = bytes.Length / 4; // eventuali byte in eccesso (file troncato) vengono ignorati
            var values = new float[n];
            if (BitConverter.IsLittleEndian)
            {
                MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, n * 4)).CopyTo(values);
            }
            else
            {
                for (int i = 0; i < n; i++)
                    values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4));
            }
            return values;
        }
    }
}
