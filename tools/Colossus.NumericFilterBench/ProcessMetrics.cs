using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Colossus.NumericFilterBench;

internal static class ProcessMetrics
{
    public static (ulong ReadBytes, ulong WriteBytes) IoBytes()
    {
        if (!OperatingSystem.IsWindows()) return (0, 0);
        return GetProcessIoCounters(Process.GetCurrentProcess().Handle, out IoCounters counters)
            ? (counters.ReadTransferCount, counters.WriteTransferCount)
            : (0, 0);
    }

    public static int PageFaults()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var counters = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
        return GetProcessMemoryInfo(Process.GetCurrentProcess().Handle, ref counters, counters.Size)
            ? unchecked((int)counters.PageFaultCount)
            : 0;
    }

    public static void PinToSixCores()
    {
        if (!OperatingSystem.IsWindows()) return;
        int logical = Math.Min(Environment.ProcessorCount, 12);
        long mask = logical == 64 ? -1L : (1L << logical) - 1;
        Process.GetCurrentProcess().ProcessorAffinity = (nint)mask;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(
        IntPtr process, ref ProcessMemoryCounters counters, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }
}
