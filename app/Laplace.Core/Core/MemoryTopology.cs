using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>
/// The single authority for physical-memory-derived sizing, the memory counterpart to
/// <see cref="CpuTopology"/>. Every RAM-scaled value — the working-set apply budget and the
/// Postgres memory GUCs (shared_buffers/work_mem/maintenance_work_mem/wal) — derives from
/// here so a box's real RAM, not a scattered literal, denotes them. Nothing downstream may
/// hardcode a byte budget or re-probe RAM independently.
/// </summary>
public static class MemoryTopology
{
    /// <summary>
    /// Hard PostgreSQL binary-COPY safety ceiling for one working-set apply. A single-table
    /// COPY-tuple buffer is addressed with a native length and, historically, validated
    /// through int-addressed paths; concatenating a whole working set per table (attestations
    /// dominate) must never approach the 2 GiB int wall. Capping the *total* apply footprint
    /// at 1 GiB keeps the largest single-table buffer well under that wall on any machine,
    /// regardless of RAM. This is a correctness invariant, not a tuning knob — do not raise it
    /// to chase throughput.
    /// </summary>
    public const long MaxApplyBufferBytes = 1L << 30;

    /// <summary>Floor so tiny/constrained hosts still make forward progress per apply.</summary>
    public const long MinWorkingSetBudgetBytes = 256L << 20;

    /// <summary>
    /// Fraction of physical RAM offered to one working-set apply before the COPY ceiling
    /// clamps it. The native COPY arenas are resident simultaneously with the PG-side write
    /// and the compose working set, so the per-apply share of RAM stays deliberately small;
    /// the ceiling wins on any large-memory box.
    /// </summary>
    private const int RamShareDivisor = 16;

    private static readonly Lazy<long> LazyTotalPhysical =
        new(DetectTotalPhysicalBytes, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static long? TestTotalPhysicalOverride;

    /// <summary>Real installed physical RAM in bytes (not the GC heap ceiling).</summary>
    public static long TotalPhysicalBytes => TestTotalPhysicalOverride ?? LazyTotalPhysical.Value;

    public static string DetectionSource { get; private set; } = "uninitialized";

    /// <summary>
    /// Byte budget for one working-set apply: a fraction of physical RAM, clamped to
    /// <see cref="MinWorkingSetBudgetBytes"/> below and <see cref="MaxApplyBufferBytes"/>
    /// above. The single source for <c>WorkingSetMode.BudgetBytes</c> and the runner's
    /// working-set flush cap.
    /// </summary>
    public static long WorkingSetBudgetBytes => Math.Clamp(
        TotalPhysicalBytes / RamShareDivisor,
        MinWorkingSetBudgetBytes,
        MaxApplyBufferBytes);

    // ---- Postgres memory GUC derivations (single source for tune-pg) --------------------
    // All are functions of physical RAM. tune-pg emits these; nothing hardcodes a GB literal.

    /// <summary>shared_buffers ≈ 25% of RAM, the standard OLTP starting point.</summary>
    public static long SharedBuffersBytes => Clamp(TotalPhysicalBytes / 4, 128L << 20, 16L << 30);

    /// <summary>effective_cache_size ≈ 65% of RAM (planner hint for OS + PG cache).</summary>
    public static long EffectiveCacheSizeBytes => Clamp(TotalPhysicalBytes * 65 / 100, 512L << 20, 96L << 30);

    /// <summary>maintenance_work_mem ≈ RAM/32 (index builds/vacuum), capped.</summary>
    public static long MaintenanceWorkMemBytes => Clamp(TotalPhysicalBytes / 32, 256L << 20, 4L << 30);

    /// <summary>work_mem ≈ RAM/256 per sort/hash node, capped modestly.</summary>
    public static long WorkMemBytes => Clamp(TotalPhysicalBytes / 256, 32L << 20, 512L << 20);

    /// <summary>wal_buffers ≈ RAM/512, PostgreSQL's own auto-cap is 16 MiB..1 GiB.</summary>
    public static long WalBuffersBytes => Clamp(TotalPhysicalBytes / 512, 16L << 20, 1L << 30);

    /// <summary>
    /// Approx resident bytes one accumulated consensus relation holds in the client-side fold
    /// dictionary: a (3×16B) key + the Acc state + ConcurrentDictionary node/bucket overhead.
    /// </summary>
    public const int ConsensusFoldBytesPerRelation = 256;

    /// <summary>
    /// Max distinct relations to accumulate before flushing a consensus-fold batch. Bounded so
    /// the fold dictionary stays within the working-set budget on any box (the former hardcoded
    /// 4,000,000 was ~2 GiB of dictionary regardless of installed RAM). Single source for
    /// <c>ConsensusAccumulatingWriter</c>'s staging threshold.
    /// </summary>
    public static int ConsensusFoldMaxRelations => (int)Math.Clamp(
        WorkingSetBudgetBytes / ConsensusFoldBytesPerRelation, 500_000, 8_000_000);

    private static long Clamp(long v, long lo, long hi) => Math.Clamp(v, lo, hi);

    private static long DetectTotalPhysicalBytes()
    {
        try
        {
            if (OperatingSystem.IsWindows() && TryWindowsPhysical(out long win))
            {
                DetectionSource = "windows-globalmemorystatusex";
                return win;
            }
            if (OperatingSystem.IsLinux() && TryLinuxMemTotal(out long lin))
            {
                DetectionSource = "linux-meminfo";
                return lin;
            }
        }
        catch
        {
            // fall through to the GC estimate — NEVER throw from a sizing probe
        }

        // Fallback: the runtime's available-memory view (container limit or heap ceiling).
        long gc = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        DetectionSource = "gc-fallback";
        return gc > 0 ? gc : (4L << 30);
    }

    private static bool TryWindowsPhysical(out long bytes)
    {
        bytes = 0;
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0)
            return false;
        bytes = (long)status.ullTotalPhys;
        return true;
    }

    private static bool TryLinuxMemTotal(out long bytes)
    {
        bytes = 0;
        const string path = "/proc/meminfo";
        if (!File.Exists(path)) return false;
        foreach (var line in File.ReadLines(path))
        {
            // "MemTotal:       65742880 kB"
            if (!line.StartsWith("MemTotal:", StringComparison.Ordinal)) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && long.TryParse(parts[1], out long kb))
            {
                bytes = kb * 1024;
                return true;
            }
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
