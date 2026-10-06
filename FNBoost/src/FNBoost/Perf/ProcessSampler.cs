using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using FNBoost.Core;

namespace FNBoost.Perf
{
    /// <summary>
    /// CPU e RAM per processo dai contatori di prestazioni di Windows (PDH, "\Process(*)\..."), gli stessi di
    /// Gestione attività / Monitoraggio risorse. I contatori sono pubblicati dal sistema: nessun processo viene
    /// aperto. Il gioco e gli anti-cheat non vengono né misurati né elencati.
    /// Campiona ogni 5 s su un timer proprio; ogni errore PDH degrada a "nessun dato".
    /// </summary>
    internal sealed class ProcessSampler : IDisposable
    {
        private const int IntervalMs = 5000;
        private const uint PDH_FMT_DOUBLE = 0x00000200;
        private const uint PDH_FMT_NOCAP100 = 0x00008000;
        private const uint PDH_MORE_DATA = 0x800007D2;
        private const uint PDH_CSTATUS_VALID_DATA = 0x0;
        private const uint PDH_CSTATUS_NEW_DATA = 0x1;

        private const string CpuCounter = @"\Process(*)\% Processor Time";
        private const string RamCounter = @"\Process(*)\Working Set - Private";

        private readonly object _lock = new();    // timer, stato e ultimo campione
        private readonly object _pdhLock = new(); // query PDH (la raccolta può durare decine di ms)
        private readonly string _ownName;
        private readonly int _cpus = Math.Max(1, Environment.ProcessorCount);
        private Timer? _timer;
        private IntPtr _query;
        private IntPtr _cpuCounter;
        private IntPtr _ramCounter;
        private volatile bool _failed;
        private volatile bool _disposed;
        private volatile string? _excludeName;
        private Dictionary<string, (double CpuPct, double RamMb)>? _latest;
        private int _seq;

        public ProcessSampler()
        {
            string own = "FNBoost";
            try
            {
                using var me = Process.GetCurrentProcess();
                own = me.ProcessName;
            }
            catch
            {
                // Nome predefinito.
            }
            _ownName = own;
        }

        /// <summary>Nome del processo del gioco misurato (escluso dall'elenco).</summary>
        public string? ExcludeName
        {
            get => _excludeName;
            set => _excludeName = value;
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_timer != null || _disposed) return;
                lock (_pdhLock) Open();
                if (_failed) return;
                _timer = new Timer(_ => Sample(), null, IntervalMs, IntervalMs);
            }
        }

        /// <summary>Ultimo campione se più recente di <paramref name="lastSeq"/> (che viene aggiornato), altrimenti null.</summary>
        public IReadOnlyDictionary<string, (double CpuPct, double RamMb)>? TakeIfNew(ref int lastSeq)
        {
            lock (_lock)
            {
                if (_latest == null || _seq == lastSeq) return null;
                lastSeq = _seq;
                return _latest;
            }
        }

        public void Dispose()
        {
            Timer? t;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                t = _timer;
                _timer = null;
            }
            try
            {
                // Attende un eventuale campionamento in corso prima di chiudere la query PDH.
                using var done = new ManualResetEvent(false);
                if (t != null && t.Dispose(done)) done.WaitOne(3000);
            }
            catch
            {
                // Best-effort.
            }
            lock (_pdhLock) Close();
        }

        private void Open()
        {
            try
            {
                if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0)
                {
                    _query = IntPtr.Zero;
                    _failed = true;
                    Log.Warn("PDH non disponibile: i processi in background non verranno registrati");
                    return;
                }
                if (PdhAddEnglishCounterW(_query, CpuCounter, IntPtr.Zero, out _cpuCounter) != 0) _cpuCounter = IntPtr.Zero;
                if (PdhAddEnglishCounterW(_query, RamCounter, IntPtr.Zero, out _ramCounter) != 0) _ramCounter = IntPtr.Zero;
                if (_cpuCounter == IntPtr.Zero)
                {
                    _failed = true;
                    Log.Warn("Contatore CPU per processo non disponibile");
                    Close();
                    return;
                }
                // Prima raccolta: "% Processor Time" è una differenza tra due letture.
                PdhCollectQueryData(_query);
            }
            catch (Exception ex)
            {
                _failed = true;
                Log.Warn("Contatori per processo non disponibili: " + ex.Message);
                Close();
            }
        }

        private void Close()
        {
            try
            {
                if (_query != IntPtr.Zero) PdhCloseQuery(_query);
            }
            catch
            {
                // Best-effort.
            }
            _query = IntPtr.Zero;
            _cpuCounter = IntPtr.Zero;
            _ramCounter = IntPtr.Zero;
        }

        private void Sample()
        {
            lock (_pdhLock)
            {
                if (_disposed || _failed || _query == IntPtr.Zero) return;
                try
                {
                    if (PdhCollectQueryData(_query) != 0) return;
                    string? game = _excludeName;
                    var map = new Dictionary<string, (double CpuPct, double RamMb)>(StringComparer.OrdinalIgnoreCase);

                    ReadArray(_cpuCounter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, (inst, v) =>
                    {
                        var name = ProcessNames.Normalize(inst);
                        if (ProcessNames.IsExcluded(name, _ownName, game)) return;
                        var cur = map.TryGetValue(name, out var x) ? x : default;
                        map[name] = (cur.CpuPct + Math.Max(0, v) / _cpus, cur.RamMb);
                    });
                    if (_ramCounter != IntPtr.Zero)
                        ReadArray(_ramCounter, PDH_FMT_DOUBLE, (inst, v) =>
                        {
                            var name = ProcessNames.Normalize(inst);
                            if (ProcessNames.IsExcluded(name, _ownName, game)) return;
                            var cur = map.TryGetValue(name, out var x) ? x : default;
                            map[name] = (cur.CpuPct, cur.RamMb + Math.Max(0, v) / 1048576.0);
                        });

                    // Istanze che cambiano indice tra due letture possono dare picchi assurdi: si limita al 100%.
                    var clean = new Dictionary<string, (double CpuPct, double RamMb)>(map.Count, StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in map) clean[kv.Key] = (Math.Min(100, kv.Value.CpuPct), kv.Value.RamMb);
                    lock (_lock)
                    {
                        _latest = clean;
                        _seq++;
                    }
                }
                catch (Exception ex)
                {
                    _failed = true;
                    Log.Warn("Campionamento processi interrotto: " + ex.Message);
                }
            }
        }

        private static void ReadArray(IntPtr counter, uint format, Action<string, double> onItem)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint size = 0;
                uint status = PdhGetFormattedCounterArrayW(counter, format, ref size, out _, IntPtr.Zero);
                if (status != PDH_MORE_DATA || size == 0) return;
                var buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    status = PdhGetFormattedCounterArrayW(counter, format, ref size, out var count, buffer);
                    if (status == PDH_MORE_DATA) continue;
                    if (status != 0) return;
                    int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
                    for (int i = 0; i < count; i++)
                    {
                        var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * itemSize);
                        if (item.FmtValue.CStatus != PDH_CSTATUS_VALID_DATA && item.FmtValue.CStatus != PDH_CSTATUS_NEW_DATA) continue;
                        var value = item.FmtValue.doubleValue;
                        if (double.IsNaN(value) || double.IsInfinity(value)) continue;
                        onItem(Marshal.PtrToStringUni(item.szName) ?? "", value);
                    }
                    return;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }

        // ---- P/Invoke pdh.dll ----

        [StructLayout(LayoutKind.Sequential)]
        private struct PDH_FMT_COUNTERVALUE
        {
            public uint CStatus;
            public double doubleValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PDH_FMT_COUNTERVALUE_ITEM
        {
            public IntPtr szName;
            public PDH_FMT_COUNTERVALUE FmtValue;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr query);
    }
}
