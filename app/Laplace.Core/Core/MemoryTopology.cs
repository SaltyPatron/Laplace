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

    /// <summary>
    /// shared_buffers ≈ 25% of RAM, the standard OLTP starting point.
    ///
    /// CEILING RAISED 16 GiB -> 64 GiB (2026-07-28). The 16 GiB cap was sized when the
    /// substrate was small and stopped tracking it: MEASURED on the 128 GB box, RAM/4 is
    /// 33.5 GiB but the cap pinned shared_buffers at 16 GiB against a 173 GB database —
    /// ~11x oversubscribed — and a full-corpus ingest sat in IO.DataFileRead /
    /// IO.AioIoCompletion with content-hash dedup probes missing cache and going to disk.
    /// A hardcoded ceiling that no longer tracks the machine is the same defect as the
    /// frozen hot-relation roster and the per-decomposer batch literals.
    ///
    /// Unlike <see cref="WorkMemBytes"/> this is a SINGLE shared allocation, not a
    /// per-connection multiplier, so it carries none of the 2026-07-15 starvation
    /// arithmetic — 64 GiB is one allocation on any box large enough for RAM/4 to reach it,
    /// and RAM/4 still governs everything smaller. Above ~64 GiB, PostgreSQL's own clock
    /// sweep and checkpoint cost stop paying back, which is why a ceiling remains.
    /// </summary>
    public static long SharedBuffersBytes => Clamp(TotalPhysicalBytes / 4, 128L << 20, 64L << 30);

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

    // ---- Aggregate budget: the invariant nothing computed ------------------------------
    //
    // Every GUC above is derived in ISOLATION from physical RAM. Nothing ever added them
    // up. shared_buffers is one pinned allocation, but work_mem is per sort/hash node PER
    // CONNECTION and maintenance_work_mem is per autovacuum worker, so the resident peak
    // is a PRODUCT of knobs that live in three different files — and it was never
    // expressed anywhere, in either language. The 2026-07-15 starvation was that product
    // going over the machine; it was diagnosed after the fact by hand, and the fix was a
    // tighter divisor rather than an invariant, so the same class of failure can recur the
    // moment max_connections or a cap moves.
    //
    // WorkMemBytes' own doc comment states the law -- "it must be sized against the
    // connection budget (max_connections x concurrent nodes), never as a flat RAM
    // fraction" -- and then the expression below it is a flat RAM fraction. These members
    // make the stated law computable so a test can hold the knobs to it.

    /// <summary>
    /// RAM deliberately left to the OS: page cache, the ingest client's own managed heap,
    /// and the CLI/runner processes. A quarter is too generous on a large box and too mean
    /// on a small one, so it is RAM/8 within a hard band.
    /// </summary>
    public static long OsReserveBytes => Clamp(TotalPhysicalBytes / 8, 1L << 30, 16L << 30);

    /// <summary>What remains for backend private memory once shared_buffers and the OS
    /// reserve are committed. Backend grants must fit inside THIS, not inside RAM.</summary>
    public static long BackendMemoryBudgetBytes =>
        Math.Max(0, TotalPhysicalBytes - SharedBuffersBytes - OsReserveBytes);

    /// <summary>
    /// work_mem sized the way the law says it should be: the backend budget divided by the
    /// worst-case number of simultaneous grants. Exposed alongside <see cref="WorkMemBytes"/>
    /// rather than replacing it, because changing the emitted value is a tuning decision
    /// that must move the shell, the C# and the parity pins together.
    /// </summary>
    public static long WorkMemBytesFor(int maxConnections, int nodesPerQuery) =>
        Clamp(BackendMemoryBudgetBytes / Math.Max(1L, (long)maxConnections * nodesPerQuery),
              16L << 20, 64L << 20);

    /// <summary>
    /// Worst-case resident bytes across the enumerable grants. A LOWER BOUND on the true
    /// peak: it cannot see a backend taking more than <paramref name="nodesPerQuery"/>
    /// grants, nor the client's own heap. Under-counting is the safe direction for a
    /// guard -- if even this bound exceeds the machine, the configuration is
    /// indefensible without measuring anything.
    /// </summary>
    public static long PeakResidentLowerBoundBytes(
        int maxConnections, int nodesPerQuery, int autovacuumWorkers, long autovacuumWorkMemBytes)
        => PeakResidentLowerBoundBytes(maxConnections, nodesPerQuery, autovacuumWorkers,
                                       autovacuumWorkMemBytes, SharedBuffersBytes, WorkMemBytes);

    /// <summary>
    /// Pure form, every term injected. Exists so the guard can be aimed at a configuration
    /// this host does not currently have -- a historical one, or a proposed one -- because
    /// a bound that can only ever be evaluated against the current machine cannot be shown
    /// to reject anything, and an assertion never demonstrated to fail is decoration.
    /// </summary>
    public static long PeakResidentLowerBoundBytes(
        int maxConnections, int nodesPerQuery, int autovacuumWorkers,
        long autovacuumWorkMemBytes, long sharedBuffersBytes, long workMemBytes)
        => sharedBuffersBytes
         + (long)maxConnections * nodesPerQuery * workMemBytes
         + (long)autovacuumWorkers * autovacuumWorkMemBytes;

    /// <summary>
    /// Fraction of physical RAM the enumerable grants may claim. Not a preference: above
    /// this the OS has no room for page cache and the box starts swapping, which is how
    /// 2026-07-15 ended in a cold power boot rather than in an OOM kill -- swap death
    /// leaves no kill record, which is exactly why it was hard to attribute.
    /// </summary>
    public const double PeakResidentSafeFraction = 0.80;

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
