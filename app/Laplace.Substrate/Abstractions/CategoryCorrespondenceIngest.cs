using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Category-key → fixed object id edge (CORRESPONDS_TO, ROLE_CORRESPONDS_TO context, etc.).
/// Extraction-only record; staging runs through the shared pipeline.
/// </summary>
public readonly record struct CategoryCorrespondenceRecord(
    string SubjectKey,
    Hash128 SubjectTypeId,
    Hash128 ObjectId,
    string RelationType = "CORRESPONDS_TO",
    Hash128? ContextId = null,
    double Magnitude = 1.0);

public sealed class CategoryCorrespondenceHandler : IIngestRecordHandler<CategoryCorrespondenceRecord>
{
    private readonly Hash128 _sourceId;
    private readonly double _trust;

    public CategoryCorrespondenceHandler(Hash128 sourceId, double trust)
    {
        _sourceId = sourceId;
        _trust = trust;
    }

    public ValueTask<bool> TryTrunkShortcircuitAsync(
        CategoryCorrespondenceRecord record, SubstrateChangeBuilder builder, ISubstrateReader reader,
        double witnessWeight, CancellationToken ct) =>
        ValueTask.FromResult(false);

    public IIngestDeferredUnit CreateDeferredUnit(CategoryCorrespondenceRecord record) =>
        new Unit(record, _sourceId, _trust);

    public void WalkWitness(
        CategoryCorrespondenceRecord record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit) { }

    private sealed class Unit(CategoryCorrespondenceRecord record, Hash128 sourceId, double trust) : IIngestDeferredUnit
    {
        public TierTree? TreeForBatchProbe => null;

        public Task<byte[]?> ProbeDescentAsync(ISubstrateReader reader, CancellationToken ct) =>
            Task.FromResult<byte[]?>(null);

        public Hash128 DrainInto(SubstrateChangeBuilder builder, double witnessWeight, byte[]? descentBitmap)
        {
            Hash128? subjectId = CategoryAnchor.Emit(builder, record.SubjectKey, record.SubjectTypeId, sourceId, trust);
            if (subjectId is null) return default;
            builder.AddAttestation(NativeAttestation.Categorical(
                subjectId.Value, record.RelationType, record.ObjectId, sourceId, trust,
                magnitude: record.Magnitude, arenaScale: 1.0, contextId: record.ContextId));
            return subjectId.Value;
        }

        public void Dispose() { }
    }
}

public static class CategoryCorrespondenceIngestSupport
{
    public static IngestBatchConfig PipelineConfig(
        Hash128 sourceId, string batchLabelPrefix, int batchSize, ISubstrateReader? reader)
    {
        var profile = IngestSourceProfile.Default;
        var ws = IngestPipelineDefaults.ResolveWorkingSet(profile, defaultBatch: batchSize);
        return new()
        {
            SourceId = sourceId,
            BatchLabelPrefix = batchLabelPrefix,
            BatchSize = ws.Batch,
            ProbeChunkSize = Math.Clamp(ws.ProbeChunk, 64, 4096),
            ContainmentReader = reader,
            EnableDeferredContentOnBuilder = false,
            EntityCapacity = ws.Batch * 3,
            AttestationCapacity = ws.Batch * 3,
            WorkingSet = WorkingSetMode.Enabled,
            WorkingSetProbeInterval = ws.ProbeInterval,
            WorkingSetRecordCap = ws.RecordCap,
            WorkingSetProfile = profile,
        };
    }

    public static IAsyncEnumerable<SubstrateChange> RunPipelineAsync(
        IAsyncEnumerable<CategoryCorrespondenceRecord> records,
        Hash128 sourceId,
        double trust,
        string batchLabelPrefix,
        int batchSize,
        ISubstrateReader? reader,
        DecomposerOptions options,
        CancellationToken ct = default)
    {
        if (options.DryRun) return Empty();
        var stream = new AsyncEnumerableRecordStream<CategoryCorrespondenceRecord>(records);
        var handler = new CategoryCorrespondenceHandler(sourceId, trust);
        var config = IngestPipelineDefaults.ApplyMaxInputUnits(
            IngestPipelineDefaults.CategoryCorrespondence(sourceId, batchLabelPrefix, batchSize, options, reader),
            options);
        return IngestBatchPipeline.RunAsync(stream, handler, config, ct);
    }

    private static async IAsyncEnumerable<SubstrateChange> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }
}
