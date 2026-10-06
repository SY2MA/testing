using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using FNBoost.Core;

namespace FNBoost.Crosshair
{
    public sealed class MonitorInfo
    {
        public string Device { get; init; } = "";
        public int Left { get; init; }
        public int Top { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public bool Primary { get; init; }
        public string Label => $"{Device.Replace(@"\\.\", "")} – {Width}×{Height}{(Primary ? " (principale)" : "")}";
        public override string ToString() => Label;
    }

    public static class Monitors
    {
        public static List<MonitorInfo> GetAll()
        {
            var list = new List<MonitorInfo>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref Native.RECT rc, IntPtr data) =>
            {
                var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
                if (Native.GetMonitorInfo(hMon, ref mi))
                {
                    list.Add(new MonitorInfo
                    {
                        Device = mi.szDevice,
                        Left = mi.rcMonitor.Left,
                        Top = mi.rcMonitor.Top,
                        Width = mi.rcMonitor.Right - mi.rcMonitor.Left,
                        Height = mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                        Primary = (mi.dwFlags & 1) != 0
                    });
                }
                return true;
            }, IntPtr.Zero);
            return list.OrderByDescending(m => m.Primary).ThenBy(m => m.Device).ToList();
        }

        /// <summary>
        /// true se <paramref name="current"/> (l'ItemsSource attuale di una ComboBox) descrive già gli stessi
        /// monitor di <paramref name="next"/>. Sostituire l'ItemsSource azzera la selezione e, con il binding
        /// TwoWay su SelectedValue, anche il monitor scelto nelle impostazioni: va fatto solo se serve.
        /// </summary>
        public static bool SameLayout(System.Collections.IEnumerable? current, IReadOnlyList<MonitorInfo> next)
        {
            if (current is not IReadOnlyList<MonitorInfo> cur || cur.Count != next.Count) return false;
            for (int i = 0; i < cur.Count; i++)
            {
                var a = cur[i];
                var b = next[i];
                if (a.Device != b.Device || a.Left != b.Left || a.Top != b.Top ||
                    a.Width != b.Width || a.Height != b.Height || a.Primary != b.Primary)
                    return false;
            }
            return true;
        }

        /// <summary>Il monitor configurato se esiste ancora, altrimenti il principale ("" se non ce ne sono).</summary>
        public static string Resolve(IReadOnlyList<MonitorInfo> monitors, string? device)
        {
            if (!string.IsNullOrEmpty(device) && monitors.Any(m => m.Device == device)) return device;
            return monitors.FirstOrDefault(m => m.Primary)?.Device ?? "";
        }

        public static MonitorInfo? Find(string device)
        {
            var all = GetAll();
            return all.FirstOrDefault(m => string.Equals(m.Device, device, StringComparison.OrdinalIgnoreCase))
                   ?? all.FirstOrDefault(m => m.Primary)
                   ?? all.FirstOrDefault();
        }
    }
}
