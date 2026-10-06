using System;
using System.Globalization;
using System.Runtime.InteropServices;
using FNBoost.Core;
using Microsoft.Win32;

namespace FNBoost.Perf
{
    /// <summary>Valori di sistema letti una volta al secondo.</summary>
    internal readonly record struct SystemSample(double CpuPercent, double RamPercent, double? GpuPercent, double? VramUsedGb);

    /// <summary>
    /// Campiona CPU, RAM, GPU 3D e VRAM con i contatori di prestazioni di Windows (PDH), gli stessi di
    /// Gestione attività. Nessun accesso ai processi: i contatori GPU per processo sono pubblicati dal sistema.
    /// Ogni errore PDH degrada a "non disponibile" (null) senza interrompere il resto.
    /// Da usare da un solo thread alla volta.
    /// </summary>
    internal sealed class SystemSampler : IDisposable
    {
        private const uint PDH_FMT_DOUBLE = 0x00000200;
        private const uint PDH_MORE_DATA = 0x800007D2;
        private const uint PDH_CSTATUS_VALID_DATA = 0x0;
        private const uint PDH_CSTATUS_NEW_DATA = 0x1;

        private const string GpuEngineCounter = @"\GPU Engine(*engtype_3D)\Utilization Percentage";
        private const string VramCounter = @"\GPU Adapter Memory(*)\Dedicated Usage";

        private readonly SystemMonitor _monitor = new();
        private IntPtr _query;
        private IntPtr _gpuCounter;
        private IntPtr _vramCounter;
        private bool _pdhFailed;
        private bool _disposed;

        public SystemSampler()
        {
            OpenPdh();
        }

        /// <summary>Legge i valori attuali. targetPid = processo di cui sommare l'uso GPU (null = uso totale).</summary>
        public SystemSample Sample(int? targetPid)
        {
            double cpu = 0, ram = 0;
            try
            {
                var s = _monitor.Sample();
                cpu = s.cpu;
                ram = s.ram;
            }
            catch (Exception ex)
            {
                Log.Warn("Lettura CPU/RAM: " + ex.Message);
            }

            double? gpu = null, vram = null;
            if (!_disposed && !_pdhFailed && _query != IntPtr.Zero)
            {
                try
                {
                    if (PdhCollectQueryData(_query) == 0)
                    {
                        if (_gpuCounter != IntPtr.Zero) gpu = ReadGpu(targetPid);
                        if (_vramCounter != IntPtr.Zero) vram = ReadVram();
                    }
                }
                catch (Exception ex)
                {
                    // DllNotFound, EntryPointNotFound, ecc.: niente più PDH per questa sessione.
                    _pdhFailed = true;
                    Log.Warn("Contatori GPU non disponibili: " + ex.Message);
                }
            }
            return new SystemSample(cpu, ram, gpu, vram);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ClosePdh();
        }

        // ---- PDH ----

        private void OpenPdh()
        {
            try
            {
                if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0)
                {
                    _query = IntPtr.Zero;
                    _pdhFailed = true;
                    Log.Warn("PDH non disponibile: utilizzo GPU e VRAM non verranno mostrati");
                    return;
                }
                if (PdhAddEnglishCounterW(_query, GpuEngineCounter, IntPtr.Zero, out _gpuCounter) != 0)
                {
                    _gpuCounter = IntPtr.Zero;
                    Log.Warn("Contatore utilizzo GPU non disponibile");
                }
                if (PdhAddEnglishCounterW(_query, VramCounter, IntPtr.Zero, out _vramCounter) != 0)
                {
                    _vramCounter = IntPtr.Zero;
                    Log.Warn("Contatore VRAM non disponibile");
                }
                // Prima raccolta: i contatori "Utilization" sono differenze tra due letture.
                PdhCollectQueryData(_query);
            }
            catch (Exception ex)
            {
                _pdhFailed = true;
                Log.Warn("PDH non disponibile: " + ex.Message);
                ClosePdh();
            }
        }

        private void ClosePdh()
        {
            try
            {
                if (_query != IntPtr.Zero) PdhCloseQuery(_query);
            }
            catch
            {
                // Chiusura best-effort.
            }
            _query = IntPtr.Zero;
            _gpuCounter = IntPtr.Zero;
            _vramCounter = IntPtr.Zero;
        }

        private double? ReadGpu(int? targetPid)
        {
            double total = 0, mine = 0;
            bool any = false, anyMine = false;
            string? pidTag = targetPid is > 0 ? "pid_" + targetPid.Value.ToString(CultureInfo.InvariantCulture) + "_" : null;
            if (!ReadArray(_gpuCounter, (name, value) =>
                {
                    any = true;
                    total += value;
                    if (pidTag != null && name.StartsWith(pidTag, StringComparison.OrdinalIgnoreCase))
                    {
                        anyMine = true;
                        mine += value;
                    }
                }))
                return null;
            if (!any) return null;
            if (pidTag != null) return anyMine ? Math.Clamp(mine, 0, 100) : 0;
            return Math.Clamp(total, 0, 100);
        }

        private double? ReadVram()
        {
            double max = -1;
            if (!ReadArray(_vramCounter, (_, value) => max = Math.Max(max, value))) return null;
            return max < 0 ? null : max / 1073741824.0;
        }

        /// <summary>Legge tutte le istanze di un contatore con caratteri jolly.</summary>
        private static bool ReadArray(IntPtr counter, Action<string, double> onItem)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint size = 0;
                uint status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out _, IntPtr.Zero);
                if (status != PDH_MORE_DATA || size == 0) return false;
                var buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out var count, buffer);
                    if (status == PDH_MORE_DATA) continue; // nuove istanze apparse nel frattempo: riprova
                    if (status != 0) return false;
                    int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
                    for (int i = 0; i < count; i++)
                    {
                        var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * itemSize);
                        if (item.FmtValue.CStatus != PDH_CSTATUS_VALID_DATA && item.FmtValue.CStatus != PDH_CSTATUS_NEW_DATA) continue;
                        var value = item.FmtValue.doubleValue;
                        if (double.IsNaN(value) || double.IsInfinity(value)) continue;
                        onItem(Marshal.PtrToStringUni(item.szName) ?? "", value);
                    }
                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return false;
        }

        // ---- VRAM totale dal registro (classe "Display adapters") ----

        /// <summary>VRAM totale della scheda video più capiente (GB), letta dal registro; null se sconosciuta.</summary>
        internal static double? ReadVramTotalGb()
        {
            const string cls = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            try
            {
                if (!OperatingSystem.IsWindows()) return null;
                using var root = Registry.LocalMachine.OpenSubKey(cls);
                if (root == null) return null;
                long best = 0;
                foreach (var name in root.GetSubKeyNames())
                {
                    if (name.Length != 4 || !int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
                    try
                    {
                        using var k = root.OpenSubKey(name);
                        if (k == null) continue;
                        long bytes = ToBytes(k.GetValue("HardwareInformation.qwMemorySize"));
                        if (bytes <= 0) bytes = ToBytes(k.GetValue("HardwareInformation.MemorySize"));
                        best = Math.Max(best, bytes);
                    }
                    catch
                    {
                        // Alcune sottochiavi (es. "Properties") non sono leggibili: si saltano.
                    }
                }
                return best > 0 ? best / 1073741824.0 : null;
            }
            catch (Exception ex)
            {
                Log.Warn("Lettura VRAM totale: " + ex.Message);
                return null;
            }
        }

        private static long ToBytes(object? value) => value switch
        {
            long l => l,
            int i => unchecked((uint)i), // REG_DWORD: valore senza segno
            byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
            byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
            _ => 0
        };

        // ---- P/Invoke pdh.dll ----

        [StructLayout(LayoutKind.Sequential)]
        private struct PDH_FMT_COUNTERVALUE
        {
            public uint CStatus;
            public double doubleValue; // unione di 8 byte: con PDH_FMT_DOUBLE è un double (offset 8)
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
