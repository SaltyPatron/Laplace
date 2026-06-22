using System.Numerics;
using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>
/// Hybrid CPU topology for ingest worker defaults. On Intel P/E CPUs, CPU-bound native work
/// (decompose, compose) should scale to performance-core count, not logical processor count.
/// Env overrides: <c>LAPLACE_P_CORES</c>, <c>LAPLACE_CPU_BOUND_WORKERS</c>.
/// </summary>
public static class CpuTopology
{
    public static int PerformanceCoreCount => Current.PerformanceCoreCount;
    public static int EfficientCoreCount => Current.EfficientCoreCount;
    public static int LogicalProcessorCount => Current.LogicalProcessorCount;
    public static bool IsHybrid => Current.IsHybrid;

    internal static CpuSnapshot? TestOverride;

    private static CpuSnapshot Current => TestOverride ?? LazySnapshot.Value;

    private static readonly Lazy<CpuSnapshot> LazySnapshot = new(Detect, LazyThreadSafetyMode.ExecutionAndPublication);

    public static int ResolveCpuBoundWorkers(int headroom = 1, int maxCap = 16)
    {
        if (TryParsePositiveEnv("LAPLACE_CPU_BOUND_WORKERS", out int hard))
            return hard;
        return Math.Clamp(PerformanceCoreCount - headroom, 1, maxCap);
    }

    public static int ResolveIoBoundWorkers(int defaultCap = 8) =>
        Math.Clamp(Math.Min(LogicalProcessorCount, defaultCap), 1, defaultCap);

    internal static CpuSnapshot Detect() => DetectPlatform();

    private static CpuSnapshot DetectPlatform()
    {
        int logical = Environment.ProcessorCount;
        if (TryParsePositiveEnv("LAPLACE_P_CORES", out int pinnedP))
            return new CpuSnapshot(pinnedP, 0, logical, IsHybrid: false);

        if (OperatingSystem.IsWindows())
        {
            if (TryDetectWindowsHybrid(out int pCores, out int eCores, out bool hybrid))
                return new CpuSnapshot(Math.Max(1, pCores), Math.Max(0, eCores), logical, hybrid);
        }

        return new CpuSnapshot(logical, 0, logical, IsHybrid: false);
    }

    private static bool TryDetectWindowsHybrid(out int performanceCores, out int efficientCores, out bool isHybrid)
    {
        performanceCores = 0;
        efficientCores = 0;
        isHybrid = false;

        const int RelationProcessorCore = 0;
        uint size = 0;
        if (!GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref size)
            && Marshal.GetLastWin32Error() != 122)
            return false;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref size))
                return false;

            int offset = 0;
            var cores = new List<(byte Eff, int LpCount)>();

            while (offset < (int)size)
            {
                var header = Marshal.PtrToStructure<LogicalProcessorInfoHeader>(buffer + offset);
                if (header.Relationship == RelationProcessorCore)
                {
                    int relOffset = offset + Marshal.SizeOf<LogicalProcessorInfoHeader>();
                    byte eff = Marshal.ReadByte(buffer, relOffset + 1);
                    int lpCount = ReadCoreLogicalProcessorCount(buffer, relOffset);
                    cores.Add((eff, lpCount));
                }
                if (header.Size == 0)
                    break;
                offset += (int)header.Size;
            }

            if (cores.Count == 0)
                return false;

            byte minClass = cores.Min(c => c.Eff);
            byte maxClass = cores.Max(c => c.Eff);
            int maxLp = cores.Max(c => c.LpCount);
            int minLp = cores.Where(c => c.LpCount > 0).Select(c => c.LpCount).DefaultIfEmpty(maxLp).Min();

            if (maxLp > minLp)
            {
                isHybrid = true;
                performanceCores = cores.Count(c => c.LpCount == maxLp);
                efficientCores = cores.Count(c => c.LpCount == minLp);
                return performanceCores > 0;
            }

            if (minClass != maxClass)
            {
                isHybrid = true;
                performanceCores = cores.Count(c => c.Eff == maxClass);
                efficientCores = cores.Count(c => c.Eff == minClass);
                return performanceCores > 0;
            }

            performanceCores = cores.Count;
            efficientCores = 0;
            isHybrid = false;
            return performanceCores > 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int ReadCoreLogicalProcessorCount(IntPtr buffer, int relationshipOffset)
    {
        ushort groupCount = (ushort)Marshal.ReadInt16(buffer, relationshipOffset + 22);
        if (groupCount == 0)
            return 0;

        int lpCount = 0;
        int groupOffset = relationshipOffset + 24;
        for (int g = 0; g < groupCount; g++)
        {
            int entryOffset = groupOffset + g * 16;
            ulong mask = (ulong)Marshal.ReadInt64(buffer, entryOffset);
            lpCount += BitOperations.PopCount(mask);
        }
        return lpCount;
    }

    private static bool TryParsePositiveEnv(string name, out int value)
    {
        value = 0;
        string? raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out value) && value > 0;
    }

    internal readonly record struct CpuSnapshot(
        int PerformanceCoreCount,
        int EfficientCoreCount,
        int LogicalProcessorCount,
        bool IsHybrid);

    [StructLayout(LayoutKind.Sequential)]
    private struct LogicalProcessorInfoHeader
    {
        public int Relationship;
        public uint Size;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationshipType,
        IntPtr buffer,
        ref uint returnedLength);
}
