using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>Stage 1 output — one logical record per yield. Tree-sitter / parser stops here.</summary>
public interface IRecordStream<TRecord>
{
    IAsyncEnumerable<TRecord> RecordsAsync(CancellationToken ct = default);
}

/// <summary>Multi-file tier: yields (file label, record) without loading entire files.</summary>
public interface IMultiFileRecordStream<TRecord>
{
    IAsyncEnumerable<(string FileLabel, TRecord Record)> RecordsAsync(CancellationToken ct = default);
}

/// <summary>
/// One deferrable ingest unit. Probe runs before materialize; drain applies the descent bitmap.
/// </summary>
public interface IIngestDeferredUnit : IDisposable
{
    /// <summary>Build tree if needed; used by batched descent merge. Do not dispose before drain.</summary>
    TierTree? TreeForBatchProbe { get; }

    Task<byte[]?> ProbeDescentAsync(ISubstrateReader reader, CancellationToken ct = default);

    Hash128 DrainInto(SubstrateChangeBuilder builder, double witnessWeight, byte[]? descentBitmap);
}

/// <summary>
/// Deferred unit with multiple content trees per record (e.g. UD sentences). FlushPending merges all
/// trees across pending units into one batched descent probe.
/// </summary>
public interface IMultiTreeIngestDeferredUnit : IIngestDeferredUnit
{
  IReadOnlyList<TierTree?> AllProbeTrees { get; }

  Hash128 DrainInto(
      SubstrateChangeBuilder builder, double witnessWeight, ReadOnlySpan<byte[]?> perTreeBitmaps);
}

/// <summary>
/// Source-specific connector: grammar witness, content mapping, trunk shortcircuit — Stage 2 only.
/// </summary>
public interface IIngestRecordHandler<TRecord>
{
    ValueTask<bool> TryTrunkShortcircuitAsync(
        TRecord record,
        SubstrateChangeBuilder builder,
        ISubstrateReader reader,
        double witnessWeight,
        CancellationToken ct = default);

    IIngestDeferredUnit CreateDeferredUnit(TRecord record);

    void WalkWitness(TRecord record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit);

    long UnitsPerRecord(TRecord record) => 1;
}

/// <summary>Batch/probe tuning for <see cref="IngestBatchPipeline"/>.</summary>
public sealed class IngestBatchConfig
{
    public required Hash128 SourceId { get; init; }
    public required string BatchLabelPrefix { get; init; }
    public int BatchSize { get; init; } = 256;
    public int ProbeChunkSize { get; init; } = 1024;
    public double WitnessWeight { get; init; } = 1.0;
    public int CommitEpoch { get; init; }
    public ISubstrateReader? ContainmentReader { get; init; }
    public Action<long>? ReportUnits { get; init; }
    public long MaxInputUnits { get; init; }

    /// <summary>When false, the builder does not route content through <see cref="ContentBatch"/> (multi-tree handlers emit directly).</summary>
    public bool EnableDeferredContentOnBuilder { get; init; } = true;

    public int? EntityCapacity { get; init; }
    public int? PhysicalityCapacity { get; init; }
    public int? AttestationCapacity { get; init; }

    public SubstrateChangeBuilder NewBuilder(int batchNumber)
    {
        var b = new SubstrateChangeBuilder(SourceId, $"{BatchLabelPrefix}/{batchNumber}", null,
            entityCapacity: EntityCapacity ?? BatchSize,
            physicalityCapacity: PhysicalityCapacity ?? BatchSize,
            attestationCapacity: AttestationCapacity ?? BatchSize * 4)
            .SetCommitEpoch(CommitEpoch);
        if (EnableDeferredContentOnBuilder)
            b.EnableDeferredContent(EffectiveReader);
        return b;
    }

    internal ISubstrateReader? EffectiveReader =>
        IntentStage.IsBulkFreshBypass ? null : ContainmentReader;
}

/// <summary>
/// Generic Stage 2 ingest: stream records → batch N → O(tier) descent probe → drain novel only → bulk stage.
/// Source-specific code plugs in via <see cref="IRecordStream{TRecord}"/> + <see cref="IIngestRecordHandler{TRecord}"/>.
/// </summary>
public static class IngestBatchPipeline
{
    public static async IAsyncEnumerable<SubstrateChange> RunAsync<TRecord>(
        IRecordStream<TRecord> stream,
        IIngestRecordHandler<TRecord> handler,
        IngestBatchConfig config,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var reader = config.EffectiveReader;
        var pending = reader is not null
            ? new List<(TRecord Record, IIngestDeferredUnit Unit)>(config.ProbeChunkSize)
            : null;

        var state = new BatchState(config.NewBuilder(0));
        long rowsTotal = 0;
        long unitsConsumed = 0;

        await foreach (var record in stream.RecordsAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            rowsTotal++;
            long units = handler.UnitsPerRecord(record);

            if (config.MaxInputUnits > 0 && unitsConsumed >= config.MaxInputUnits)
            {
                await foreach (var change in FlushPending(pending, handler, reader, state, config, ct))
                    yield return change;
                if (state.InBatch > 0)
                    yield return await state.BuildRemainingAsync(ct);
                yield break;
            }

            if (reader is null)
            {
                using (var unit = handler.CreateDeferredUnit(record))
                {
                    var root = unit.DrainInto(state.Builder, config.WitnessWeight, null);
                    handler.WalkWitness(record, root, state.Builder, unit);
                }
                state.AddUnits(units);
                unitsConsumed += units;
            }
            else if (!IntentStage.IsBulkFreshBypass
                     && await handler.TryTrunkShortcircuitAsync(
                         record, state.Builder, reader, config.WitnessWeight, ct).ConfigureAwait(false))
            {
                state.AddUnits(units);
                unitsConsumed += units;
            }
            else
            {
                pending!.Add((record, handler.CreateDeferredUnit(record)));
                if (pending.Count >= config.ProbeChunkSize)
                {
                    await foreach (var change in FlushPending(pending, handler, reader, state, config, ct))
                        yield return change;
                }
                unitsConsumed += units;
            }

            config.ReportUnits?.Invoke(rowsTotal);

            if (state.InBatch >= config.BatchSize)
            {
                yield return await state.YieldBatchAsync(ct);
                state.ResetBuilder(config.NewBuilder(state.BatchNumber));
            }
        }

        if (pending is { Count: > 0 })
        {
            await foreach (var change in FlushPending(pending, handler, reader!, state, config, ct))
                yield return change;
        }

        if (state.InBatch > 0)
            yield return await state.BuildRemainingAsync(ct);
    }

    /// <summary>
    /// Multi-file tier: stream files sequentially; each file gets its own handler/config via factories.
    /// </summary>
    public static async IAsyncEnumerable<SubstrateChange> RunMultiFileAsync<TRecord>(
        IMultiFileRecordStream<TRecord> stream,
        Func<string, IIngestRecordHandler<TRecord>> handlerFactory,
        Func<string, IngestBatchConfig> configFactory,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string? currentLabel = null;
        IIngestRecordHandler<TRecord>? handler = null;
        IngestBatchConfig? config = null;
        var buffer = new List<TRecord>();

        await foreach (var (label, record) in stream.RecordsAsync(ct))
        {
            if (label != currentLabel)
            {
                if (buffer.Count > 0 && handler is not null && config is not null)
                {
                    await foreach (var change in RunAsync(new ListRecordStream<TRecord>(buffer), handler, config, ct))
                        yield return change;
                    buffer.Clear();
                }

                currentLabel = label;
                handler = handlerFactory(label);
                config = configFactory(label);
            }
            buffer.Add(record);
        }

        if (buffer.Count > 0 && handler is not null && config is not null)
        {
            await foreach (var change in RunAsync(new ListRecordStream<TRecord>(buffer), handler, config, ct))
                yield return change;
        }
    }

    private static async IAsyncEnumerable<SubstrateChange> FlushPending<TRecord>(
        List<(TRecord Record, IIngestDeferredUnit Unit)>? pending,
        IIngestRecordHandler<TRecord> handler,
        ISubstrateReader? reader,
        BatchState state,
        IngestBatchConfig config,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (pending is not { Count: > 0 } || reader is null) yield break;

        var flatTrees = new List<TierTree?>();
        var treeRanges = new List<(int Start, int Count)>(pending.Count);
        foreach (var (_, unit) in pending)
        {
            if (unit is IMultiTreeIngestDeferredUnit multi)
            {
                var trees = multi.AllProbeTrees;
                treeRanges.Add((flatTrees.Count, trees.Count));
                flatTrees.AddRange(trees);
            }
            else
            {
                treeRanges.Add((flatTrees.Count, 1));
                flatTrees.Add(unit.TreeForBatchProbe);
            }
        }

        byte[]?[] flatBitmaps = await TierTreeContainmentProbe
            .ProbeBatchNodeEmitBitmapsAsync(flatTrees, reader, ct).ConfigureAwait(false);

        for (int i = 0; i < pending.Count; i++)
        {
            var (record, unit) = pending[i];
            var (start, count) = treeRanges[i];
            try
            {
                Hash128 root = unit is IMultiTreeIngestDeferredUnit multi
                    ? multi.DrainInto(state.Builder, config.WitnessWeight, flatBitmaps.AsSpan(start, count))
                    : unit.DrainInto(state.Builder, config.WitnessWeight,
                        count > 0 ? flatBitmaps[start] : null);
                handler.WalkWitness(record, root, state.Builder, unit);
            }
            finally
            {
                unit.Dispose();
            }

            state.AddUnits(handler.UnitsPerRecord(record));

            if (state.InBatch >= config.BatchSize)
            {
                yield return await state.YieldBatchAsync(ct);
                state.ResetBuilder(config.NewBuilder(state.BatchNumber));
            }
        }
        pending.Clear();
    }

    private sealed class BatchState(SubstrateChangeBuilder builder)
    {
        public SubstrateChangeBuilder Builder { get; private set; } = builder;
        public int InBatch { get; private set; }
        public int BatchNumber { get; private set; }
        private long _rowsInBatch;

        public void AddUnits(long units)
        {
            InBatch++;
            _rowsInBatch += units;
        }

        public void ResetBuilder(SubstrateChangeBuilder next) => Builder = next;

        public async Task<SubstrateChange> YieldBatchAsync(CancellationToken ct)
        {
            var change = await Builder.SetInputUnitsConsumed(_rowsInBatch).BuildAsync(ct);
            InBatch = 0;
            _rowsInBatch = 0;
            BatchNumber++;
            return change;
        }

        public Task<SubstrateChange> BuildRemainingAsync(CancellationToken ct) =>
            Builder.SetInputUnitsConsumed(_rowsInBatch).BuildAsync(ct);
    }

    internal sealed class ListRecordStream<TRecord>(IReadOnlyList<TRecord> records) : IRecordStream<TRecord>
    {
        public async IAsyncEnumerable<TRecord> RecordsAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (int i = 0; i < records.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return records[i];
                await Task.Yield();
            }
        }
    }
}
