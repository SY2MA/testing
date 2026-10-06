using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FNBoost.Core
{
    public enum Impact { Low, Medium, High, Variable, Stability, MenuOnly }
    public enum Risk { None, Low, Medium }
    public enum TweakState { Unknown, Applied, NotApplied, Partial, NotApplicable }

    /// <summary>
    /// Un tweak reversibile. Ogni tweak:
    ///  1. salva i valori originali prima di cambiarli (BackupStore);
    ///  2. si può ripristinare in qualsiasi momento;
    ///  3. non tocca mai i file o il processo di Fortnite.
    /// </summary>
    public abstract class Tweak : ObservableObject
    {
        private TweakState _state = TweakState.Unknown;
        private bool _isBusy;
        private string? _notApplicableReason;
        private string _currentValue = "";

        public string Id { get; init; } = "";
        public string Category { get; init; } = "";
        public string Title { get; init; } = "";
        public string Description { get; init; } = "";
        /// <summary>Perché funziona / cosa dicono test e fonti.</summary>
        public string Details { get; init; } = "";
        public string Technical { get; init; } = "";
        public Impact Impact { get; init; }
        public Risk Risk { get; init; }
        public bool RequiresReboot { get; init; }
        public bool Recommended { get; init; }

        public TweakState State
        {
            get => _state;
            set
            {
                if (Set(ref _state, value))
                {
                    OnPropertyChanged(nameof(IsOn));
                    OnPropertyChanged(nameof(StateText));
                    OnPropertyChanged(nameof(IsApplicable));
                    OnPropertyChanged(nameof(CanToggle));
                }
            }
        }

        public bool IsOn => State == TweakState.Applied;
        public bool IsApplicable => State != TweakState.NotApplicable;
        public bool CanToggle => IsApplicable && !IsBusy && State != TweakState.Unknown;

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(CanToggle));
            }
        }

        /// <summary>Forza l'aggiornamento dell'interruttore anche se lo stato non è cambiato (es. errore).</summary>
        public void NotifyState()
        {
            OnPropertyChanged(nameof(IsOn));
            OnPropertyChanged(nameof(CanToggle));
        }

        public string? NotApplicableReason
        {
            get => _notApplicableReason;
            protected set => Set(ref _notApplicableReason, value);
        }

        public string CurrentValue
        {
            get => _currentValue;
            protected set => Set(ref _currentValue, value);
        }

        public string StateText => State switch
        {
            TweakState.Applied => "Attivo",
            TweakState.NotApplied => "Non attivo",
            TweakState.Partial => "Parziale",
            TweakState.NotApplicable => "Non applicabile",
            _ => "…"
        };

        public string ImpactText => Impact switch
        {
            Impact.Low => "Impatto: basso",
            Impact.Medium => "Impatto: medio",
            Impact.High => "Impatto: alto",
            Impact.Variable => "Impatto: variabile",
            Impact.Stability => "Stabilità",
            Impact.MenuOnly => "Solo desktop/menu",
            _ => ""
        };

        public string RiskText => Risk switch
        {
            Risk.None => "Rischio: nessuno",
            Risk.Low => "Rischio: basso",
            Risk.Medium => "Rischio: medio",
            _ => ""
        };

        /// <summary>Restituisce un motivo se il tweak non è applicabile su questo PC.</summary>
        protected virtual string? CheckApplicable() => null;

        protected abstract TweakState DetectCore();
        protected abstract void ApplyCore(BackupStore store);
        protected abstract void RevertCore(BackupStore store);

        public TweakState Detect()
        {
            try
            {
                var reason = CheckApplicable();
                NotApplicableReason = reason;
                return reason != null ? TweakState.NotApplicable : DetectCore();
            }
            catch (Exception ex)
            {
                Log.Error($"Rilevamento '{Title}'", ex);
                return TweakState.Unknown;
            }
        }

        public void Apply(BackupStore store)
        {
            if (CheckApplicable() is { } reason) throw new InvalidOperationException(reason);
            ApplyCore(store);
            Log.Info($"Applicato: {Title}");
        }

        public void Revert(BackupStore store)
        {
            RevertCore(store);
            Log.Info($"Ripristinato: {Title}");
        }
    }

    /// <summary>Un valore di registro da impostare.</summary>
    /// <param name="WindowsDefault">Valore predefinito di Windows (null = il valore non esiste di default).</param>
    /// <param name="PruneUpTo">Se impostato, al ripristino elimina le chiavi rimaste vuote fino a questo percorso.</param>
    /// <param name="MissingMeansDesired">true se, quando il valore non esiste, Windows si comporta già come desiderato.</param>
    public sealed record RegSpec(
        string Hive,
        string Path,
        string Name,
        RegistryValueKind Kind,
        object Value,
        object? WindowsDefault,
        string? PruneUpTo = null,
        bool MissingMeansDesired = false);

    public class RegistryTweak : Tweak
    {
        public IReadOnlyList<RegSpec> Specs { get; init; } = Array.Empty<RegSpec>();
        public Action? AfterChange { get; init; }
        public Func<string?>? Applicability { get; init; }

        protected override string? CheckApplicable() => Applicability?.Invoke();

        protected override TweakState DetectCore()
        {
            int ok = 0;
            var parts = new List<string>();
            foreach (var s in Specs)
            {
                var (exists, kind, value) = RegistryUtil.Read(s.Hive, s.Path, s.Name);
                if (exists ? RegistryUtil.ValueEquals(s.Kind, value, s.Value) : s.MissingMeansDesired) ok++;
                parts.Add($"{s.Name} = {(exists ? Describe(kind, value) : s.MissingMeansDesired ? "(predefinito di Windows, già ok)" : "(non impostato)")}");
            }
            CurrentValue = string.Join("   ·   ", parts);
            if (ok == Specs.Count) return TweakState.Applied;
            return ok == 0 ? TweakState.NotApplied : TweakState.Partial;
        }

        private static string Describe(RegistryValueKind kind, object? value)
        {
            if (value is string[] arr) return string.Join(" | ", arr.Where(a => a.Length > 0));
            if (kind == RegistryValueKind.DWord)
            {
                var u = unchecked((uint)Convert.ToInt32(value));
                return u == 0xFFFFFFFF ? "0xFFFFFFFF" : u.ToString();
            }
            return Convert.ToString(value) ?? "";
        }

        protected override void ApplyCore(BackupStore store)
        {
            if (store.Get(Id) == null)
            {
                store.Put(new TweakBackup
                {
                    TweakId = Id,
                    TweakTitle = Title,
                    Registry = Specs.Select(s => RegistryUtil.Snapshot(s.Hive, s.Path, s.Name)).ToList()
                });
            }

            var written = new List<RegSpec>();
            try
            {
                foreach (var s in Specs)
                {
                    RegistryUtil.Write(s.Hive, s.Path, s.Name, s.Kind, s.Value);
                    written.Add(s);
                }
            }
            catch
            {
                // Rollback parziale: rimette i valori già toccati come erano.
                var backup = store.Get(Id);
                if (backup != null)
                {
                    foreach (var s in written)
                    {
                        var snap = backup.Registry.FirstOrDefault(r => Same(r, s));
                        if (snap != null) TryRestore(snap, s.PruneUpTo);
                    }
                }
                throw;
            }
            AfterChange?.Invoke();
        }

        protected override void RevertCore(BackupStore store)
        {
            var backup = store.Get(Id);
            if (backup != null)
            {
                foreach (var snap in backup.Registry)
                {
                    var spec = Specs.FirstOrDefault(s => Same(snap, s));
                    RegistryUtil.Restore(snap, spec?.PruneUpTo);
                }
                store.Remove(Id);
            }
            else
            {
                // Nessun backup (tweak attivato fuori dall'app): torna ai valori predefiniti di Windows.
                foreach (var s in Specs)
                {
                    if (s.WindowsDefault == null)
                    {
                        RegistryUtil.Delete(s.Hive, s.Path, s.Name);
                        if (s.PruneUpTo != null) RegistryUtil.PruneEmptyKeys(s.Hive, s.Path, s.PruneUpTo);
                    }
                    else
                    {
                        RegistryUtil.Write(s.Hive, s.Path, s.Name, s.Kind, s.WindowsDefault);
                    }
                }
            }
            AfterChange?.Invoke();
        }

        private static bool Same(RegValueSnapshot r, RegSpec s) =>
            r.Hive == s.Hive &&
            string.Equals(r.Path, s.Path, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Name, s.Name, StringComparison.OrdinalIgnoreCase);

        private static void TryRestore(RegValueSnapshot snap, string? prune)
        {
            try { RegistryUtil.Restore(snap, prune); }
            catch (Exception ex) { Log.Error("Rollback fallito", ex); }
        }
    }

    /// <summary>
    /// Impostazioni salvate come "Chiave=Valore;" dentro HKCU\Software\Microsoft\DirectX\UserGpuPreferences
    /// (le stesse che scrive Impostazioni › Sistema › Schermo › Grafica).
    /// </summary>
    public sealed class DxUserSettingTweak : Tweak
    {
        public const string DxPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

        public Func<string?> ValueNameProvider { get; init; } = () => null;
        public string SettingKey { get; init; } = "";
        public string SettingValue { get; init; } = "";
        public string MissingReason { get; init; } = "Non disponibile";

        protected override string? CheckApplicable() => ValueNameProvider() == null ? MissingReason : null;

        protected override TweakState DetectCore()
        {
            var name = ValueNameProvider()!;
            var raw = RegistryUtil.ReadString("HKCU", DxPath, name) ?? "";
            var dict = Parse(raw);
            CurrentValue = raw.Length == 0 ? "(non impostato)" : raw;
            return dict.TryGetValue(SettingKey, out var v) && v == SettingValue
                ? TweakState.Applied
                : TweakState.NotApplied;
        }

        protected override void ApplyCore(BackupStore store)
        {
            var name = ValueNameProvider()!;
            var raw = RegistryUtil.ReadString("HKCU", DxPath, name) ?? "";
            var dict = Parse(raw);
            if (store.Get(Id) == null)
            {
                // Si salva solo la singola impostazione: le altre (Auto HDR, VRR…) restano di Windows.
                var backup = new TweakBackup { TweakId = Id, TweakTitle = Title };
                backup.Extra["valueName"] = name;
                backup.Extra["hadKey"] = dict.ContainsKey(SettingKey) ? "1" : "0";
                if (dict.TryGetValue(SettingKey, out var orig)) backup.Extra["original"] = orig;
                store.Put(backup);
            }
            dict[SettingKey] = SettingValue;
            RegistryUtil.Write("HKCU", DxPath, name, RegistryValueKind.String, Format(dict));
        }

        protected override void RevertCore(BackupStore store)
        {
            var backup = store.Get(Id);
            var name = backup != null && backup.Extra.TryGetValue("valueName", out var n) ? n : ValueNameProvider();
            if (name == null) return;

            var raw = RegistryUtil.ReadString("HKCU", DxPath, name) ?? "";
            var dict = Parse(raw);
            if (backup != null && backup.Extra.TryGetValue("hadKey", out var had) && had == "1" &&
                backup.Extra.TryGetValue("original", out var original))
                dict[SettingKey] = original;
            else
                dict.Remove(SettingKey);

            if (dict.Count == 0) RegistryUtil.Delete("HKCU", DxPath, name);
            else RegistryUtil.Write("HKCU", DxPath, name, RegistryValueKind.String, Format(dict));
            store.Remove(Id);
        }

        private static Dictionary<string, string> Parse(string raw)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var i = part.IndexOf('=');
                if (i > 0) d[part[..i]] = part[(i + 1)..];
            }
            return d;
        }

        private static string Format(Dictionary<string, string> d) =>
            string.Concat(d.Select(kv => $"{kv.Key}={kv.Value};"));
    }

    /// <summary>Piano energetico tramite powercfg.exe (strumento ufficiale di Windows).</summary>
    public sealed class PowerPlanTweak : Tweak
    {
        public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
        public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

        public string TargetScheme { get; init; } = HighPerformance;

        private static readonly Regex GuidRx = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

        public static (string? guid, string name) GetActive()
        {
            var output = PowerCfg("/getactivescheme", out _);
            var m = GuidRx.Match(output);
            var name = "";
            var open = output.LastIndexOf('(');
            var close = output.LastIndexOf(')');
            if (open >= 0 && close > open) name = output.Substring(open + 1, close - open - 1);
            return (m.Success ? m.Value.ToLowerInvariant() : null, name);
        }

        private static bool SchemeExists(string guid) =>
            PowerCfg("/list", out _).Contains(guid, StringComparison.OrdinalIgnoreCase);

        protected override TweakState DetectCore()
        {
            var (active, name) = GetActive();
            CurrentValue = $"Piano attivo: {name}";
            if (active == null) return TweakState.Unknown;
            if (active == TargetScheme) return TweakState.Applied;
            var created = FindCreatedScheme();
            return created != null && active == created ? TweakState.Applied : TweakState.NotApplied;
        }

        private string? FindCreatedScheme()
        {
            // Lo schema duplicato (se è stato necessario crearlo) è registrato nel backup.
            var store = App.Backups;
            var b = store?.Get(Id);
            return b != null && b.Extra.TryGetValue("createdScheme", out var g) ? g : null;
        }

        protected override void ApplyCore(BackupStore store)
        {
            var (active, _) = GetActive();
            var backup = store.Get(Id) ?? new TweakBackup { TweakId = Id, TweakTitle = Title };
            if (!backup.Extra.ContainsKey("previousScheme") && active != null)
                backup.Extra["previousScheme"] = active;

            var target = TargetScheme;
            if (!SchemeExists(target))
            {
                // Su alcuni PC lo schema "Prestazioni elevate" è nascosto: lo si duplica dall'originale.
                var output = PowerCfg($"-duplicatescheme {TargetScheme}", out var code);
                var m = GuidRx.Match(output);
                if (code != 0 || !m.Success)
                    throw new InvalidOperationException("Impossibile creare il piano 'Prestazioni elevate': " + output.Trim());
                target = m.Value.ToLowerInvariant();
                backup.Extra["createdScheme"] = target;
            }
            store.Put(backup);

            PowerCfg($"/setactive {target}", out var setCode);
            if (setCode != 0) throw new InvalidOperationException("powercfg /setactive non riuscito");
        }

        protected override void RevertCore(BackupStore store)
        {
            var backup = store.Get(Id);
            var previous = backup != null && backup.Extra.TryGetValue("previousScheme", out var p) ? p : Balanced;
            if (!SchemeExists(previous)) previous = Balanced;
            PowerCfg($"/setactive {previous}", out _);
            if (backup != null && backup.Extra.TryGetValue("createdScheme", out var created) &&
                !string.Equals(created, previous, StringComparison.OrdinalIgnoreCase))
            {
                PowerCfg($"-delete {created}", out _);
            }
            store.Remove(Id);
        }

        public static string PowerCfg(string args, out int exitCode)
        {
            var psi = new ProcessStartInfo(Path.Combine(AppPaths.System32, "powercfg.exe"), args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("powercfg non avviabile");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            exitCode = p.HasExited ? p.ExitCode : -1;
            return stdout + stderr;
        }
    }

    /// <summary>File di paging gestito automaticamente da Windows (impostazione predefinita).</summary>
    public sealed class PageFileTweak : RegistryTweak
    {
        public const string MmPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

        public static string[] ReadPagingFiles()
        {
            var (exists, _, value) = RegistryUtil.Read("HKLM", MmPath, "PagingFiles");
            return exists ? ((value as string[]) ?? Array.Empty<string>()).Where(s => s.Trim().Length > 0).ToArray()
                          : Array.Empty<string>();
        }

        /// <summary>true se Windows gestisce la dimensione (automatico o "dimensione gestita dal sistema").</summary>
        public static bool IsSystemManaged(string[] entries)
        {
            if (entries.Length == 0) return false; // nessun file di paging
            return entries.All(e =>
            {
                var t = e.Trim();
                if (t.StartsWith("?:", StringComparison.Ordinal)) return true;
                var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length == 1 || (parts.Length >= 3 && parts[^1] == "0" && parts[^2] == "0");
            });
        }

        /// <summary>Dimensione massima fissa in MB (se impostata manualmente).</summary>
        public static int? FixedMaxMb(string[] entries)
        {
            int? max = null;
            foreach (var e in entries)
            {
                var parts = e.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && int.TryParse(parts[^1], out var mb) && mb > 0)
                    max = (max ?? 0) + mb;
            }
            return max;
        }

        protected override string? CheckApplicable()
        {
            // Un file fisso ma già grande è una scelta valida dell'utente: non lo si tocca.
            var entries = ReadPagingFiles();
            if (!IsSystemManaged(entries) && FixedMaxMb(entries) is >= 16384 && App.Backups?.Get(Id) == null)
            {
                CurrentValue = string.Join(" | ", entries);
                return $"File di paging fisso già adeguato ({FixedMaxMb(entries) / 1024.0:0.#} GB): nessuna modifica necessaria.";
            }
            return null;
        }

        protected override TweakState DetectCore()
        {
            var entries = ReadPagingFiles();
            CurrentValue = entries.Length == 0 ? "Nessun file di paging!" : string.Join(" | ", entries);
            return IsSystemManaged(entries) ? TweakState.Applied : TweakState.NotApplied;
        }
    }
}
