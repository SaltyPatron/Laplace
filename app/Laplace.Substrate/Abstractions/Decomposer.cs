using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Centralized working-set pipeline defaults for all decomposer lanes. Every
/// <see cref="Decomposer{TRecord}"/> subclass routes through these presets so
/// batch sizes, capacities, and working-set mode stay consistent.
/// </summary>
public static class IngestPipelineDefaults
{
    /// <summary>
    /// Per-source working-set knobs from Intel topology + RAM (Rule #12).
    /// </summary>
    public static (int Batch, int ProbeInterval, int RecordCap, int ProbeChunk) ResolveWorkingSet(
        IngestSourceProfile profile,
        DecomposerOptions? options = null,
        int? defaultBatch = null)
    {
        int batch = BatchConfigDefaults.Resolve(
            options, defaultBatch ?? IngestSizing.ResolveForSource(profile).RecordBatchSize);
        var sized = IngestSizing.ResolveForSource(profile, batch);
        return (batch, sized.WorkingSetProbeInterval, sized.WorkingSetRecordCap, sized.ProbeChunkSize);
    }

    /// <summary>
    /// Relation-triple lane: each record composes subject + object tier trees (see
    /// <see cref="RelationTripleHandler"/>). Batch and probe interval come from
    /// <see cref="IngestSourceProfile.RelationTriple"/>, not HighVolume.
    /// </summary>
    public static IngestBatchConfig RelationTriple(
        Hash128 sourceId, string batchLabelPrefix, DecomposerOptions options, ISubstrateReader? reader)
    {
        var profile = IngestSourceProfile.RelationTriple;
        var ws = ResolveWorkingSet(profile, options);
        return new()
        {
            SourceId = sourceId,
            BatchLabelPrefix = batchLabelPrefix,
            BatchSize = ws.Batch,
            WorkingSetProbeInterval = ws.ProbeInterval,
            WorkingSetRecordCap = ws.RecordCap,
            WorkingSetProfile = profile,
            ContainmentReader = reader,
            MaxInputUnits = options.MaxInputUnits,
            WorkingSet = WorkingSetMode.Enabled,
        };
    }

    public static IngestBatchConfig Compose(
        Hash128 sourceId,
        string batchLabelPrefix,
        int defaultBatchSize,
        DecomposerOptions options,
        ISubstrateReader? reader,
        IngestSourceProfile? profile = null,
        int? attestationCapacity = null,
        int commitEpoch = 0)
    {
        profile ??= IngestSourceProfile.Default;
        var ws = ResolveWorkingSet(profile, options, defaultBatchSize);
        return new()
        {
            SourceId = sourceId,
            BatchLabelPrefix = batchLabelPrefix,
            BatchSize = ws.Batch,
            ProbeChunkSize = ws.ProbeChunk,
            CommitEpoch = commitEpoch,
            ContainmentReader = reader,
            MaxInputUnits = options.MaxInputUnits,
            WorkingSet = WorkingSetMode.Enabled,
            WorkingSetProbeInterval = ws.ProbeInterval,
            WorkingSetRecordCap = ws.RecordCap,
            WorkingSetProfile = profile,
            EntityCapacity = ws.Batch * 4,
            PhysicalityCapacity = ws.Batch * 2,
            AttestationCapacity = attestationCapacity ?? ws.Batch * 8,
        };
    }

    public static IngestBatchConfig GrammarCompose(
        Hash128 sourceId, string batchLabelPrefix, int defaultBatchSize,
        DecomposerOptions options, ISubstrateReader? reader,
        IngestSourceProfile? profile = null)
    {
        profile ??= IngestSourceProfile.Default;
        var ws = ResolveWorkingSet(profile, options, defaultBatchSize);
        return new()
        {
            SourceId = sourceId,
            BatchLabelPrefix = batchLabelPrefix,
            BatchSize = ws.Batch,
            ProbeChunkSize = Math.Clamp(ws.ProbeChunk, 64, 1024),
            ContainmentReader = reader,
            EnableDeferredContentOnBuilder = false,
            EntityCapacity = ws.Batch * 8,
            PhysicalityCapacity = ws.Batch * 8,
            AttestationCapacity = ws.Batch * 16,
            WorkingSet = WorkingSetMode.Enabled,
            WorkingSetProbeInterval = ws.ProbeInterval,
            WorkingSetRecordCap = ws.RecordCap,
            WorkingSetProfile = profile,
            MaxInputUnits = options.MaxInputUnits,
        };
    }

    /// <summary>
    /// Witnessed structured-grammar lane (OMW, Wiktionary, Tatoeba, Etl rows).
    /// Mirrors <see cref="StructuredGrammarIngest.IngestFileAsync"/> config shape.
    /// </summary>
    public static IngestBatchConfig StructuredGrammar(
        Hash128 sourceId,
        string batchLabelPrefix,
        int defaultBatchSize,
        DecomposerOptions options,
        ISubstrateReader? reader,
        double witnessWeight = 1.0,
        int commitEpoch = 0,
        IngestSourceProfile? profile = null)
    {
        profile ??= IngestSourceProfile.Wiktionary;
        var sized = IngestSizing.ResolveForSource(profile, defaultBatchSize > 0 ? defaultBatchSize : null);
        return new()
        {
            SourceId = sourceId,
            BatchLabelPrefix = batchLabelPrefix,
            BatchSize = sized.RecordBatchSize,
            ProbeChunkSize = sized.ProbeChunkSize,
            WitnessWeight = witnessWeight,
            CommitEpoch = commitEpoch,
            ContainmentReader = reader,
            MaxInputUnits = options.MaxInputUnits,
            WorkingSet = WorkingSetMode.Enabled,
            WorkingSetProbeInterval = sized.WorkingSetProbeInterval,
            WorkingSetRecordCap = sized.WorkingSetRecordCap,
            WorkingSetProfile = profile,
        };
    }

    public static IngestBatchConfig CategoryCorrespondence(
        Hash128 sourceId, string batchLabelPrefix, int defaultBatchSize,
        DecomposerOptions options, ISubstrateReader? reader,
        IngestSourceProfile? profile = null)
    {
        profile ??= IngestSourceProfile.Default;
        var ws = ResolveWorkingSet(profile, options, defaultBatchSize);
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
            MaxInputUnits = options.MaxInputUnits,
        };
    }

    public static IngestBatchConfig ApplyMaxInputUnits(IngestBatchConfig config, DecomposerOptions options) =>
        options.MaxInputUnits > 0 ? config.WithMaxInputUnits(options.MaxInputUnits) : config;
}

/// <summary>
/// Unified extract-only decomposer base. Subclasses implement record extraction and
/// handler selection; <see cref="DecomposeAsync"/> is sealed and always routes through
/// <see cref="IngestBatchPipeline"/> working-set mode.
/// </summary>
public abstract class Decomposer<TRecord> : IDecomposer
{
    public abstract Hash128 SourceId { get; }
    public abstract string SourceName { get; }
    public abstract int LayerOrder { get; }
    public abstract Hash128 TrustClassId { get; }
    protected abstract double SourceTrust { get; }

    protected ISubstrateReader? ContainmentReader { get; set; }

    protected virtual string BatchLabelPrefix => SourceName;

    protected virtual int DefaultBatchSize => BatchConfigDefaults.Structural;

    public virtual int EstimatedBytesPerRecord => IngestSizing.DefaultEstBytesPerRecord;

    public virtual int EstimatedComposeUnitsPerRecord => 1;

    protected IngestSourceProfile PipelineProfile =>
        new(EstimatedBytesPerRecord, EstimatedComposeUnitsPerRecord);

    protected abstract IIngestRecordHandler<TRecord> CreateHandler();

    protected abstract IAsyncEnumerable<TRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options, CancellationToken ct);

    protected virtual IngestBatchConfig BuildPipelineConfig(
        IDecomposerContext context, DecomposerOptions options) =>
        IngestPipelineDefaults.Compose(
            SourceId, BatchLabelPrefix, DefaultBatchSize, options, context.Reader, PipelineProfile);

    public abstract Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default);

    public abstract Task<long?> EstimateUnitCountAsync(
        IDecomposerContext context, CancellationToken ct = default);

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var change in RunDecomposeAsync(context, options, ct))
            yield return change;
    }

    protected virtual async IAsyncEnumerable<SubstrateChange> RunDecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ContainmentReader = context.Reader;
        if (options.DryRun) yield break;

        var stream = new AsyncEnumerableRecordStream<TRecord>(
            ExtractRecordsAsync(context.EcosystemPath, options, ct));
        var handler = CreateHandler();
        var config = IngestPipelineDefaults.ApplyMaxInputUnits(
            BuildPipelineConfig(context, options), options);

        await foreach (var change in IngestBatchPipeline.RunAsync(stream, handler, config, ct))
            yield return change;
    }
}

/// <summary>
/// Multi-file sources (document ingest, per-file treebanks) that route through
/// <see cref="IngestBatchPipeline.RunMultiFileAsync"/>.
/// </summary>
public abstract class DecomposerMultiFile<TRecord> : Decomposer<TRecord>
{
    protected abstract IMultiFileRecordStream<TRecord> CreateMultiFileStream(
        string ecosystemPath, DecomposerOptions options);

    protected abstract IIngestRecordHandler<TRecord> CreateHandlerForFile(string fileLabel);

    protected abstract IngestBatchConfig ConfigForFile(
        string fileLabel, ISubstrateReader? reader, DecomposerOptions options);

    protected sealed override async IAsyncEnumerable<SubstrateChange> RunDecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ContainmentReader = context.Reader;
        if (options.DryRun) yield break;

        await foreach (var change in IngestBatchPipeline.RunMultiFileAsync(
                           CreateMultiFileStream(context.EcosystemPath, options),
                           CreateHandlerForFile,
                           label => ConfigForFile(label, context.Reader, options),
                           maxTotalUnits: options.MaxInputUnits,
                           ct))
            yield return change;
    }

    protected sealed override IIngestRecordHandler<TRecord> CreateHandler() =>
        throw new NotSupportedException(
            $"{GetType().Name} uses multi-file streaming; use CreateHandlerForFile instead.");

    protected sealed override IAsyncEnumerable<TRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options, CancellationToken ct) =>
        throw new NotSupportedException(
            $"{GetType().Name} uses multi-file streaming; override CreateMultiFileStream instead.");
}

/// <summary>
/// One phase of a multi-phase source (WordNet data/sense/exc/sent, Model tokenizer/recipe/…).
/// </summary>
public abstract class DecomposerPhase<TRecord> : Decomposer<TRecord>
{
    protected abstract string PhaseLabel { get; }

    protected sealed override string BatchLabelPrefix => $"{SourceName}/{PhaseLabel}";
}

/// <summary>
/// Imperative-compose phase inside a multi-phase orchestrator.
/// </summary>
public abstract class ComposeDecomposerPhase<TRecord> : ComposeDecomposer<TRecord>
{
    protected abstract string PhaseLabel { get; }

    protected sealed override string BatchLabelPrefix => $"{SourceName}/{PhaseLabel}";
}

/// <summary>
/// Imperative-compose lane: record → callback into <see cref="SubstrateChangeBuilder"/>.
/// </summary>
public abstract class ComposeDecomposer<TRecord> : Decomposer<TRecord>
{
    protected abstract void Compose(TRecord record, SubstrateChangeBuilder builder);

    protected sealed override IIngestRecordHandler<TRecord> CreateHandler() =>
        new DirectComposeHandler<TRecord>(Compose);

    protected override IngestBatchConfig BuildPipelineConfig(
        IDecomposerContext context, DecomposerOptions options) =>
        IngestPipelineDefaults.Compose(
            SourceId, BatchLabelPrefix, DefaultBatchSize, options, context.Reader, PipelineProfile);
}

public abstract class RelationTripleDecomposer : Decomposer<RelationTripleRecord>
{
    public override int EstimatedBytesPerRecord => IngestSourceProfile.RelationTriple.EstBytesPerRecord;

    public override int EstimatedComposeUnitsPerRecord =>
        IngestSourceProfile.RelationTriple.EstComposeUnitsPerRecord;

    protected sealed override IIngestRecordHandler<RelationTripleRecord> CreateHandler() =>
        new RelationTripleHandler(SourceId, SourceTrust);

    protected override IngestBatchConfig BuildPipelineConfig(
        IDecomposerContext context, DecomposerOptions options) =>
        IngestPipelineDefaults.RelationTriple(SourceId, BatchLabelPrefix, options, context.Reader);
}

public abstract class GrammarComposeDecomposer : Decomposer<GrammarComposeRecord>
{
    protected sealed override IIngestRecordHandler<GrammarComposeRecord> CreateHandler() =>
        new GrammarComposeHandler(SourceId, SourceTrust, ContainmentReader);

    protected override IngestBatchConfig BuildPipelineConfig(
        IDecomposerContext context, DecomposerOptions options) =>
        IngestPipelineDefaults.GrammarCompose(
            SourceId, BatchLabelPrefix, DefaultBatchSize, options, context.Reader);

    protected override int DefaultBatchSize => BatchConfigDefaults.Code;
}

/// <summary>
/// Witnessed structured-grammar lane: row parse → <see cref="GrammarIngestHandler"/>.
/// Subclasses supply record streams (file, parallel file, multi-file).
/// </summary>
public abstract class GrammarIngestDecomposer : Decomposer<GrammarIngestRecord>
{
    protected abstract string ModalityId { get; }
    protected abstract IGrammarWitness CreateWitness(DecomposerOptions options);
    protected virtual double WitnessWeight => 1.0;
    protected virtual int CommitEpoch => 0;
    protected virtual Hash128? ContextId => null;
    protected virtual IngestSourceProfile IngestProfile => IngestSourceProfile.Wiktionary;

    private DecomposerOptions? _activeOptions;

    protected sealed override IIngestRecordHandler<GrammarIngestRecord> CreateHandler()
    {
        var options = _activeOptions ?? DecomposerOptions.Default;
        return new GrammarIngestHandler(SourceId, ModalityId, CreateWitness(options), ContextId);
    }

    protected override IngestBatchConfig BuildPipelineConfig(
        IDecomposerContext context, DecomposerOptions options) =>
        IngestPipelineDefaults.StructuredGrammar(
            SourceId, BatchLabelPrefix, DefaultBatchSize, options, context.Reader,
            WitnessWeight, CommitEpoch, IngestProfile);

    protected sealed override async IAsyncEnumerable<SubstrateChange> RunDecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ContainmentReader = context.Reader;
        _activeOptions = options;
        if (options.DryRun) yield break;

        var stream = new AsyncEnumerableRecordStream<GrammarIngestRecord>(
            ExtractRecordsAsync(context.EcosystemPath, options, ct));
        var handler = CreateHandler();
        var config = IngestPipelineDefaults.ApplyMaxInputUnits(
            BuildPipelineConfig(context, options), options);

        await foreach (var change in IngestBatchPipeline.RunAsync(stream, handler, config, ct))
            yield return change;
    }
}

public abstract class CategoryCorrespondenceDecomposer : Decomposer<CategoryCorrespondenceRecord>
{
    protected sealed override IIngestRecordHandler<CategoryCorrespondenceRecord> CreateHandler() =>
        new CategoryCorrespondenceHandler(SourceId, SourceTrust);

    protected override IngestBatchConfig BuildPipelineConfig(
        IDecomposerContext context, DecomposerOptions options) =>
        IngestPipelineDefaults.CategoryCorrespondence(
            SourceId, BatchLabelPrefix, DefaultBatchSize, options, context.Reader);

    protected override int DefaultBatchSize => BatchConfigDefaults.HighVolume;
}

/// <summary>
/// Multi-phase sources (WordNet data/sense/exc/sent, SemLink sub-ingests,
/// Model tokenizer/recipe/…). Each phase is a standalone
/// <see cref="DecomposerPhase{T}"/> or <see cref="ComposeDecomposerPhase{T}"/>
/// routed through <see cref="RunPhaseAsync"/>. Sealed on
/// <see cref="DecomposeAsync"/> — subclasses implement <see cref="RunIngestAsync"/> only.
/// </summary>
public abstract class DecomposerMultiPhase : IDecomposer
{
    public abstract Hash128 SourceId { get; }
    public abstract string SourceName { get; }
    public abstract int LayerOrder { get; }
    public abstract Hash128 TrustClassId { get; }

    public abstract Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default);

    public abstract Task<long?> EstimateUnitCountAsync(
        IDecomposerContext context, CancellationToken ct = default);

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected abstract IAsyncEnumerable<SubstrateChange> RunIngestAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct);

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (options.DryRun) yield break;
        await foreach (var change in RunIngestAsync(context, options, ct))
            yield return change;
    }

    protected static async IAsyncEnumerable<SubstrateChange> RunPhaseAsync(
        IDecomposer phase,
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var change in phase.DecomposeAsync(context, options, ct))
            yield return change;
    }
}
