using System.Text;

namespace Laplace.Engine.Core;

public static class IngestSizing
{

    // Bytes/record used when neither a source profile nor a measurement supplies one.
    public const int DefaultEstBytesPerRecord = 512;

    /// <summary>
    /// Mean UTF-8 bytes per non-blank line (newline included) of the file about to be
    /// read. Record width is the denominator of the per-worker memory share, so this
    /// measured value sizes batches from the artifact itself rather than from the
    /// profile's declared estimate.
    ///
    /// Samples one sequential-I/O window by default, or exactly
    /// <paramref name="sampleRecords"/> lines when given. Returns
    /// <paramref name="fallback"/> on missing, unreadable, or empty input; sizing never throws.
    /// </summary>
    public static int MeasureBytesPerRecord(
        string path,
        int? sampleRecords = null,
        int fallback = DefaultEstBytesPerRecord)
    {
        if (string.IsNullOrWhiteSpace(path) || sampleRecords is <= 0) return fallback;
        try
        {
            if (!File.Exists(path)) return fallback;

            long bytes = 0;
            int records = 0;
            long sampleByteBudget = ResolveSequentialIoBufferBytes();
            using var reader = new StreamReader(path);
            while ((sampleRecords.HasValue
                    ? records < sampleRecords.Value
                    : bytes < sampleByteBudget)
                && reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;          // blank separators are not records
                bytes += Encoding.UTF8.GetByteCount(line) + 1;   // + the newline it cost
                records++;
            }

            if (records == 0 || bytes <= 0) return fallback;
            long mean = bytes / records;
            return mean is > 0 and <= int.MaxValue ? (int)mean : fallback;
        }
        catch (IOException) { return fallback; }
        catch (UnauthorizedAccessException) { return fallback; }
    }

    /// <summary>
    /// Per-row byte estimate the apply gate bills for each entity, physicality, and
    /// attestation row.
    /// </summary>
    public const int ApplyTupleByteEstimate = 152;

    /// <summary>
    /// Wire ceiling for one array-carrying statement (bytea[]/int8[] parameters
    /// marshalled in a single Bind message). PostgreSQL's frontend protocol rejects a
    /// message whose declared length reaches ~1 GiB and resets the connection; this
    /// ceiling leaves headroom for protocol framing plus the server-side unnest/hash of
    /// one chunk. Statements whose
    /// parameters exceed this MUST chunk and loop inside the caller's transaction;
    /// chunking changes transport grain only, never the admitted set.
    /// </summary>
    public const long MaxArrayStatementWireBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Extra apply cost billed per attestation on top of staged/COPY row bytes. Zero:
    /// attestation ids are content-addressed and recur across inputs, and one large
    /// apply collapses duplicate ids into a single merge with summed observations,
    /// whereas inflating the per-attestation cost splits the same ids across more
    /// applies and re-merges each id once per apply.
    /// </summary>
    public const int AttestationApplySurchargeBytes = 0;

    /// <summary>
    /// Apply-gate byte estimate: staged/COPY bytes plus attestation merge surcharge
    /// so <see cref="MemoryTopology.WorkingSetFlushEnvelopeBytes"/> bounds merge work.
    /// </summary>
    public static long EstimateApplyGateBytes(
        int entityCount,
        int physicalityCount,
        int attestationCount,
        long trajectoryBytes,
        long intentStageTupleBytes,
        int intentStageAttestationCount)
    {
        long att = (long)attestationCount + intentStageAttestationCount;
        long bytes =
            ((long)entityCount + physicalityCount + attestationCount) * ApplyTupleByteEstimate
            + Math.Max(0, trajectoryBytes)
            + Math.Max(0, intentStageTupleBytes);
        if (att > 0)
            bytes += att * AttestationApplySurchargeBytes;
        return bytes;
    }

    public sealed record Plan(
        int RecordBatchSize,
        int ProbeChunkSize,
        int CommitRows,
        int DecomposeChannelCapacity,
        int FileWorkerChannelDepth,
        int MaxIntentsPerCommit,
        long RowBudget)
    {
        public int IntentsPerCommit =>
            CommitRows / Math.Max(1, RecordBatchSize) + 1;
    }

    /// <summary>
    /// Per-source ingest plan derived from CPU topology (P/E pools), RAM budget,
    /// and the source byte/compose model.
    /// </summary>
    public sealed record SourcePlan(
        long WorkingSetBudgetBytes,
        long TotalMemoryBytes,
        int RecordBatchSize,
        int CommitRows,
        int WorkingSetRecordCap,
        int WorkingSetProbeInterval,
        int ComposeWorkers,
        int FileWorkers,
        int IoWorkersAvailable,
        int ApplyPartitions,
        int ProbeChunkSize,
        int DecomposeChannelCapacity,
        int MaxIntentsPerCommit,
        long RowBudget)
    {
        public void Log(string sourceLabel)
        {
            Console.Error.WriteLine(
                "ingest_source_sizing: source={0} budget_bytes={1} total_ram_bytes={2} "
                + "record_batch={3} commit_rows={4} ws_record_cap={5} ws_probe={6} "
                + "compose_workers={7} file_workers={8} io_workers_available={9} "
                + "apply_mode=set_coordinator apply_partitions={10} "
                + "probe_chunk={11} decompose_channel={12} max_intents={13} row_budget={14}",
                sourceLabel,
                WorkingSetBudgetBytes,
                TotalMemoryBytes,
                RecordBatchSize,
                CommitRows,
                WorkingSetRecordCap,
                WorkingSetProbeInterval,
                ComposeWorkers,
                FileWorkers,
                IoWorkersAvailable,
                ApplyPartitions,
                ProbeChunkSize,
                DecomposeChannelCapacity,
                MaxIntentsPerCommit,
                RowBudget);
        }
    }

    /// <summary>
    /// Machine-derived sizing for the consensus fold. These are one plan because
    /// chunk width, connection fanout, retained deltas, and mask-pair residency
    /// consume the same process/backend memory envelope.
    /// </summary>
    public sealed record ConsensusFoldPlan(
        int Connections,
        int ChunkCells,
        int PipelineDepth,
        int DeltaCapacityCells,
        int MaskPairCapacity)
    {
        public void Log() => Console.Error.WriteLine(
            "consensus_fold_sizing: connections={0} chunk_cells={1} "
            + "pipeline_depth={2} delta_capacity_cells={3} "
            + "mask_pair_capacity={4} transit_bytes_per_cell={5} mask_pair_bytes={6}",
            Connections,
            ChunkCells,
            PipelineDepth,
            DeltaCapacityCells,
            MaskPairCapacity,
            MemoryTopology.ConsensusFoldTransitBytesPerCell,
            MemoryTopology.ConsensusMaskPairResidentBytes);
    }

    /// <summary>
    /// Machine-derived sizing for working-set verification/COPY/merge and the
    /// run-scoped exact caches. All counts are byte budgets divided by the actual
    /// transport/resident width; none are corpus-tuned row literals.
    /// </summary>
    public sealed record ApplyIoPlan(
        int Connections,
        int ProbeChunkIds,
        int MergeChunkRows,
        int ReaderProvenCacheIds,
        int ReaderRootCacheIds,
        int TextRootCacheIds,
        int ImageRootCacheIds,
        int AudioRootCacheIds,
        long CacheBytesPerOwner,
        int CopyStartupBytes)
    {
        public void Log() => Console.Error.WriteLine(
            "apply_io_sizing: connections={0} probe_chunk_ids={1} merge_chunk_rows={2} "
            + "reader_proven_ids={3} reader_root_ids={4} text_root_ids={5} "
            + "image_root_ids={6} audio_root_ids={7} cache_bytes_per_owner={8} "
            + "copy_startup_bytes={9}",
            Connections,
            ProbeChunkIds,
            MergeChunkRows,
            ReaderProvenCacheIds,
            ReaderRootCacheIds,
            TextRootCacheIds,
            ImageRootCacheIds,
            AudioRootCacheIds,
            CacheBytesPerOwner,
            CopyStartupBytes);
    }

    /// <summary>
    /// Connections the fold cedes from its apply-partition width. Zero: the pool
    /// provisions the fold's slack itself (<see cref="PostgresResourcePlan"/>), so the
    /// fold runs at full apply width.
    /// </summary>
    public const int ConsensusFoldPoolHeadroom = 0;

    /// <summary>
    /// Resolve the fold from the same memory and topology inputs as compose/apply.
    /// No throughput cap lives here: the only ceilings are the shared memory envelope,
    /// the fold's share of the ingest connection pool, and the CLR/PostgreSQL
    /// signed-array index range.
    /// </summary>
    public static ConsensusFoldPlan ResolveConsensusFold(
        int applyPartitions,
        long? workingSetBudgetBytes = null,
        long? flushEnvelopeBytes = null)
    {
        // The fold shares the ingest pool with the apply COPY fan and runs the fold of
        // one batch while the next batch probes/COPYs. The pool provisions the fold's
        // slack, so the fold takes full apply width and back-pressures on its own
        // connection gate rather than exhausting the pool.
        int connections = Math.Max(1, applyPartitions - ConsensusFoldPoolHeadroom);
        long budget = Math.Max(1, workingSetBudgetBytes ?? ResolveWorkingSetBudgetBytes());
        long envelope = Math.Clamp(
            flushEnvelopeBytes ?? ResolveWorkingSetFlushEnvelopeBytes(), 1, budget);

        // The transit estimate includes both managed/Npgsql parameter objects and
        // PostgreSQL array/slice residency, so all active connections share ONE
        // envelope rather than each receiving an unrelated fixed row count.
        long perConnectionBytes = Math.Max(1, envelope / connections);
        int chunkCells = IntCount(perConnectionBytes
            / MemoryTopology.ConsensusFoldTransitBytesPerCell);

        // The budget is already the fold/mask owner's share of the client domain;
        // compose, apply transit, and exact caches hold their own shares in
        // MemoryTopology.WorkingSetResidentOwners, so none is subtracted here.
        int pipelineDepth = IntCount(budget / envelope);

        int deltaCapacityCells = IntCount(envelope
            / MemoryTopology.ConsensusFoldBytesPerRelation);
        int maskPairCapacity = IntCount(envelope
            / MemoryTopology.ConsensusMaskPairResidentBytes);

        return new ConsensusFoldPlan(
            connections,
            chunkCells,
            pipelineDepth,
            deltaCapacityCells,
            maskPairCapacity);
    }

    /// <summary>
    /// Allocate the live connection topology across type runs without a row-count
    /// threshold. Every run gets one lane; spare connections repeatedly split the
    /// run with the largest current per-lane load. A run never gets more lanes than
    /// cells. When there are more types than connections, the global connection
    /// gate schedules their one-lane jobs work-conservingly.
    /// </summary>
    public static int[] AllocateFoldRunWidths(IReadOnlyList<int> lengths, int connections)
    {
        ArgumentNullException.ThrowIfNull(lengths);
        if (lengths.Count == 0) return [];
        connections = Math.Max(1, connections);

        var widths = new int[lengths.Count];
        for (int i = 0; i < lengths.Count; i++)
        {
            if (lengths[i] <= 0)
                throw new ArgumentOutOfRangeException(nameof(lengths), "fold runs must be non-empty");
            widths[i] = 1;
        }

        int spare = Math.Max(0, connections - lengths.Count);
        while (spare-- > 0)
        {
            int best = -1;
            int bestLoad = 0;
            for (int i = 0; i < lengths.Count; i++)
            {
                if (widths[i] >= lengths[i]) continue;
                int load = (lengths[i] + widths[i] - 1) / widths[i];
                if (load > bestLoad)
                {
                    best = i;
                    bestLoad = load;
                }
            }
            if (best < 0) break;
            widths[best]++;
        }
        return widths;
    }

    /// <summary>
    /// Resolve apply IO from the same budget/envelope/topology inputs as compose and
    /// consensus fold. One cache envelope is split across the two presence maps and
    /// the ladder ledger; simultaneous probe/merge connections share one transit
    /// envelope. Counts are consequences of byte width, not tuning knobs.
    /// </summary>
    public static ApplyIoPlan ResolveApplyIo(
        int applyPartitions,
        long? workingSetBudgetBytes = null,
        long? flushEnvelopeBytes = null)
    {
        int connections = Math.Max(1, applyPartitions);
        long budget = Math.Max(1, workingSetBudgetBytes ?? ResolveWorkingSetBudgetBytes());
        long envelope = Math.Clamp(
            flushEnvelopeBytes ?? ResolveWorkingSetFlushEnvelopeBytes(), 1, budget);
        long perConnectionBytes = Math.Max(1, envelope / connections);

        int probeChunkIds = IntCount(perConnectionBytes
            / MemoryTopology.PresenceProbeTransitBytesPerId);
        int mergeChunkRows = IntCount(perConnectionBytes
            / MemoryTopology.AttestationMergeTransitBytesPerRow);

        // The cache envelope is shared by every run-long exact acceleration map:
        // reader proven ids and canonical-root pairs, text/image/audio root
        // memoization, and vendor reuse. Reaching capacity only restores the normal
        // DB probe/compose path and cannot lose data.
        const int readerIdentityOwners = 2;
        const int modalityRootOwners = 3;
        const int vendorReuseOwners = 1;
        const int cacheOwners = readerIdentityOwners + modalityRootOwners + vendorReuseOwners;
        long cacheBytesPerMap = Math.Max(1, envelope / cacheOwners);
        int cacheIds = IntCount(cacheBytesPerMap
            / MemoryTopology.ConcurrentHash128ResidentBytes);
        int rootCacheIds = IntCount(cacheBytesPerMap
            / MemoryTopology.ConcurrentHash128PairResidentBytes);

        return new ApplyIoPlan(
            connections,
            probeChunkIds,
            mergeChunkRows,
            cacheIds,
            rootCacheIds,
            rootCacheIds,
            rootCacheIds,
            rootCacheIds,
            cacheBytesPerMap,
            MemoryTopology.CopyStartupBytesPerConnection);
    }

    /// <summary>
    /// Number of COPY connections justified by the finalized payload. Every extra
    /// connection must own at least one row and one transport buffer/page of bytes;
    /// the upper bound is the machine's apply topology.
    /// </summary>
    public static int ResolveCopyConnections(
        int rowCount, long payloadBytes, int applyPartitions, int copyStartupBytes)
    {
        if (rowCount <= 0 || payloadBytes <= 0) return 1;
        long byPayload = (payloadBytes + Math.Max(1, copyStartupBytes) - 1)
            / Math.Max(1, copyStartupBytes);
        return (int)Math.Max(1, Math.Min(
            Math.Min((long)Math.Max(1, applyPartitions), rowCount), byPayload));
    }

    /// <summary>
    /// Row capacity for one array/COPY-style transit operation. Active connections
    /// divide a single process envelope; <paramref name="transitBytesPerRow"/> is the
    /// row's actual client/wire/server resident shape.
    /// </summary>
    public static int ResolveTransitBatchRows(
        int transitBytesPerRow,
        int? activeConnections = null,
        long? flushEnvelopeBytes = null)
    {
        int connections = Math.Max(1,
            activeConnections ?? IngestTopology.Current.ApplyPartitions);
        long envelope = Math.Max(1,
            flushEnvelopeBytes ?? ResolveWorkingSetFlushEnvelopeBytes());
        return IntCount(envelope / connections / Math.Max(1, transitBytesPerRow));
    }

    /// <summary>
    /// Maximum expanded rows owned by one apply transaction. The limiting domain
    /// is one PostgreSQL backend's private executor grant, not the larger client
    /// working-set envelope.
    /// </summary>
    public static int ResolveApplyTransactionRows() => IntCount(
        PostgresResourcePlan.Current.WorkMemBytes
        / MemoryTopology.AttestationMergeTransitBytesPerRow);

    private static int IntCount(long value) =>
        (int)Math.Clamp(value, 1, int.MaxValue);

    public static long TotalPhysicalMemoryBytes() => MemoryTopology.TotalPhysicalBytes;

    /// <summary>
    /// Per-worker sequential I/O window: the compose flush envelope divided across the
    /// active I/O workers, floored at one COPY transport page and capped at the CLR
    /// array limit.
    /// </summary>
    public static int ResolveSequentialIoBufferBytes(int? ioWorkers = null)
    {
        int workers = Math.Max(1, ioWorkers ?? CpuTopology.ResolveIoBoundWorkers());
        long bytes = ResolveWorkingSetFlushEnvelopeBytes() / workers;
        return (int)Math.Clamp(
            bytes,
            MemoryTopology.CopyStartupBytesPerConnection,
            Array.MaxLength);
    }

    /// <summary>
    /// Largest payload one parser may require as one contiguous managed buffer, bounded
    /// by the compose envelope and the CLR's array addressability.
    /// </summary>
    public static int ResolveContiguousPayloadBytes() =>
        (int)Math.Max(1, Math.Min(
            (long)Array.MaxLength,
            ResolveWorkingSetFlushEnvelopeBytes()));

    /// <summary>
    /// Working-set apply byte budget from <see cref="MemoryTopology"/>: physical RAM
    /// divided across the topology's simultaneously resident owners.
    /// </summary>
    public static long ResolveWorkingSetBudgetBytes() => MemoryTopology.WorkingSetBudgetBytes;

    /// <summary>
    /// Compose-side flush envelope — the resident-memory bound that closes a working set
    /// before its builder and content bank are reset — from
    /// <see cref="MemoryTopology.WorkingSetFlushEnvelopeBytes"/>, divided across
    /// <paramref name="concurrentWorkingSets"/>.
    /// </summary>
    public static long ResolveWorkingSetFlushEnvelopeBytes(int concurrentWorkingSets = 1) =>
        Math.Max(1, MemoryTopology.WorkingSetFlushEnvelopeBytes
            / Math.Max(1, concurrentWorkingSets));

    /// <summary>
    /// Hard record ceiling for one working set derived from the flush envelope and the
    /// per-source byte model — the inverse of <see cref="EstimateWorkingSetBytes"/>, so it
    /// agrees with the byte-estimate close check. Bounds the set even when
    /// <see cref="SubstrateChangeBuilder.StagedBytesEstimate"/> under-reports resident cost.
    /// </summary>
    public static int ResolveFlushEnvelopeRecordCap(
        IngestSourceProfile profile, long? flushEnvelopeBytes = null)
    {
        long envelope = flushEnvelopeBytes ?? ResolveWorkingSetFlushEnvelopeBytes();
        long cap = envelope / Math.Max(1, profile.UncomposedResidentBytesPerRecord);
        return (int)Math.Clamp(cap, 1, int.MaxValue);
    }

    /// <summary>
    /// Resolve a full per-source plan from live CPU topology and RAM. Call after
    /// <see cref="IngestTopology.EnsureReady"/> so worker pools are initialized.
    /// </summary>
    public static SourcePlan ResolveForSource(
        IngestSourceProfile profile,
        int? recordBatchOverride = null,
        long? workingSetBudgetBytes = null)
    {
        var topo = IngestTopology.Current;
        long budget = workingSetBudgetBytes ?? ResolveWorkingSetBudgetBytes();
        long ram = TotalPhysicalMemoryBytes();

        var plan = Resolve(
            topo.PerformanceCoreCount,
            topo.FileWorkers,
            topo.ApplyPartitions,
            recordBatchOverride: recordBatchOverride,
            profile: profile,
            workingSetBudgetBytes: budget,
            composeWorkers: topo.ComposeWorkers);

        int batch = plan.RecordBatchSize;
        return new SourcePlan(
            budget,
            ram,
            batch,
            plan.CommitRows,
            ResolveFlushEnvelopeRecordCap(profile),
            ResolveWorkingSetProbeInterval(batch, profile),
            topo.ComposeWorkers,
            topo.FileWorkers,
            topo.IoWorkersAvailable,
            topo.ApplyPartitions,
            plan.ProbeChunkSize,
            plan.DecomposeChannelCapacity,
            plan.MaxIntentsPerCommit,
            plan.RowBudget);
    }

    /// <summary>
    /// Max input records per working set before apply, from the smaller of the RAM budget
    /// and the flush envelope over the per-source resident-byte model. This is a
    /// pre-compose estimate; the pipeline sizes each built unit by its native tree capacity.
    /// </summary>
    public static int ResolveWorkingSetRecordCap(
        IngestSourceProfile profile, long? workingSetBudgetBytes = null)
    {
        long envelope = Math.Min(
            workingSetBudgetBytes ?? ResolveWorkingSetBudgetBytes(),
            ResolveWorkingSetFlushEnvelopeBytes());
        return ResolveFlushEnvelopeRecordCap(profile, envelope);
    }

    /// <summary>
    /// Working-set memory estimate: staged builder bytes plus deferred compose trees
    /// (tier trees / grammar ASTs held in WorkingSetDeferredBatch that
    /// SubstrateChangeBuilder.StagedBytesEstimate does not count).
    /// </summary>
    public static long EstimateWorkingSetBytes(
        long recordsInSet, long stagedBuilderBytes, IngestSourceProfile profile) =>
        stagedBuilderBytes
        + checked(recordsInSet * profile.UncomposedResidentBytesPerRecord);

    public static Plan Resolve(
        int performanceCoreCount,
        int fileWorkers,
        int applyPartitions,
        int? recordBatchOverride = null,
        int? commitRowsOverride = null,
        IngestSourceProfile? profile = null,
        long? workingSetBudgetBytes = null,
        int composeWorkers = 1)
    {
        profile ??= IngestSourceProfile.Default;

        int batch = recordBatchOverride
            ?? ResolveRecordBatch(
                performanceCoreCount,
                profile.EstBytesPerRecord,
                profile.EstComposeUnitsPerRecord,
                composeWorkers,
                workingSetBudgetBytes,
                profile.ResidentBytesPerComposeUnit);
        int probe = ResolveProbeChunk(applyPartitions);

        int commit = commitRowsOverride
            ?? ResolveCommitRows(batch, applyPartitions, profile, workingSetBudgetBytes);

        int maxIntents = ResolveMaxIntentsPerCommit(batch, commit, commitRowsOverride);

        // One backpressure slot per active pipeline actor. Queue depth follows the
        // actual compose/file/apply topology instead of a fixed waves multiplier.
        int decomposeChan = checked(Math.Max(1, composeWorkers)
            + Math.Max(1, fileWorkers) + Math.Max(1, applyPartitions));
        int fileChan = checked(Math.Max(1, fileWorkers) + Math.Max(1, applyPartitions));

        long rowBudget = (long)Math.Max(commit, batch) * decomposeChan;

        return new Plan(batch, probe, commit, decomposeChan, fileChan, maxIntents, rowBudget);
    }

    /// <summary>
    /// Record batch from RAM budget, per-record bytes, P-core count, and compose parallelism.
    /// Narrow records yield larger batches; wide records or many compose units per record
    /// yield smaller ones.
    /// </summary>
    public static int ResolveRecordBatch(
        int performanceCoreCount,
        int estBytesPerRecord = DefaultEstBytesPerRecord,
        int estComposeUnits = 1,
        int composeWorkers = 1,
        long? workingSetBudgetBytes = null,
        int? residentBytesPerComposeUnit = null)
    {
        _ = performanceCoreCount; // topology is represented by composeWorkers
        long budget = workingSetBudgetBytes ?? ResolveWorkingSetBudgetBytes();
        long envelope = Math.Min(budget, ResolveWorkingSetFlushEnvelopeBytes());
        long residentBytes = (long)Math.Max(1,
                residentBytesPerComposeUnit ?? estBytesPerRecord)
            * Math.Max(1, estComposeUnits);
        long perWorkerBytes = Math.Max(1, envelope / Math.Max(1, composeWorkers));

        // One batch per compose worker fits in the shared envelope, including the
        // parsed-record plus declared deferred residency. There are no source classes,
        // powers-of-two bands, or hidden minimum/maximum batch sizes.
        long perRecordBytes = checked((long)Math.Max(1, estBytesPerRecord) + residentBytes);
        long records = perWorkerBytes / Math.Max(1, perRecordBytes);
        return IntCount(records);
    }

    /// <summary>
    /// Commit row budget from working-set RAM, capped by the pipeline-derived wave size.
    /// </summary>
    public static int ResolveCommitRows(
        int recordBatch,
        int applyPartitions,
        IngestSourceProfile profile,
        long? workingSetBudgetBytes = null)
    {
        _ = recordBatch;
        _ = applyPartitions;
        long budget = workingSetBudgetBytes ?? ResolveWorkingSetBudgetBytes();
        long envelope = Math.Min(budget, ResolveWorkingSetFlushEnvelopeBytes());
        return ResolveFlushEnvelopeRecordCap(profile, envelope);
    }

    /// <summary>
    /// How many records accumulate in <c>pending</c> before
    /// <c>FlushPending</c> runs. MUST be ≤ the compose flush record cap: the close
    /// check reads <c>state.InBatch</c>, which only advances inside FlushPending, so a
    /// larger interval would let pending records outrun the envelope close.
    /// </summary>
    public static int ResolveWorkingSetProbeInterval(
        int recordBatchSize, IngestSourceProfile profile, long? flushEnvelopeBytes = null)
    {
        int flushCap = ResolveFlushEnvelopeRecordCap(profile, flushEnvelopeBytes);
        long raw = (long)recordBatchSize * Math.Max(1, profile.EstComposeUnitsPerRecord);
        // Stay at or under flushCap so pending cannot outrun the envelope close.
        return (int)Math.Max(1, Math.Min(raw, flushCap));
    }

    // Presence probes are round-trip dominated at 16 bytes per id, so the chunk is the
    // apply-IO transit share per connection rather than a small fixed id count.
    public static int ResolveProbeChunk(int applyPartitions = 1) =>
        ResolveApplyIo(Math.Max(1, applyPartitions)).ProbeChunkIds;

    public static int ResolveMaxIntentsPerCommit(
        int recordBatch, int commitRowBudget, int? commitRowsOverride = null)
    {
        int budget = commitRowsOverride ?? commitRowBudget;
        if (budget <= 0) return 1;
        return IntCount((budget + (long)Math.Max(1, recordBatch) - 1)
            / Math.Max(1, recordBatch));
    }

    public static void LogPlan(Plan plan)
    {
        Console.Error.WriteLine(
            "ingest_sizing: total_ram_bytes={0} working_set_budget_bytes={1} record_batch={2} "
            + "probe_chunk={3} commit_rows={4} decompose_channel={5} file_channel={6} "
            + "max_intents_per_commit={7} row_budget={8}",
            TotalPhysicalMemoryBytes(),
            ResolveWorkingSetBudgetBytes(),
            plan.RecordBatchSize,
            plan.ProbeChunkSize,
            plan.CommitRows,
            plan.DecomposeChannelCapacity,
            plan.FileWorkerChannelDepth,
            plan.MaxIntentsPerCommit,
            plan.RowBudget);
    }
}
