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
    /// Ceiling for one working-set apply's byte budget. HISTORY: this was 1 GiB, sized so a
    /// single-table COPY buffer could never approach a 2 GiB int wall in the then int-addressed
    /// validate/COPY paths. That wall has since been eliminated — the apply/COPY path is now
    /// long/size_t-addressed end to end (native IntentStage arena is size_t; TupleBuffer,
    /// CollectBlobs, CopyBlobValidator, CopyTupleParser.Parse*, and WriteFilteredAsync are all
    /// long, and the write streams from the unmanaged arena in 8 MiB windows — audited: no
    /// managed byte[] ever concatenates a whole single-table working set). So the 1 GiB clamp
    /// had become a throughput tourniquet, not a safety invariant: on a 48 GiB box it truncated
    /// the RAM/16 budget (~3.2 GiB) down to 1 GiB while tune-pg hands PG shared_buffers = RAM/4.
    ///
    /// The REAL remaining bound is row-COUNT, not bytes: CopyTupleParser's per-table metadata
    /// lists (List&lt;Hash128&gt;, List&lt;StagedRowRef&gt;) are int-indexed managed arrays that
    /// cap near ~134M rows/table (~2 GiB / 16 B); for the smallest rows (entities ~70 B) that is
    /// ~10 GiB of buffer. This ceiling is 4 GiB — ~2.5× under that row-count wall even for the
    /// smallest rows — so the RAM/16 budget flows through unclamped on typical boxes. To lift it
    /// further, first convert those row-metadata lists to long-indexed; do NOT raise ABOVE the
    /// row-count bound without that conversion.
    /// </summary>
    public const long MaxApplyBufferBytes = 4L << 30;

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

    /// <summary>
    /// Default ceiling for the COMPOSE-side flush envelope (see
    /// <see cref="WorkingSetFlushEnvelopeBytes"/>). Deliberately far below
    /// <see cref="MaxApplyBufferBytes"/>: this bounds the RESIDENT compose memory of one
    /// working set (deferred tier trees + the process-global content bank held live
    /// while a set is composed), not the COPY buffer.
    /// </summary>
    public const long DefaultFlushEnvelopeCeilingBytes = 512L << 20;

    /// <summary>RAM share offered to one compose flush envelope before the ceiling clamps it.</summary>
    private const int FlushEnvelopeRamShareDivisor = 64;

    /// <summary>
    /// COMPOSE-side flush envelope — the resident-memory ceiling for ONE working set
    /// before it is closed, applied, and its builder + content bank reset. This is
    /// DELIBERATELY far below <see cref="WorkingSetBudgetBytes"/> (which is the apply
    /// COPY-buffer safety ceiling). Holding millions of deferred tier trees plus a giant
    /// content bank in a single working set collapses compose throughput (MEASURED
    /// 30k → 1.8k rec/s as a ~4 GiB set filled with ~3M records before flushing) and
    /// spikes GC; a tight envelope flushes continuously in small bulk COPYs so compose
    /// stays fast and resident memory flat. Never exceeds the apply budget. Tunable via
    /// <c>LAPLACE_WS_FLUSH_MB</c> (megabytes); default RAM/64 clamped to
    /// [<see cref="MinWorkingSetBudgetBytes"/>, <see cref="DefaultFlushEnvelopeCeilingBytes"/>].
    /// </summary>
    public static long WorkingSetFlushEnvelopeBytes => ResolveFlushEnvelope();

    private static long ResolveFlushEnvelope()
    {
        long apply = WorkingSetBudgetBytes;
        string? env = Environment.GetEnvironmentVariable("LAPLACE_WS_FLUSH_MB");
        if (!string.IsNullOrWhiteSpace(env)
            && long.TryParse(env.Trim(), out long mb) && mb > 0)
            return Math.Clamp(mb << 20, MinWorkingSetBudgetBytes, apply);

        long ceiling = Math.Min(DefaultFlushEnvelopeCeilingBytes, apply);
        return Math.Clamp(TotalPhysicalBytes / FlushEnvelopeRamShareDivisor,
            MinWorkingSetBudgetBytes, ceiling);
    }

    // ---- Postgres memory GUC derivations (single source for tune-pg) --------------------
    // All are functions of physical RAM. tune-pg emits these; nothing hardcodes a GB literal.

    /// <summary>shared_buffers ≈ 25% of RAM, the standard OLTP starting point.</summary>
    public static long SharedBuffersBytes => Clamp(TotalPhysicalBytes / 4, 128L << 20, 16L << 30);

    /// <summary>effective_cache_size ≈ 65% of RAM (planner hint for OS + PG cache).</summary>
    public static long EffectiveCacheSizeBytes => Clamp(TotalPhysicalBytes * 65 / 100, 512L << 20, 96L << 30);

    /// <summary>maintenance_work_mem ≈ RAM/32 (index builds/vacuum), capped.</summary>
    // Index builds plateau near 1GB; autovacuum workers inherit this when
    // autovacuum_work_mem = -1, so an oversized value multiplies by worker
    // count (2026-07-15 incident arithmetic, doc 28).
    public static long MaintenanceWorkMemBytes => Clamp(TotalPhysicalBytes / 48, 256L << 20, 1L << 30);

    /// <summary>work_mem ≈ RAM/256 per sort/hash node, capped modestly.</summary>
    // work_mem is PER SORT/HASH NODE PER CONNECTION — it must be sized against
    // the connection budget (max_connections × concurrent nodes), never as a
    // flat RAM fraction. RAM/256 gave 190MB on a 48GB box; one misplanned
    // partitioned hash join starved the machine to a cold power boot
    // (2026-07-15, doc 28). RAM/1536 → 32MB at 48GB: worst case
    // 60 conns × 2 nodes × 32MB ≈ 3.8GB.
    public static long WorkMemBytes => Clamp(TotalPhysicalBytes / 1536, 16L << 20, 64L << 20);

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
