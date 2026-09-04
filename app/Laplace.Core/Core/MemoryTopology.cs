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
    /// Byte budget for one working-set apply. Every simultaneously-live client owner
    /// receives one equal share of PostgresResourcePlan's client domain; PostgreSQL shared
    /// cache/private backends and the OS page cache are therefore not promised to the
    /// ingest heap a second time. The only floor is enough transport pages for active apply
    /// partitions; there is no corpus- or machine-specific byte ceiling.
    /// </summary>
    public static long WorkingSetBudgetBytes => Math.Max(
        (long)CopyStartupBytesPerConnection * CpuTopology.ResolveApplyPartitions(),
        PostgresResourcePlan.Current.ClientBudgetBytes / WorkingSetResidentOwners);

    /// <summary>
    /// Resident-memory ceiling for ONE working set before it is closed and handed to
    /// apply. <see cref="WorkingSetBudgetBytes"/> is already one working-set owner's
    /// share of the client-memory domain. Dividing that share by apply parallelism here
    /// and then dividing it by the same connection fan again in ResolveApplyIo /
    /// ResolveConsensusFold made the hot path scale as 1/p²: on a 12-P-core host one
    /// logical working set was cut to 1/12 of its owned memory before its 12 database
    /// lanes split it again. That produced tens-of-thousands-row executor calls against
    /// million-row sets and recreated RBAR as repeated medium packets.
    ///
    /// Parallel connection transit is accounted when the apply/fold plan divides this
    /// envelope among its live connections. When several independent working sets are
    /// genuinely resident at once, callers use
    /// IngestSizing.ResolveWorkingSetFlushEnvelopeBytes(concurrentWorkingSets) to divide
    /// this owner share exactly once across those sets.
    /// </summary>
    public static long WorkingSetFlushEnvelopeBytes => WorkingSetBudgetBytes;

    // ---- Postgres memory GUC derivations (single source for tune-pg) --------------------
    // All are functions of physical RAM. tune-pg emits these; nothing hardcodes a GB literal.

    /// <summary>PostgreSQL's shared-cache domain; no machine-size ceiling.</summary>
    public static long SharedBuffersBytes => PostgresResourcePlan.Current.SharedBuffersBytes;

    /// <summary>Planner-visible cache: PostgreSQL shared cache plus OS page cache.</summary>
    public static long EffectiveCacheSizeBytes => PostgresResourcePlan.Current.EffectiveCacheSizeBytes;

    /// <summary>One backend-private owner share for index/vacuum maintenance.</summary>
    public static long MaintenanceWorkMemBytes => PostgresResourcePlan.Current.MaintenanceWorkMemBytes;

    /// <summary>Executor half of one accounted backend-private owner share.</summary>
    public static long WorkMemBytes => PostgresResourcePlan.Current.WorkMemBytes;

    /// <summary>
    /// Approx resident bytes one accumulated consensus relation holds in the client-side fold
    /// dictionary: a (3×16B) key + the Acc state + ConcurrentDictionary node/bucket overhead.
    ///
    /// MEASURED 2026-09-04 (FoldMemoryTopologyMeasurementTests): the current accumulator
    /// shape -- Dictionary&lt;(Hash128,Hash128,Hash128?), Delta&gt; with an inline first
    /// rating period, optional overflow dictionary reference, and aggregate totals -- costs
    /// <b>125 bytes/entry</b> over 200,000 entries on the production runner. This constant
    /// is 224, so it remains 1.79x conservative without halving useful accumulator capacity.
    ///
    /// That is not free: accumulatorCapacity = budget / this, so the fold accumulator holds
    /// 44% of what memory actually allows and flushes correspondingly more often, and each
    /// flush lands on consensus.upsert_type -- the most expensive statement in the ingest
    /// (3,189s over 1,058 calls on the live foundation seed, against 1,121s for all COPY
    /// combined). Lowering it toward the measurement is a real throughput lever and is NOT
    /// taken here: it needs an A/B on a cluster, not a guess swapped for a guess.
    ///
    /// The test pins both directions -- below the measurement is an under-reserved envelope,
    /// above 2x is memory reserved for nothing.
    /// </summary>
    public const int ConsensusFoldBytesPerRelation = 224;

    /// <summary>
    /// Conservative transient resident cost per cell while a fold chunk crosses
    /// managed byte arrays, the Npgsql write buffer, PostgreSQL arrays, native cell-id
    /// construction, and per-type slices. This is byte accounting, not a row-count cap:
    /// 3 varlena ids/references + 4 scalar arrays + wire copy + server arrays/slices.
    ///
    /// UNMEASURED, and it divides the envelope to produce chunkCells -- the number of cells
    /// one consensus_upsert carries. FoldMemoryTopologyMeasurementTests pins only that it
    /// exceeds the resident cost above, because the wire portion cannot be measured from
    /// managed heap accounting alone. Measuring it needs instrumentation on a live fold.
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
    /// <summary>Combined ingest/client and OS page-cache memory domains.</summary>
    public static long OsReserveBytes =>
        PostgresResourcePlan.Current.ClientBudgetBytes
        + PostgresResourcePlan.Current.OsPageCacheBudgetBytes;

    /// <summary>What remains for backend private memory once shared_buffers and the OS
    /// reserve are committed. Backend grants must fit inside THIS, not inside RAM.</summary>
    public static long BackendMemoryBudgetBytes =>
        PostgresResourcePlan.Current.BackendPrivateBudgetBytes;

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
