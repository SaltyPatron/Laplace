using System.Text;

namespace Laplace.Engine.Core;

public static class IngestSizing
{

    // Fallback only — real bytes/record comes from IngestSourceProfile.
    public const int DefaultEstBytesPerRecord = 512;

    /// <summary>
    /// MEASURE bytes/record from the file about to be read, instead of trusting the
    /// per-source constant declared in <c>IngestSourceProfile</c>.
    ///
    /// Those constants are estimates that nothing ever checked against a corpus, and they
    /// are wrong by enough to matter. MEASURED 2026-08-01 on
    /// /vault/Data/Wiktionary/raw-wiktextract-data.jsonl (20.4 GB): the mean over the
    /// first 20,000 records is 6,158 bytes. <c>IngestSourceProfile.Wiktionary</c> declares
    /// 12,000 — 1.95x too high.
    ///
    /// The error is in the slow direction, not the dangerous one: record width is
    /// the denominator of the per-worker memory share, so an over-estimate shrinks
    /// every batch and increases scheduling/probe overhead for the whole corpus.
    ///
    /// By default it samples one machine-derived sequential-I/O window; tests and
    /// diagnostics may request an exact record count. Returns <paramref name="fallback"/>
    /// on unreadable/empty input. Sizing must never be the thing that throws.
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
    /// Per-tuple byte estimate used by the ingest runner's apply gate
    /// (<c>IngestRunner.BytesOf</c>) for entities / physicalities / attestations.
    /// </summary>
    public const int ApplyTupleByteEstimate = 152;

    /// <summary>
    /// Extra apply-cost billed per attestation on top of staged/COPY tuple bytes.
    ///
    /// MUST stay 0 for chess-shaped traffic. MEASURED 2026-08-04: surcharge 2048
    /// shrunk applies to ~220k present merges each and cut committed rate
    /// ~63 g/s → ~21 g/s. Cause: present attestation ids are content-addressed and
    /// shared across games; one large apply collapses duplicate ids to a single
    /// <c>attestation_merge</c> with summed observations, while many small applies
    /// re-merge the same id once per apply — more total merge work, same merge
    /// rows/s (~10–25k/s). Wall-clock needs fewer applies (coalesce), not a
    /// tighter envelope. Speedup belongs in merge throughput / run-scoped fold,
    /// not BytesOf inflation.
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
    /// Per-source ingest plan derived from Intel topology (P/E pools), RAM budget,
    /// and the source byte/compose model. Single entry point for pipeline config.
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
    /// consume the same process/backend memory envelope; tuning them independently
    /// is how fixed powers of two accumulated in the writer.
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
        int EntityPresenceCacheIds,
        int PhysicalityPresenceCacheIds,
        int LadderCacheIds,
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
            + "entity_cache_ids={3} physicality_cache_ids={4} ladder_cache_ids={5} "
            + "reader_proven_ids={6} reader_root_ids={7} text_root_ids={8} "
            + "image_root_ids={9} audio_root_ids={10} cache_bytes_per_owner={11} "
            + "copy_startup_bytes={12}",
            Connections,
            ProbeChunkIds,
            MergeChunkRows,
            EntityPresenceCacheIds,
            PhysicalityPresenceCacheIds,
            LadderCacheIds,
            ReaderProvenCacheIds,
            ReaderRootCacheIds,
            TextRootCacheIds,
            ImageRootCacheIds,
            AudioRootCacheIds,
            CacheBytesPerOwner,
            CopyStartupBytes);
    }

    /// <summary>
    /// Resolve the fold from the same memory and topology inputs as compose/apply.
    /// No throughput cap lives here: the only ceilings are the shared memory envelope
    /// and the CLR/PostgreSQL signed-array index range.
    /// </summary>
    public static ConsensusFoldPlan ResolveConsensusFold(
        int applyPartitions,
        long? workingSetBudgetBytes = null,
        long? flushEnvelopeBytes = null)
    {
        int connections = Math.Max(1, applyPartitions);
        long budget = Math.Max(1, workingSetBudgetBytes ?? ResolveWorkingSetBudgetBytes());
        long envelope = Math.Clamp(
            flushEnvelopeBytes ?? ResolveWorkingSetFlushEnvelopeBytes(), 1, budget);

        // The transit estimate includes both managed/Npgsql parameter objects and
        // PostgreSQL array/slice residency, so all active connections share ONE
        // envelope rather than each receiving an unrelated fixed row count.
        long perConnectionBytes = Math.Max(1, envelope / connections);
        int chunkCells = IntCount(perConnectionBytes
            / MemoryTopology.ConsensusFoldTransitBytesPerCell);

        // This budget is already the fold/mask owner's share of the client domain;
        // compose, apply transit, and exact caches have their own shares in
        // MemoryTopology.WorkingSetResidentOwners. Subtracting those owners again
        // was double-accounting and became an unexplained "-4" throughput limiter.
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
        // writer entity/physicality presence, content ladder, reader proven ids, and
        // reader canonical-root pairs, and text/image/audio root memoization. Reaching
        // capacity only restores the normal DB probe/compose path and cannot lose data.
        const int writerPresenceOwners = 2;
        const int contentLadderOwners = 1;
        const int readerIdentityOwners = 2;
        const int modalityRootOwners = 3;
        const int vendorReuseOwners = 1;
        const int cacheOwners = writerPresenceOwners + contentLadderOwners
            + readerIdentityOwners + modalityRootOwners + vendorReuseOwners;
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
            cacheIds,
            cacheIds,
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
    /// row's actual client/wire/server resident shape. This is the common replacement
    /// for fixed 4K/64K/etc. bulk-operation caps.
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

    private static int IntCount(long value) =>
        (int)Math.Clamp(value, 1, int.MaxValue);

    public static long TotalPhysicalMemoryBytes() => MemoryTopology.TotalPhysicalBytes;

    /// <summary>
    /// Per-worker sequential I/O window. All active I/O workers share one compose
    /// envelope, so file readers no longer each allocate an unrelated 1 MiB buffer.
    /// PostgreSQL/Npgsql's transport page is the forward-progress floor.
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
    /// Largest payload one parser may require as one contiguous managed buffer. It is
    /// bounded by both the compose envelope and the CLR's actual array addressability;
    /// there is no format-specific 64/256 MiB rejection threshold.
    /// </summary>
    public static int ResolveContiguousPayloadBytes() =>
        (int)Math.Max(1, Math.Min(
            (long)Array.MaxLength,
            ResolveWorkingSetFlushEnvelopeBytes()));

    /// <summary>
    /// Working-set apply byte budget — delegated to <see cref="MemoryTopology"/>, the single
    /// RAM authority. The budget is real RAM divided across the topology's simultaneously
    /// resident owners. Historical 1/4-GiB and RAM/16 clamps are gone; COPY now streams and
    /// every managed allocation obeys the runtime's actual array-addressability boundary.
    /// </summary>
    public static long ResolveWorkingSetBudgetBytes() => MemoryTopology.WorkingSetBudgetBytes;

    /// <summary>
    /// Compose-side flush envelope (resident-memory bound that closes a working set before
    /// its builder + content bank are reset) — delegated to <see cref="MemoryTopology"/>.
    /// One apply-partition share of the apply budget so compose flushes continuously and
    /// stays fast; see
    /// <see cref="MemoryTopology.WorkingSetFlushEnvelopeBytes"/> for the rationale.
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
    /// Resolve a full per-source plan from live Intel topology + RAM. Call after
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
    /// Max input records per working set before descent/apply — derived from the RAM budget
    /// and per-source staged-byte model. The live pipeline replaces this pre-compose
    /// estimate with actual native tree capacity as soon as each unit is built.
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
    /// Cheap records (unicode) scale up; fat records (wiktionary, relation triples) scale down.
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
    /// check reads <c>state.InBatch</c>, which only advances inside FlushPending.
    /// Wiktionary with EstComposeUnits=64 resolved probe=32768 (batch×units clamp)
    /// while flush recordCap≈516 — an 8k-line uncapped slice never FlushPending'd
    /// mid-stream and applied as intents=1 / ~281k entity verify (measured 2026-08-06).
    /// </summary>
    public static int ResolveWorkingSetProbeInterval(
        int recordBatchSize, IngestSourceProfile profile, long? flushEnvelopeBytes = null)
    {
        int flushCap = ResolveFlushEnvelopeRecordCap(profile, flushEnvelopeBytes);
        long raw = (long)recordBatchSize * Math.Max(1, profile.EstComposeUnitsPerRecord);
        // Stay at or under flushCap so pending cannot outrun the envelope close.
        return (int)Math.Max(1, Math.Min(raw, flushCap));
    }

    // Presence probes are ROUND-TRIP dominated (~10ms fixed cost each, id
    // arrays are 16 bytes/id); the old [128, 2048] clamp turned big-source
    // descent into thousands of serial 512-id round trips — the tiny-codes /
    // Wiktionary "no progress, never finishes" signature (measured
    // 2026-07-16: continuous 10-12ms probe stream, zero writes). The WS-apply
    // probe already runs 131,072-id chunks through the same functions; match
    // its scale. 32k ids = 512KB parameter — noise.
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
