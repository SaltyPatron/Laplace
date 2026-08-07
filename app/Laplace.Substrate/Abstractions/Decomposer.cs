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
    /// THE record-batch resolver. Every decomposer that needs a batch size calls this and
    /// nothing else.
    ///
    /// It exists because the idiom was hand-written eight times, and five of those wrote it
    /// as `options.BatchSize > 1 ? options.BatchSize : &lt;literal&gt;` — which never consults
    /// <see cref="IngestSizing"/> at all. A per-source literal cannot track the box, so those
    /// sources ingested with the same batch on a 4-core laptop and a 128 GB server while
    /// CLAUDE.md documented that batch sizing "deliberately has no env override" because
    /// IngestSizing/MemoryTopology own it. A private `? : 2048` overrides it exactly as
    /// effectively as an env var would.
    ///
    /// An explicit operator batch (`--batch`) still wins — that is the ONE legitimate
    /// override, and it arrives through <see cref="DecomposerOptions.BatchSize"/>.
    /// </summary>
    public static int ResolveBatch(IngestSourceProfile profile, DecomposerOptions? options) =>
        ResolveWorkingSet(profile, options).Batch;

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

    /// <summary>See <see cref="IDecomposer.PerFileCompletion"/>.</summary>
    public virtual bool PerFileCompletion => false;

    // Virtual on the class, not just the interface default: interface mapping is
    // computed at the class that lists IDecomposer, so a derived class declaring
    // this property WITHOUT override would shadow it — invisible through
    // IDecomposer references, silently registering zero readback names.
    public virtual IReadOnlyCollection<string> CanonicalNamesForReadback => Array.Empty<string>();

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

        IngestBatchConfig BuildConfig() => IngestPipelineDefaults.ApplyMaxInputUnits(
            BuildPipelineConfig(context, options), options);

        // A monolithic single-file source has no intra-file parallelism: one working-set
        // builder, serial DrainInto, idle P-cores. Cut the already-FRAMED record stream on
        // record boundaries into N independent working-set pipelines — a segmented monolith
        // rides the same pool as a multi-file source, and content-addressing merges any
        // cross-segment collisions (same content -> same id) with no coordination. Capped
        // runs stay serial (ResolveSegments -> 1) so the exact input-unit stop point holds.
        int segments = MonolithSegmenter.ResolveSegments(BuildConfig());
        if (segments <= 1)
        {
            await foreach (var change in IngestBatchPipeline.RunAsync(
                               stream, CreateHandler(), BuildConfig(), ct))
                yield return change;
            yield break;
        }

        await foreach (var change in MonolithSegmenter.RunSegmentedAsync(
                           stream,
                           _ => CreateHandler(),
                           _ => BuildConfig(),
                           segments,
                           MonolithSegmenter.ResolveChunkRecords(BuildConfig()),
                           BatchLabelPrefix,
                           ct))
            yield return change;
    }
}

/// <summary>
/// Multi-file sources that route through <see cref="IngestBatchPipeline.RunMultiFileAsync"/>.
/// The unit of work is <see cref="ExtractFileAsync"/> — one file → records. Multi-file
/// orchestration calls that unit once per path; it is not a second parser family.
/// Override <see cref="CreateMultiFileStream"/> only when the open shape is not a path list
/// (legacy adapters); new sources implement <see cref="ListFiles"/> + <see cref="ExtractFileAsync"/>.
/// </summary>
public abstract class DecomposerMultiFile<TRecord> : Decomposer<TRecord>
{
    /// <summary>Cheap path/label enumeration — reads nothing.</summary>
    protected virtual IReadOnlyList<(string Path, string Label)> ListFiles(
        string ecosystemPath, DecomposerOptions options) =>
        throw new NotSupportedException(
            $"{GetType().Name}: implement ListFiles+ExtractFileAsync, or override CreateMultiFileStream.");

    /// <summary>
    /// Parse ONE file into records. This is the single-file masticator; the multi-file
    /// pool invokes it per claimed path.
    /// </summary>
    protected virtual IAsyncEnumerable<TRecord> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options, CancellationToken ct) =>
        throw new NotSupportedException(
            $"{GetType().Name}: implement ExtractFileAsync, or override CreateMultiFileStream.");

    /// <summary>
    /// Default: <see cref="PathListMultiFileStream{TRecord}"/> over <see cref="ListFiles"/>,
    /// each file opened via <see cref="ExtractFileAsync"/>.
    /// </summary>
    protected virtual IMultiFileRecordStream<TRecord> CreateMultiFileStream(
        string ecosystemPath, DecomposerOptions options) =>
        new PathListMultiFileStream<TRecord>(
            ListFiles(ecosystemPath, options), ExtractFileAsync, options);

    protected abstract IIngestRecordHandler<TRecord> CreateHandlerForFile(string fileLabel);

    protected abstract IngestBatchConfig ConfigForFile(
        string fileLabel, ISubstrateReader? reader, DecomposerOptions options);

    /// <summary>
    /// Per-file resume (GH #898): each finished file's boundary deposits a
    /// HasLayerCompleted marker on the file's content identity, and a restarted run
    /// true-skips marker-complete files before opening them. Without this, a killed
    /// multi-hour run restarts from record zero and RE-FOLDS the applied prefix —
    /// testimony is not idempotent, so witness counts inflate corpus-wide. With it,
    /// the blast radius of a kill is the one file that was mid-apply.
    /// Default ON for every <see cref="DecomposerMultiFile{TRecord}"/> lane — that is
    /// the shape the resume contract was built for. Monolith / multi-phase sources do
    /// not inherit this base. Files above the resume hash cap opt out per file.
    /// </summary>
    public virtual bool PerFileResume => true;

    // Multi-file sources ingest PARALLEL BY DEFAULT across the file-worker pool. References resolve
    // content-addressed (hash of the canonical key), so files carry no cross-file ordering and no
    // phase concept is needed — cross-source agreement is a hash collision.
    protected sealed override async IAsyncEnumerable<SubstrateChange> RunDecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ContainmentReader = context.Reader;
        if (options.DryRun) yield break;

        IngestBatchPipeline.PerFileResumePlan? resume =
            PerFileResume && context.Reader is { } rdr
                ? new IngestBatchPipeline.PerFileResumePlan(
                    rdr, LayerOrder, options.ReObservePresent)
                : null;

        await foreach (var change in IngestBatchPipeline.RunMultiFileAsync(
                           CreateMultiFileStream(context.EcosystemPath, options),
                           CreateHandlerForFile,
                           label => ConfigForFile(label, context.Reader, options),
                           maxTotalUnits: options.MaxInputUnits,
                           fileWorkers: IngestTopology.Current.FileWorkers,
                           isolateFileFailures: PerFileCompletion,
                           resume: resume,
                           ct: ct))
            yield return change;
    }

    protected sealed override IIngestRecordHandler<TRecord> CreateHandler() =>
        throw new NotSupportedException(
            $"{GetType().Name} uses multi-file streaming; use CreateHandlerForFile instead.");

    /// <summary>
    /// Concatenation of <see cref="ExtractFileAsync"/> over <see cref="ListFiles"/> —
    /// the same masticator the parallel pool uses, serial. Not the production driver
    /// (<see cref="RunDecomposeAsync"/> uses the pool); exists so the unit is callable
    /// without a second parser.
    /// </summary>
    protected sealed override async IAsyncEnumerable<TRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var (path, label) in ListFiles(ecosystemPath, options))
        {
            await foreach (var record in ExtractFileAsync(path, label, options, ct))
                yield return record;
        }
    }
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

/// <summary>
/// Imperative-compose source spread across MANY files: the multi-file worker pool AND the
/// <see cref="DirectComposeHandler{TRecord}"/>, which nothing joined before.
///
/// A compose-shaped source with more than one input file previously had to choose between
/// <see cref="ComposeDecomposer{TRecord}"/> — which gives you Compose() but drives ONE serial
/// record stream — and <see cref="DecomposerMultiFile{TRecord}"/>, which gives you the parallel
/// pool but no compose handler. ChessPgnDecomposer chose the first and streamed 11 PGN files
/// through a single thread, on a box whose other multi-file sources fan out by default.
/// The missing base is the whole reason; there is nothing chess-specific about it.
///
/// Files carry no cross-file ordering (references resolve content-addressed), so parallelism
/// here is the same claim the multi-file pool already makes for every other source.
/// </summary>
public abstract class ComposeDecomposerMultiFile<TRecord> : DecomposerMultiFile<TRecord>
{
    protected abstract void Compose(TRecord record, SubstrateChangeBuilder builder);

    protected sealed override IIngestRecordHandler<TRecord> CreateHandlerForFile(string fileLabel) =>
        new DirectComposeHandler<TRecord>(Compose);

    // Per-FILE label, not BatchLabelPrefix: with workers running concurrently the batch label is
    // the only thing attributing a batch to its input file in the run journal.
    protected override IngestBatchConfig ConfigForFile(
        string fileLabel, ISubstrateReader? reader, DecomposerOptions options) =>
        IngestPipelineDefaults.Compose(
            SourceId, fileLabel, DefaultBatchSize, options, reader, PipelineProfile);
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

    /// <summary>Paths to masticate. Usually one file for monolith sources.</summary>
    protected abstract IReadOnlyList<string> ListInputFiles(
        string ecosystemPath, DecomposerOptions options);

    /// <summary>
    /// Parse ONE file into triples. This is the unit — multi-file relation-triple
    /// sources call the same shape via <see cref="DecomposerMultiFile{TRecord}.ExtractFileAsync"/>.
    /// </summary>
    protected abstract IAsyncEnumerable<RelationTripleRecord> ExtractFileAsync(
        string filePath, DecomposerOptions options, CancellationToken ct);

    protected sealed override async IAsyncEnumerable<RelationTripleRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        long cap = options.MaxInputUnits;
        long consumed = 0;
        foreach (var path in ListInputFiles(ecosystemPath, options))
        {
            await foreach (var record in ExtractFileAsync(path, options, ct))
            {
                yield return record;
                if (cap > 0 && ++consumed >= cap) yield break;
            }
        }
    }
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

    // Same segmented monolith path as Decomposer<TRecord>.RunDecomposeAsync — do not
    // keep a second idle-core serial compose lane for grammar monoliths.
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

        IngestBatchConfig BuildConfig() => IngestPipelineDefaults.ApplyMaxInputUnits(
            BuildPipelineConfig(context, options), options);

        int segments = MonolithSegmenter.ResolveSegments(BuildConfig());
        if (segments <= 1)
        {
            await foreach (var change in IngestBatchPipeline.RunAsync(
                               stream, CreateHandler(), BuildConfig(), ct))
                yield return change;
            yield break;
        }

        await foreach (var change in MonolithSegmenter.RunSegmentedAsync(
                           stream,
                           _ => CreateHandler(),
                           _ => BuildConfig(),
                           segments,
                           MonolithSegmenter.ResolveChunkRecords(BuildConfig()),
                           BatchLabelPrefix,
                           ct))
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

    // See Decomposer<TRecord>.CanonicalNamesForReadback: must be virtual on the
    // class so derived overrides stay reachable through IDecomposer.
    public virtual IReadOnlyCollection<string> CanonicalNamesForReadback => Array.Empty<string>();

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

/// <summary>
/// Multi-phase orchestrator with sealed Initialize from <typeparamref name="TSource"/>.
/// Existing non-generic <see cref="DecomposerMultiPhase"/> subclasses migrate in Wave 3.
/// </summary>
public abstract class DecomposerMultiPhase<TSource, TScope> : DecomposerMultiPhase
    where TSource : ISeedSource
    where TScope : ISeedScope
{
    protected ISourceManifest Manifest => SeedSourceManifest<TSource>.Instance;

    public sealed override Hash128 SourceId => TSource.SourceId;
    public sealed override string SourceName => TSource.SourceName;
    public sealed override Hash128 TrustClassId => TSource.TrustClass;

    public int EstimatedBytesPerRecord => TSource.Profile.EstBytesPerRecord;
    public int EstimatedComposeUnitsPerRecord => TSource.Profile.EstComposeUnitsPerRecord;

    /// <summary>Optional vocabulary readback sink filled during sealed Initialize.</summary>
    protected virtual System.Collections.Concurrent.ConcurrentDictionary<string, byte>? VocabularyReadback => null;

    public sealed override async Task InitializeAsync(
        IDecomposerContext context, CancellationToken ct = default)
    {
        await OnBeforeRegisterAsync(context, ct);
        await SourceVocabularyBootstrap.RegisterManifestAsync(
            context, Manifest, VocabularyReadback, ct: ct);
        await OnInitializedAsync(context, ct);
    }

    /// <summary>Optional pre-bootstrap hook (CILI map load, etc.).</summary>
    protected virtual Task OnBeforeRegisterAsync(IDecomposerContext context, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>Optional post-bootstrap hook (extra classifier entities, etc.).</summary>
    protected virtual Task OnInitializedAsync(IDecomposerContext context, CancellationToken ct) =>
        Task.CompletedTask;
}

/// <summary>
/// Extract-only decomposer with sealed Initialize from compile-time
/// <typeparamref name="TSource"/> / <typeparamref name="TScope"/>.
/// </summary>
public abstract class Decomposer<TRecord, TSource, TScope> : Decomposer<TRecord>
    where TSource : ISeedSource
    where TScope : ISeedScope
{
    protected ISourceManifest Manifest => SeedSourceManifest<TSource>.Instance;

    public sealed override Hash128 SourceId => TSource.SourceId;
    public sealed override string SourceName => TSource.SourceName;
    public sealed override Hash128 TrustClassId => TSource.TrustClass;

    public override int EstimatedBytesPerRecord => TSource.Profile.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => TSource.Profile.EstComposeUnitsPerRecord;

    /// <summary>Optional vocabulary readback sink filled during sealed Initialize.</summary>
    protected virtual System.Collections.Concurrent.ConcurrentDictionary<string, byte>? VocabularyReadback => null;

    public sealed override async Task InitializeAsync(
        IDecomposerContext context, CancellationToken ct = default)
    {
        await OnBeforeRegisterAsync(context, ct);
        await SourceVocabularyBootstrap.RegisterManifestAsync(
            context, Manifest, VocabularyReadback, ct: ct);
        await OnInitializedAsync(context, ct);
    }

    /// <summary>Optional pre-bootstrap hook (CILI map load, etc.).</summary>
    protected virtual Task OnBeforeRegisterAsync(IDecomposerContext context, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>Optional post-bootstrap hook.</summary>
    protected virtual Task OnInitializedAsync(IDecomposerContext context, CancellationToken ct) =>
        Task.CompletedTask;
}

/// <summary>Multi-file lane with sealed Initialize from <typeparamref name="TSource"/>.</summary>
public abstract class DecomposerMultiFile<TRecord, TSource, TScope> : DecomposerMultiFile<TRecord>
    where TSource : ISeedSource
    where TScope : ISeedScope
{
    protected ISourceManifest Manifest => SeedSourceManifest<TSource>.Instance;

    public sealed override Hash128 SourceId => TSource.SourceId;
    public sealed override string SourceName => TSource.SourceName;
    public sealed override Hash128 TrustClassId => TSource.TrustClass;

    public override int EstimatedBytesPerRecord => TSource.Profile.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => TSource.Profile.EstComposeUnitsPerRecord;

    protected virtual System.Collections.Concurrent.ConcurrentDictionary<string, byte>? VocabularyReadback => null;

    public sealed override async Task InitializeAsync(
        IDecomposerContext context, CancellationToken ct = default)
    {
        await OnBeforeRegisterAsync(context, ct);
        await SourceVocabularyBootstrap.RegisterManifestAsync(
            context, Manifest, VocabularyReadback, ct: ct);
        await OnInitializedAsync(context, ct);
    }

    protected virtual Task OnBeforeRegisterAsync(IDecomposerContext context, CancellationToken ct) =>
        Task.CompletedTask;

    protected virtual Task OnInitializedAsync(IDecomposerContext context, CancellationToken ct) =>
        Task.CompletedTask;
}

/// <summary>Compose lane with sealed Initialize from <typeparamref name="TSource"/>.</summary>
public abstract class ComposeDecomposer<TRecord, TSource, TScope> : ComposeDecomposer<TRecord>
    where TSource : ISeedSource
    where TScope : ISeedScope
{
    protected ISourceManifest Manifest => SeedSourceManifest<TSource>.Instance;

    public sealed override Hash128 SourceId => TSource.SourceId;
    public sealed override string SourceName => TSource.SourceName;
    public sealed override Hash128 TrustClassId => TSource.TrustClass;

    public override int EstimatedBytesPerRecord => TSource.Profile.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => TSource.Profile.EstComposeUnitsPerRecord;

    protected virtual System.Collections.Concurrent.ConcurrentDictionary<string, byte>? VocabularyReadback => null;

    public sealed override async Task InitializeAsync(
        IDecomposerContext context, CancellationToken ct = default)
    {
        await OnBeforeRegisterAsync(context, ct);
        await SourceVocabularyBootstrap.RegisterManifestAsync(
            context, Manifest, VocabularyReadback, ct: ct);
        await OnInitializedAsync(context, ct);
    }

    protected virtual Task OnBeforeRegisterAsync(IDecomposerContext context, CancellationToken ct) =>
        Task.CompletedTask;

    protected virtual Task OnInitializedAsync(IDecomposerContext context, CancellationToken ct) =>
        Task.CompletedTask;
}

/// <summary>Grammar-ingest lane with sealed Initialize from <typeparamref name="TSource"/>.</summary>
public abstract class GrammarIngestDecomposer<TSource, TScope> : GrammarIngestDecomposer
    where TSource : ISeedSource
    where TScope : ISeedScope
{
    protected ISourceManifest Manifest => SeedSourceManifest<TSource>.Instance;

    public sealed override Hash128 SourceId => TSource.SourceId;
    public sealed override string SourceName => TSource.SourceName;
    public sealed override Hash128 TrustClassId => TSource.TrustClass;

    public override int EstimatedBytesPerRecord => TSource.Profile.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => TSource.Profile.EstComposeUnitsPerRecord;

    protected virtual System.Collections.Concurrent.ConcurrentDictionary<string, byte>? VocabularyReadback => null;

    public sealed override async Task InitializeAsync(
        IDecomposerContext context, CancellationToken ct = default)
    {
        await OnBeforeRegisterAsync(context, ct);
        await SourceVocabularyBootstrap.RegisterManifestAsync(
            context, Manifest, VocabularyReadback, ct: ct);
        await OnInitializedAsync(context, ct);
    }

    protected virtual Task OnBeforeRegisterAsync(IDecomposerContext context, CancellationToken ct) =>
        Task.CompletedTask;

    protected virtual Task OnInitializedAsync(IDecomposerContext context, CancellationToken ct) =>
        Task.CompletedTask;
}

/// <summary>Grammar-compose lane with sealed Initialize from <typeparamref name="TSource"/>.</summary>
public abstract class GrammarComposeDecomposer<TSource, TScope> : GrammarComposeDecomposer
    where TSource : ISeedSource
    where TScope : ISeedScope
{
    protected ISourceManifest Manifest => SeedSourceManifest<TSource>.Instance;

    public sealed override Hash128 SourceId => TSource.SourceId;
    public sealed override string SourceName => TSource.SourceName;
    public sealed override Hash128 TrustClassId => TSource.TrustClass;

    public override int EstimatedBytesPerRecord => TSource.Profile.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => TSource.Profile.EstComposeUnitsPerRecord;

    protected virtual System.Collections.Concurrent.ConcurrentDictionary<string, byte>? VocabularyReadback => null;

    public sealed override async Task InitializeAsync(
        IDecomposerContext context, CancellationToken ct = default)
    {
        await OnBeforeRegisterAsync(context, ct);
        await SourceVocabularyBootstrap.RegisterManifestAsync(
            context, Manifest, VocabularyReadback, ct: ct);
        await OnInitializedAsync(context, ct);
    }

    protected virtual Task OnBeforeRegisterAsync(IDecomposerContext context, CancellationToken ct) =>
        Task.CompletedTask;

    protected virtual Task OnInitializedAsync(IDecomposerContext context, CancellationToken ct) =>
        Task.CompletedTask;
}

