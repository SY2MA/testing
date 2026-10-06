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

        public static MonitorInfo? Find(string device)
        {
            var all = GetAll();
            return all.FirstOrDefault(m => string.Equals(m.Device, device, StringComparison.OrdinalIgnoreCase))
                   ?? all.FirstOrDefault(m => m.Primary)
                   ?? all.FirstOrDefault();
        }
    }
}
