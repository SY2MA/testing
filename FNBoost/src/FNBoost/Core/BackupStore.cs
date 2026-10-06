using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FNBoost.Core
{
    /// <summary>Valori originali salvati prima di applicare un tweak.</summary>
    public sealed class TweakBackup
    {
        public string TweakId { get; set; } = "";
        public string TweakTitle { get; set; } = "";
        public DateTime AppliedAt { get; set; } = DateTime.Now;
        public List<RegValueSnapshot> Registry { get; set; } = new();
        public Dictionary<string, string> Extra { get; set; } = new();
    }

    /// <summary>
    /// Archivio JSON dei backup (%LOCALAPPDATA%\FNBoost\tweak-backups.json).
    /// Il valore originale viene salvato solo la prima volta che un tweak viene applicato,
    /// così il ripristino riporta sempre allo stato precedente all'app.
    /// </summary>
    public sealed class BackupStore
    {
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
        private readonly object _gate = new();
        private Dictionary<string, TweakBackup> _items = new(StringComparer.OrdinalIgnoreCase);

        public static BackupStore Load()
        {
            var store = new BackupStore();
            try
            {
                if (File.Exists(AppPaths.BackupFile))
                {
                    var json = File.ReadAllText(AppPaths.BackupFile);
                    var list = JsonSerializer.Deserialize<List<TweakBackup>>(json) ?? new();
                    store._items = list.ToDictionary(b => b.TweakId, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Lettura backup fallita (verrà creato un nuovo file)", ex);
                try
                {
                    File.Copy(AppPaths.BackupFile, AppPaths.BackupFile + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
                }
                catch { /* ignorato */ }
            }
            return store;
        }

        public TweakBackup? Get(string id)
        {
            lock (_gate) return _items.TryGetValue(id, out var b) ? b : null;
        }

        public void Put(TweakBackup backup)
        {
            lock (_gate)
            {
                _items[backup.TweakId] = backup;
                Save();
            }
        }

        public void Remove(string id)
        {
            lock (_gate)
            {
                if (_items.Remove(id)) Save();
            }
        }

        public IReadOnlyList<TweakBackup> All()
        {
            lock (_gate) return _items.Values.OrderBy(b => b.AppliedAt).ToList();
        }

        private void Save()
        {
            AppPaths.Ensure();
            var tmp = AppPaths.BackupFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items.Values.ToList(), JsonOpts));
            File.Move(tmp, AppPaths.BackupFile, true);
        }
    }
}
