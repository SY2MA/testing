namespace FNBoost.Core
{
    /// <summary>Uso CPU e RAM di sistema (GetSystemTimes / GlobalMemoryStatusEx).</summary>
    public sealed class SystemMonitor
    {
        private ulong _lastIdle, _lastKernel, _lastUser;

        public SystemMonitor()
        {
            Sample();
        }

        /// <returns>(cpu %, ram %, ram usata GB, ram totale GB)</returns>
        public (double cpu, double ram, double usedGb, double totalGb) Sample()
        {
            double cpu = 0;
            if (Native.GetSystemTimes(out var idle, out var kernel, out var user))
            {
                var dIdle = idle.Value - _lastIdle;
                var dKernel = kernel.Value - _lastKernel;
                var dUser = user.Value - _lastUser;
                var total = dKernel + dUser; // il tempo kernel include l'idle
                if (_lastKernel != 0 && total > 0)
                    cpu = (total - dIdle) * 100.0 / total;
                _lastIdle = idle.Value;
                _lastKernel = kernel.Value;
                _lastUser = user.Value;
            }
            var m = Native.GetMemoryStatus();
            var totalGb = m.ullTotalPhys / 1073741824.0;
            var usedGb = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
            return (System.Math.Clamp(cpu, 0, 100), m.dwMemoryLoad, usedGb, totalGb);
        }
    }
}
