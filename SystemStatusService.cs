using System.IO;
using System.Runtime.InteropServices;

namespace WindowsNotch
{
    public readonly record struct SystemStatusSnapshot(
        double? CpuPercent,
        double? RamPercent,
        double? DiskPercent,
        string DiskLabel,
        int? BatteryPercent,
        bool IsCharging);

    /// <summary>
    /// Reads lightweight local system metrics without WMI or extra packages.
    /// CPU/RAM are sampled once per second; battery/disk every 30s.
    /// </summary>
    public sealed class SystemStatusService : IDisposable
    {
        private const int FastIntervalMs = 1000;
        private const int SlowEveryTicks = 30;

        private readonly object _gate = new();
        private readonly Timer _timer;

        private bool _running;
        private int _tick;
        private bool _hasCpuSample;
        private ulong _prevIdle;
        private ulong _prevKernel;
        private ulong _prevUser;

        private double? _cpuPercent;
        private double? _ramPercent;
        private double? _diskPercent;
        private string _diskLabel = "C:";
        private int? _batteryPercent;
        private bool _isCharging;

        public event Action<SystemStatusSnapshot>? StatusChanged;

        public SystemStatusService()
        {
            _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_running) return;
                _running = true;
                _tick = 0;
                _hasCpuSample = TrySampleCpuBaseline();
            }

            RefreshSlowMetrics();
            EmitStatus();
            _timer.Change(FastIntervalMs, FastIntervalMs);
        }

        public void Stop()
        {
            lock (_gate)
            {
                _running = false;
            }

            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        public SystemStatusSnapshot GetCurrentSnapshot()
        {
            lock (_gate)
            {
                if (!_running && !_hasCpuSample)
                {
                    _hasCpuSample = TrySampleCpuBaseline();
                    RefreshSlowMetrics();
                }
                return BuildSnapshot();
            }
        }

        private void Tick()
        {
            SystemStatusSnapshot? snapshot = null;

            lock (_gate)
            {
                if (!_running) return;

                _tick++;
                _cpuPercent = ReadCpuPercent();
                _ramPercent = ReadRamPercent();

                if (_tick % SlowEveryTicks == 0)
                {
                    ReadBattery();
                    ReadDisk();
                }

                snapshot = BuildSnapshot();
            }

            if (snapshot is { } value)
            {
                StatusChanged?.Invoke(value);
            }
        }

        private void RefreshSlowMetrics()
        {
            lock (_gate)
            {
                _ramPercent = ReadRamPercent();
                ReadBattery();
                ReadDisk();
            }
        }

        private void EmitStatus()
        {
            SystemStatusSnapshot snapshot;
            lock (_gate)
            {
                snapshot = BuildSnapshot();
            }

            StatusChanged?.Invoke(snapshot);
        }

        private SystemStatusSnapshot BuildSnapshot() => new(
            _cpuPercent,
            _ramPercent,
            _diskPercent,
            _diskLabel,
            _batteryPercent,
            _isCharging);

        private bool TrySampleCpuBaseline()
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
            {
                return false;
            }

            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;
            return true;
        }

        private double? ReadCpuPercent()
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
            {
                return _cpuPercent;
            }

            if (!_hasCpuSample)
            {
                _prevIdle = idle;
                _prevKernel = kernel;
                _prevUser = user;
                _hasCpuSample = true;
                return _cpuPercent;
            }

            ulong idleDelta = idle - _prevIdle;
            ulong kernelDelta = kernel - _prevKernel;
            ulong userDelta = user - _prevUser;

            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;

            ulong total = kernelDelta + userDelta;
            if (total == 0)
            {
                return _cpuPercent;
            }

            double busy = total > idleDelta ? total - idleDelta : 0;
            return Math.Clamp(busy * 100.0 / total, 0, 100);
        }

        private static double? ReadRamPercent()
        {
            var memory = new MemoryStatusEx
            {
                Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
            };

            if (!GlobalMemoryStatusEx(ref memory) || memory.TotalPhys == 0)
            {
                return null;
            }

            double used = memory.TotalPhys - Math.Min(memory.AvailPhys, memory.TotalPhys);
            return Math.Clamp(used * 100.0 / memory.TotalPhys, 0, 100);
        }

        private void ReadDisk()
        {
            try
            {
                string root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                var drive = new DriveInfo(root);
                _diskLabel = drive.Name.TrimEnd('\\');

                if (!drive.IsReady || drive.TotalSize <= 0)
                {
                    _diskPercent = null;
                    return;
                }

                double used = drive.TotalSize - drive.AvailableFreeSpace;
                _diskPercent = Math.Clamp(used * 100.0 / drive.TotalSize, 0, 100);
            }
            catch
            {
                _diskPercent = null;
            }
        }

        private void ReadBattery()
        {
            if (!GetSystemPowerStatus(out var status))
            {
                _batteryPercent = null;
                _isCharging = false;
                return;
            }

            bool noBattery = (status.BatteryFlag & 128) != 0;
            if (noBattery || status.BatteryLifePercent == 255)
            {
                _batteryPercent = null;
                _isCharging = false;
                return;
            }

            _batteryPercent = Math.Clamp((int)status.BatteryLifePercent, 0, 100);
            _isCharging = status.ACLineStatus == 1;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _running = false;
            }

            _timer.Dispose();
        }

        // ===================== Native interop =====================

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(
            out ulong idleTime,
            out ulong kernelTime,
            out ulong userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerStatus
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }
    }
}
