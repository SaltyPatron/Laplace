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
    protected abstract IMultiFileRecordStream<TRecord> CreateMultiFileStream(string ecosystemPath);

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
                           CreateMultiFileStream(context.EcosystemPath),
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
/// Sources whose ingest spans multiple pipeline lanes or files with custom
/// orchestration (UD parallel treebanks, SemLink sub-ingests, Model phases).
/// Still sealed on <see cref="DecomposeAsync"/> — subclasses implement
/// <see cref="RunIngestAsync"/> only.
/// </summary>
public abstract class DecomposerOrchestrator : IDecomposer
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

    protected async IAsyncEnumerable<SubstrateChange> RunComposePhaseAsync<T>(
        IAsyncEnumerable<T> records,
        Action<T, SubstrateChangeBuilder> compose,
        string phaseLabel,
        double sourceTrust,
        int batchSize,
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct,
        int commitEpoch = 0,
        int? attestationCapacity = null)
    {
        var phase = new OrchestratorComposePhase<T>(
            this, phaseLabel, records, compose, sourceTrust, batchSize, commitEpoch, attestationCapacity);
        await foreach (var change in phase.DecomposeAsync(context, options, ct))
            yield return change;
    }

    protected async IAsyncEnumerable<SubstrateChange> RunGrammarComposePhaseAsync(
        IAsyncEnumerable<GrammarComposeRecord> records,
        double sourceTrust,
        string phaseLabel,
        int batchSize,
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var phase = new OrchestratorGrammarComposePhase(this, phaseLabel, records, sourceTrust, batchSize);
        await foreach (var change in phase.DecomposeAsync(context, options, ct))
            yield return change;
    }

    protected async IAsyncEnumerable<SubstrateChange> RunCategoryCorrespondencePhaseAsync(
        IAsyncEnumerable<CategoryCorrespondenceRecord> records,
        double sourceTrust,
        string phaseLabel,
        int batchSize,
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var phase = new OrchestratorCategoryCorrespondencePhase(this, phaseLabel, records, sourceTrust, batchSize);
        await foreach (var change in phase.DecomposeAsync(context, options, ct))
            yield return change;
    }

    private sealed class OrchestratorComposePhase<T> : ComposeDecomposerPhase<T>
    {
        private readonly DecomposerOrchestrator _owner;
        private readonly IAsyncEnumerable<T> _records;
        private readonly Action<T, SubstrateChangeBuilder> _compose;
        private readonly double _sourceTrust;
        private readonly int _batchSize;
        private readonly int _commitEpoch;
        private readonly int? _attestationCapacity;

        public OrchestratorComposePhase(
            DecomposerOrchestrator owner,
            string phaseLabel,
            IAsyncEnumerable<T> records,
            Action<T, SubstrateChangeBuilder> compose,
            double sourceTrust,
            int batchSize,
            int commitEpoch,
            int? attestationCapacity)
        {
            _owner = owner;
            _phaseLabel = phaseLabel;
            _records = records;
            _compose = compose;
            _sourceTrust = sourceTrust;
            _batchSize = batchSize;
            _commitEpoch = commitEpoch;
            _attestationCapacity = attestationCapacity;
        }

        private readonly string _phaseLabel;

        protected override string PhaseLabel => _phaseLabel;

        public override Hash128 SourceId => _owner.SourceId;
        public override string SourceName => _owner.SourceName;
        public override int LayerOrder => _owner.LayerOrder;
        public override Hash128 TrustClassId => _owner.TrustClassId;
        protected override double SourceTrust => _sourceTrust;
        protected override int DefaultBatchSize => _batchSize;

        public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.CompletedTask;

        public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.FromResult<long?>(null);

        protected override void Compose(T record, SubstrateChangeBuilder builder) => _compose(record, builder);

        protected override IAsyncEnumerable<T> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options, CancellationToken ct) => _records;

        protected override IngestBatchConfig BuildPipelineConfig(
            IDecomposerContext context, DecomposerOptions options)
        {
            var config = IngestPipelineDefaults.Compose(
                SourceId, BatchLabelPrefix, DefaultBatchSize, options, context.Reader,
                attestationCapacity: _attestationCapacity, commitEpoch: _commitEpoch);
            return IngestPipelineDefaults.ApplyMaxInputUnits(config, options);
        }
    }

    private sealed class OrchestratorGrammarComposePhase : GrammarComposeDecomposer
    {
        private readonly DecomposerOrchestrator _owner;
        private readonly IAsyncEnumerable<GrammarComposeRecord> _records;
        private readonly double _sourceTrust;
        private readonly int _batchSize;
        private readonly string _batchLabelPrefix;

        public OrchestratorGrammarComposePhase(
            DecomposerOrchestrator owner,
            string phaseLabel,
            IAsyncEnumerable<GrammarComposeRecord> records,
            double sourceTrust,
            int batchSize)
        {
            _owner = owner;
            _batchLabelPrefix = $"{owner.SourceName}/{phaseLabel}";
            _records = records;
            _sourceTrust = sourceTrust;
            _batchSize = batchSize;
        }

        protected override string BatchLabelPrefix => _batchLabelPrefix;

        public override Hash128 SourceId => _owner.SourceId;
        public override string SourceName => _owner.SourceName;
        public override int LayerOrder => _owner.LayerOrder;
        public override Hash128 TrustClassId => _owner.TrustClassId;
        protected override double SourceTrust => _sourceTrust;
        protected override int DefaultBatchSize => _batchSize;

        public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.CompletedTask;

        public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.FromResult<long?>(null);

        protected override IAsyncEnumerable<GrammarComposeRecord> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options, CancellationToken ct) => _records;
    }

    private sealed class OrchestratorCategoryCorrespondencePhase : CategoryCorrespondenceDecomposer
    {
        private readonly DecomposerOrchestrator _owner;
        private readonly IAsyncEnumerable<CategoryCorrespondenceRecord> _records;
        private readonly double _sourceTrust;
        private readonly int _batchSize;
        private readonly string _batchLabelPrefix;

        public OrchestratorCategoryCorrespondencePhase(
            DecomposerOrchestrator owner,
            string phaseLabel,
            IAsyncEnumerable<CategoryCorrespondenceRecord> records,
            double sourceTrust,
            int batchSize)
        {
            _owner = owner;
            _batchLabelPrefix = $"{owner.SourceName}/{phaseLabel}";
            _records = records;
            _sourceTrust = sourceTrust;
            _batchSize = batchSize;
        }

        protected override string BatchLabelPrefix => _batchLabelPrefix;

        public override Hash128 SourceId => _owner.SourceId;
        public override string SourceName => _owner.SourceName;
        public override int LayerOrder => _owner.LayerOrder;
        public override Hash128 TrustClassId => _owner.TrustClassId;
        protected override double SourceTrust => _sourceTrust;
        protected override int DefaultBatchSize => _batchSize;

        public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.CompletedTask;

        public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.FromResult<long?>(null);

        protected override IAsyncEnumerable<CategoryCorrespondenceRecord> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options, CancellationToken ct) => _records;
    }
}
