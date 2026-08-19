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
    /// <summary>Exact storage width of the canonical BLAKE3-128 identity.</summary>
    public const int Hash128Bytes = 16;

    /// <summary>
    /// Resident owners that can coexist with a working-set buffer: one owner per active
    /// apply partition, plus compose, apply metadata, exact caches, and fold/mask state.
    /// The latter four are real simultaneously-live ownership classes, not a tuning value.
    /// This replaces the historical RAM/16 plus 4-GiB clamp: a 12-partition machine still
    /// has sixteen owners, while another topology scales instead of inheriting its number.
    /// </summary>
    public static int WorkingSetResidentOwners =>
        checked(CpuTopology.ResolveApplyPartitions() + 4);

    private static readonly Lazy<long> LazyTotalPhysical =
        new(DetectTotalPhysicalBytes, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static long? TestTotalPhysicalOverride;

    /// <summary>Real installed physical RAM in bytes (not the GC heap ceiling).</summary>
    public static long TotalPhysicalBytes => TestTotalPhysicalOverride ?? LazyTotalPhysical.Value;

    public static string DetectionSource { get; private set; } = "uninitialized";

    /// <summary>
    /// Byte budget for one working-set apply. Every simultaneously-live owner receives
    /// one equal RAM share. The only floor is enough PostgreSQL transport pages for the
    /// active apply partitions; there is no corpus- or machine-specific byte ceiling.
    /// </summary>
    public static long WorkingSetBudgetBytes => Math.Max(
        (long)CopyStartupBytesPerConnection * CpuTopology.ResolveApplyPartitions(),
        TotalPhysicalBytes / WorkingSetResidentOwners);

    /// <summary>
    /// COMPOSE-side flush envelope — the resident-memory ceiling for ONE working set
    /// before it is closed, applied, and its builder + content bank reset. This is
    /// Deliberately below <see cref="WorkingSetBudgetBytes"/>. Holding millions of
    /// deferred tier trees plus a giant
    /// content bank in a single working set collapses compose throughput (MEASURED
    /// 30k → 1.8k rec/s as a ~4 GiB set filled with ~3M records before flushing) and
    /// spikes GC. The default is one apply-partition share of the working-set budget,
    /// so the exact topology controls the flush width. Tunable via
    /// <c>LAPLACE_WS_FLUSH_MB</c> for an explicit operator experiment.
    /// </summary>
    public static long WorkingSetFlushEnvelopeBytes => ResolveFlushEnvelope();

    private static long ResolveFlushEnvelope()
    {
        long apply = WorkingSetBudgetBytes;
        string? env = Environment.GetEnvironmentVariable("LAPLACE_WS_FLUSH_MB");
        if (!string.IsNullOrWhiteSpace(env)
            && long.TryParse(env.Trim(), out long mb) && mb > 0)
            return Math.Clamp(mb << 20, CopyStartupBytesPerConnection, apply);

        return Math.Max(
            CopyStartupBytesPerConnection,
            apply / CpuTopology.ResolveApplyPartitions());
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
    /// Conservative transient resident cost per cell while a fold chunk crosses
    /// managed byte arrays, the Npgsql write buffer, PostgreSQL arrays, native cell-id
    /// construction, and per-type slices. This is byte accounting, not a row-count cap:
    /// 3 varlena ids/references + 4 scalar arrays + wire copy + server arrays/slices.
    /// </summary>
    public const int ConsensusFoldTransitBytesPerCell = 512;

    /// <summary>
    /// Conservative resident cost for one HashSet entry carrying two Hash128 values,
    /// including slot/bucket overhead. Used to derive the run-scoped mask dedup capacity
    /// from the compose envelope instead of fixing it at 8,388,608 entries.
    /// </summary>
    public const int ConsensusMaskPairResidentBytes = 64;

    /// <summary>
    /// Conservative resident cost of one Hash128 key in a ConcurrentDictionary,
    /// including the key/value, node, bucket, and allocator overhead. Run-scoped
    /// presence and content-ladder caches are sized from bytes with this value;
    /// they never own an unrelated fixed entry cap.
    /// </summary>
    public const int ConcurrentHash128ResidentBytes = 64;

    /// <summary>
    /// Conservative resident cost of one Hash128-to-Hash128 cache entry, including
    /// two values plus dictionary node/bucket and allocator overhead.
    /// </summary>
    public const int ConcurrentHash128PairResidentBytes = 96;

    /// <summary>
    /// Worst-case transient bytes per presence-probe id. The keyed attestation
    /// probe carries three Hash128 arrays (id/type/subject), plus managed byte-array
    /// objects, Npgsql framing, wire data, and PostgreSQL array storage.
    /// </summary>
    public const int PresenceProbeTransitBytesPerId = 256;

    /// <summary>
    /// Transient bytes per present-attestation merge row: three Hash128 arrays,
    /// two int64 arrays, one timestamp array, and their client/wire/server copies.
    /// </summary>
    public const int AttestationMergeTransitBytesPerRow = 512;

    /// <summary>
    /// Resident/wire allowance for one ingest-file journal event: the event object,
    /// file/source/status strings, array slots, Npgsql framing, and PostgreSQL array
    /// values. Journal batching divides a real byte envelope by this measured shape;
    /// it does not own an unrelated event-count cap.
    /// </summary>
    public const int FileJournalTransitBytesPerEvent = 512;

    /// <summary>
    /// Exact native tier-tree structure-of-arrays width per allocated node:
    /// tier(1), six uint32 arrays(24), id(16), coord(32), and Hilbert(16).
    /// Text bytes and the managed string key are accounted separately by callers.
    /// </summary>
    public const int TierTreeResidentBytesPerCapacity = 89;

    /// <summary>
    /// Minimum useful payload for another COPY connection. 8192 is PostgreSQL's
    /// physical page size and Npgsql's default write buffer: below one buffer/page
    /// there is no payload to amortize an additional connection and transaction.
    /// This is a transport unit, not a corpus row-count threshold.
    /// </summary>
    public const int CopyStartupBytesPerConnection = 8 * 1024;

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
