using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Working-set configuration of the shared ingest pipeline. Every
/// <see cref="Decomposer{TRecord}"/> takes its batch size, capacities and working-set
/// mode from these presets, sized from the machine and the source profile.
/// </summary>
public static class IngestPipelineDefaults
{
    /// <summary>
    /// Working-set sizes for a source profile, from CPU topology and memory.
    /// </summary>
    public static (int Batch, int ProbeInterval, int RecordCap, int ProbeChunk) ResolveWorkingSet(
        IngestSourceProfile profile,
        DecomposerOptions? options = null)
    {
        // Only an explicit operator --batch overrides the machine/profile sizing.
        int batch = options is { BatchSize: > 1 }
            ? options.BatchSize
            : IngestSizing.ResolveForSource(profile).RecordBatchSize;
        var sized = IngestSizing.ResolveForSource(profile, batch);
        return (batch, sized.WorkingSetProbeInterval, sized.WorkingSetRecordCap, sized.ProbeChunkSize);
    }

    /// <summary>
    /// The record batch size every provider uses: <see cref="IngestSizing"/> for the
    /// profile on this machine, unless the operator passed an explicit batch
    /// (<c>--batch</c>, via <see cref="DecomposerOptions.BatchSize"/>).
    /// </summary>
    public static int ResolveBatch(IngestSourceProfile profile, DecomposerOptions? options) =>
        ResolveWorkingSet(profile, options).Batch;

    /// <summary>
    /// Relation-triple records: each composes subject and object tier trees (see
    /// <see cref="RelationTripleHandler"/>). Batch and probe interval come from
    /// <see cref="IngestSourceProfile.RelationTriple"/>.
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
        DecomposerOptions options,
        ISubstrateReader? reader,
        IngestSourceProfile? profile = null,
        int? attestationCapacity = null,
        int commitEpoch = 0)
    {
        profile ??= IngestSourceProfile.Default;
        var ws = ResolveWorkingSet(profile, options);
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
        Hash128 sourceId, string batchLabelPrefix,
        DecomposerOptions options, ISubstrateReader? reader,
        IngestSourceProfile? profile = null)
    {
        profile ??= IngestSourceProfile.Default;
        var ws = ResolveWorkingSet(profile, options);
        return new()
        {
            SourceId = sourceId,
            BatchLabelPrefix = batchLabelPrefix,
            BatchSize = ws.Batch,
            ProbeChunkSize = ws.ProbeChunk,
            ContainmentReader = reader,
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
    /// Grammar-content configuration, for when the parsed serialization itself is admitted
    /// content. A provider that only parses packaging uses <see cref="Compose"/>, so
    /// delimiters, field names and source-local keys create no second content tree. Same
    /// shape as <see cref="StructuredGrammarIngest.IngestFileAsync"/>.
    /// </summary>
    public static IngestBatchConfig StructuredGrammar(
        Hash128 sourceId,
        string batchLabelPrefix,
        DecomposerOptions options,
        ISubstrateReader? reader,
        double witnessWeight = 1.0,
        int commitEpoch = 0,
        IngestSourceProfile? profile = null)
    {
        profile ??= IngestSourceProfile.Wiktionary;
        var sized = ResolveWorkingSet(profile, options);
        return new()
        {
            SourceId = sourceId,
            BatchLabelPrefix = batchLabelPrefix,
            BatchSize = sized.Batch,
            ProbeChunkSize = sized.ProbeChunk,
            WitnessWeight = witnessWeight,
            CommitEpoch = commitEpoch,
            ContainmentReader = reader,
            MaxInputUnits = options.MaxInputUnits,
            WorkingSet = WorkingSetMode.Enabled,
            WorkingSetProbeInterval = sized.ProbeInterval,
            WorkingSetRecordCap = sized.RecordCap,
            WorkingSetProfile = profile,
        };
    }

    public static IngestBatchConfig CategoryCorrespondence(
        Hash128 sourceId, string batchLabelPrefix,
        DecomposerOptions options, ISubstrateReader? reader,
        IngestSourceProfile? profile = null)
    {
        profile ??= IngestSourceProfile.Default;
        var ws = ResolveWorkingSet(profile, options);
        return new()
        {
            SourceId = sourceId,
            BatchLabelPrefix = batchLabelPrefix,
            BatchSize = ws.Batch,
            ProbeChunkSize = ws.ProbeChunk,
            ContainmentReader = reader,
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
/// Provider base: subclasses extract records and pick a handler; the sealed
/// <see cref="DecomposeAsync"/> always admits them through the shared
/// <see cref="IngestBatchPipeline"/> working-set recipe.
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

    public virtual int EstimatedBytesPerRecord => IngestSizing.DefaultEstBytesPerRecord;

    public virtual int EstimatedComposeUnitsPerRecord => 1;

    public virtual IngestSourceProfile SizingProfile =>
        new(EstimatedBytesPerRecord, EstimatedComposeUnitsPerRecord);

    /// <summary>See <see cref="IDecomposer.PerFileCompletion"/>.</summary>
    public virtual bool PerFileCompletion => false;

    // Virtual on the class, not only the interface default: interface mapping is fixed
    // at the class that lists IDecomposer, so a derived declaration without override
    // would be invisible through IDecomposer and register no readback names.
    public virtual IReadOnlyCollection<string> CanonicalNamesForReadback => Array.Empty<string>();

    public virtual IReadOnlyList<string> DeclaredRelations => Array.Empty<string>();

    protected IngestSourceProfile PipelineProfile => SizingProfile;

    protected abstract IIngestRecordHandler<TRecord> CreateHandler();

    /// <summary>
    /// Option-aware handler factory used by the shared driver; most handlers ignore the
    /// options and inherit this default.
    /// </summary>
    protected virtual IIngestRecordHandler<TRecord> CreateHandler(DecomposerOptions options) =>
        CreateHandler();

    protected abstract IAsyncEnumerable<TRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options, CancellationToken ct);

    protected virtual IngestBatchConfig BuildPipelineConfig(
        IDecomposerContext context, DecomposerOptions options) =>
        IngestPipelineDefaults.Compose(
            SourceId, BatchLabelPrefix, options, context.Reader, PipelineProfile);

    /// <summary>
    /// Whether a single-file record stream may be cut into independent working-set
    /// pipelines. Ordered containers return false: record-local compose still fans across
    /// the shared compose workers, but the ordered structure must see every preceding
    /// record before it is emitted.
    /// </summary>
    protected virtual bool CanSegmentMonolith => true;

    /// <summary>
    /// A non-null stream selects the multi-file scheduling branch of the same driver.
    /// </summary>
    protected virtual IMultiFileRecordStream<TRecord>? CreateMultiFileStream(
        IDecomposerContext context, DecomposerOptions options) => null;

    protected virtual IIngestRecordHandler<TRecord> CreateHandlerForFile(
        string fileLabel, DecomposerOptions options) => CreateHandler(options);

    protected virtual IngestBatchConfig ConfigForFile(
        string fileLabel, ISubstrateReader? reader, DecomposerOptions options) =>
        throw new NotSupportedException(
            $"{GetType().Name} selected multi-file execution without a per-file configuration.");

    public virtual bool PerFileResume => false;

    public abstract Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default);

    public abstract Task<long?> EstimateUnitCountAsync(
        IDecomposerContext context, CancellationToken ct = default);

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected virtual Task OnBeforeDecomposeAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct) =>
        Task.CompletedTask;

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await OnBeforeDecomposeAsync(context, options, ct).ConfigureAwait(false);
        ContainmentReader = context.Reader;
        if (options.DryRun) yield break;

        var multiFileStream = CreateMultiFileStream(context, options);
        if (multiFileStream is not null)
        {
            IngestBatchPipeline.PerFileResumePlan? resume =
                PerFileResume && context.Reader is { } reader
                    ? new IngestBatchPipeline.PerFileResumePlan(
                        reader, LayerOrder, options.ReObservePresent, SourceId)
                    {
                        Resolved = new System.Collections.Concurrent.ConcurrentDictionary<
                            string, (Hash128? Root, bool Skip)>(StringComparer.Ordinal),
                    }
                    : null;

            await foreach (var change in IngestBatchPipeline.RunMultiFileAsync(
                               multiFileStream,
                               label => CreateHandlerForFile(label, options),
                               label => ConfigForFile(label, context.Reader, options)
                                   .WithCanonicalNamesProvider(() =>
                                   {
                                       var names = new HashSet<string>(
                                           CanonicalNamesForReadback, StringComparer.Ordinal)
                                       {
                                           $"substrate/source/{SourceName}/v1",
                                       };
                                       return names;
                                   }),
                               maxTotalUnits: options.MaxInputUnits,
                               fileWorkers: IngestTopology.Current.FileWorkers,
                               isolateFileFailures: PerFileCompletion,
                               resume: resume,
                               ct: ct))
                yield return change.WithSourcePrior(SourceId, SourceTrust);
            yield break;
        }

        var stream = new AsyncEnumerableRecordStream<TRecord>(
            ExtractRecordsAsync(context.EcosystemPath, options, ct));

        IngestBatchConfig BuildConfig() => IngestPipelineDefaults.ApplyMaxInputUnits(
            BuildPipelineConfig(context, options), options);

        // Cut a single-file record stream on record boundaries into N independent
        // working-set pipelines on the same pool multi-file sources use. Content addressing
        // merges cross-segment duplicates (same content, same id) with no coordination.
        // Capped runs stay serial (ResolveSegments → 1) so the input-unit stop is exact.
        int segments = CanSegmentMonolith ? MonolithSegmenter.ResolveSegments(BuildConfig()) : 1;
        if (segments <= 1)
        {
            await foreach (var change in IngestBatchPipeline.RunAsync(
                               stream, CreateHandler(options), BuildConfig(), ct))
                yield return change.WithSourcePrior(SourceId, SourceTrust);
            yield break;
        }

        await foreach (var change in MonolithSegmenter.RunSegmentedAsync(
                           stream,
                           _ => CreateHandler(options),
                           _ => BuildConfig(),
                           segments,
                           BatchLabelPrefix,
                           ct))
            yield return change.WithSourcePrior(SourceId, SourceTrust);
    }

}

/// <summary>
/// Multi-file provider admitted through <see cref="IngestBatchPipeline.RunMultiFileAsync"/>.
/// The unit of work is <see cref="ExtractFileAsync"/> (one file → records), called once per
/// claimed path. Sources implement <see cref="ListFiles"/> and <see cref="ExtractFileAsync"/>;
/// <see cref="CreateMultiFileStream"/> is overridden only when the input is not a path list.
/// </summary>
public abstract class DecomposerMultiFile<TRecord> : Decomposer<TRecord>
{
    public override bool PerFileCompletion => true;

    /// <summary>Cheap path/label enumeration — reads nothing.</summary>
    protected virtual IReadOnlyList<(string Path, string Label)> ListFiles(
        string ecosystemPath, DecomposerOptions options) =>
        throw new NotSupportedException(
            $"{GetType().Name}: implement ListFiles+ExtractFileAsync, or override CreateMultiFileStream.");

    /// <summary>
    /// Parses one file into records; the multi-file pool calls it per claimed path.
    /// </summary>
    protected virtual IAsyncEnumerable<TRecord> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options, CancellationToken ct) =>
        throw new NotSupportedException(
            $"{GetType().Name}: implement ExtractFileAsync, or override CreateMultiFileStream.");

    /// <summary>
    /// Default: <see cref="PathListMultiFileStream{TRecord}"/> over <see cref="ListFiles"/>,
    /// each file opened via <see cref="ExtractFileAsync"/>.
    /// </summary>
    protected override IMultiFileRecordStream<TRecord> CreateMultiFileStream(
        IDecomposerContext context, DecomposerOptions options)
    {
        IReadOnlyList<(string Path, string Label)> files = IngestInput.ResolveScheduledFiles(
            context.SelectedArtifacts,
            context.HasArtifactGraph ? [] : ListFiles(context.EcosystemPath, options));
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, label) in files)
        {
            if (string.IsNullOrWhiteSpace(label))
                throw new InvalidOperationException(
                    $"{GetType().Name} returned an empty multi-file job label.");
            if (!labels.Add(label))
                throw new InvalidOperationException(
                    $"{GetType().Name} returned duplicate multi-file job label '{label}'. "
                    + "File labels are journal identities and must be unique within a run.");
        }
        var scheduled = MultiFileScheduler.Schedule(files, options.MaxInputUnits);
        return new PathListMultiFileStream<TRecord>(scheduled, ExtractFileAsync, options);
    }

    protected abstract override IIngestRecordHandler<TRecord> CreateHandlerForFile(
        string fileLabel, DecomposerOptions options);

    protected abstract override IngestBatchConfig ConfigForFile(
        string fileLabel, ISubstrateReader? reader, DecomposerOptions options);

    /// <summary>
    /// Per-file resume: each finished file's boundary receipts a completion on the file's
    /// content identity, and a restarted run skips completed files before opening them.
    /// Testimony is not idempotent, so this keeps a restart from folding an applied file
    /// twice; an interrupted run re-reads only the file that was mid-apply. The content
    /// fingerprint is streamed through a bounded pooled buffer.
    /// </summary>
    public override bool PerFileResume => true;

    protected sealed override IIngestRecordHandler<TRecord> CreateHandler() =>
        throw new NotSupportedException(
            $"{GetType().Name} uses multi-file streaming; use CreateHandlerForFile instead.");

    /// <summary>
    /// Serial concatenation of <see cref="ExtractFileAsync"/> over <see cref="ListFiles"/>.
    /// The shared driver uses the parallel pool instead; this makes the same unit callable
    /// as one record stream.
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
/// Imperative-compose provider: each record → callback into <see cref="SubstrateChangeBuilder"/>.
/// </summary>
public abstract class ComposeDecomposer<TRecord> : Decomposer<TRecord>
{
    protected abstract void Compose(TRecord record, SubstrateChangeBuilder builder);

    protected virtual long UnitsPerRecord(TRecord record) => 1;
    protected virtual long EstimatedOutputRows(TRecord record) => 1;

    /// <summary>
    /// The compose callback runs in <see cref="IIngestDeferredUnit.DrainInto"/> (serial).
    /// A source whose <see cref="ContentTierSpine"/> work should run on the parallel compose
    /// fan overrides this with a handler that builds trees in
    /// <see cref="IIngestRecordHandler{TRecord}.CreateDeferredUnit"/>.
    /// </summary>
    protected override IIngestRecordHandler<TRecord> CreateHandler() =>
        new DirectComposeHandler<TRecord>(
            Compose,
            unitsPerRecord: UnitsPerRecord,
            estimatedOutputRows: EstimatedOutputRows);

    protected override IngestBatchConfig BuildPipelineConfig(
        IDecomposerContext context, DecomposerOptions options) =>
        IngestPipelineDefaults.Compose(
            SourceId, BatchLabelPrefix, options, context.Reader, PipelineProfile);
}

/// <summary>
/// Imperative-compose provider over many files: the multi-file worker pool with the
/// <see cref="DirectComposeHandler{TRecord}"/>. Files carry no cross-file ordering
/// (references resolve by content address), so they compose in parallel like any other
/// multi-file source.
/// </summary>
public abstract class ComposeDecomposerMultiFile<TRecord> : DecomposerMultiFile<TRecord>
{
    protected abstract void Compose(TRecord record, SubstrateChangeBuilder builder);


    protected sealed override IIngestRecordHandler<TRecord> CreateHandlerForFile(
        string fileLabel, DecomposerOptions options) =>
        new DirectComposeHandler<TRecord>(Compose);

    // Per-file label, not BatchLabelPrefix: with concurrent workers the batch label is what
    // attributes a batch to its input file in the run journal.
    protected override IngestBatchConfig ConfigForFile(
        string fileLabel, ISubstrateReader? reader, DecomposerOptions options) =>
        IngestPipelineDefaults.Compose(
            SourceId, fileLabel, options, reader, PipelineProfile);
}

/// <summary>Whole-file grammar compose on the generic multi-file scheduler.</summary>
public abstract class GrammarComposeDecomposerMultiFile : DecomposerMultiFile<GrammarComposeRecord>
{
    protected sealed override IIngestRecordHandler<GrammarComposeRecord> CreateHandlerForFile(
        string fileLabel, DecomposerOptions options) =>
        new GrammarComposeHandler(SourceId, SourceTrust, ContainmentReader);

    protected override IngestBatchConfig ConfigForFile(
        string fileLabel, ISubstrateReader? reader, DecomposerOptions options) =>
        IngestPipelineDefaults.GrammarCompose(
            SourceId, fileLabel, options, reader, PipelineProfile);

}

public abstract class RelationTripleDecomposer : Decomposer<RelationTripleRecord>
{
    public override int EstimatedBytesPerRecord => IngestSourceProfile.RelationTriple.EstBytesPerRecord;

    public override int EstimatedComposeUnitsPerRecord =>
        IngestSourceProfile.RelationTriple.EstComposeUnitsPerRecord;

    protected virtual ConcurrentIdSet? SourceNodeDeclarations => null;

    protected sealed override IIngestRecordHandler<RelationTripleRecord> CreateHandler() =>
        new RelationTripleHandler(SourceId, SourceTrust, SourceNodeDeclarations);

    protected override IngestBatchConfig BuildPipelineConfig(
        IDecomposerContext context, DecomposerOptions options) =>
        IngestPipelineDefaults.RelationTriple(SourceId, BatchLabelPrefix, options, context.Reader);

    /// <summary>Input paths; often a single file.</summary>
    protected abstract IReadOnlyList<string> ListInputFiles(
        string ecosystemPath, DecomposerOptions options);

    /// <summary>
    /// Parses one file into triples; multi-file relation-triple sources use the same shape
    /// via <see cref="DecomposerMultiFile{TRecord}.ExtractFileAsync"/>.
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
            SourceId, BatchLabelPrefix, options, context.Reader, PipelineProfile);

}

/// <summary>
/// Structured-grammar provider: row parse → <see cref="GrammarIngestHandler"/>.
/// Subclasses supply record streams (file, parallel file, multi-file).
/// </summary>
public abstract class GrammarIngestDecomposer : Decomposer<GrammarIngestRecord>
{
    protected abstract string ModalityId { get; }
    protected abstract IGrammarWitness CreateWitness(DecomposerOptions options);
    protected virtual double WitnessWeight => 1.0;
    protected virtual int CommitEpoch => 0;
    protected virtual Hash128? ContextId => null;

    protected sealed override IIngestRecordHandler<GrammarIngestRecord> CreateHandler() =>
        throw new NotSupportedException("Grammar ingest handlers require the active decomposer options.");

    protected sealed override IIngestRecordHandler<GrammarIngestRecord> CreateHandler(
        DecomposerOptions options) =>
        new GrammarIngestHandler(SourceId, ModalityId, CreateWitness(options), ContextId);

    protected override IngestBatchConfig BuildPipelineConfig(
        IDecomposerContext context, DecomposerOptions options) =>
        IngestPipelineDefaults.StructuredGrammar(
            SourceId, BatchLabelPrefix, options, context.Reader,
            WitnessWeight, CommitEpoch, PipelineProfile);

}

public abstract class CategoryCorrespondenceDecomposer : Decomposer<CategoryCorrespondenceRecord>
{
    protected sealed override IIngestRecordHandler<CategoryCorrespondenceRecord> CreateHandler() =>
        new CategoryCorrespondenceHandler(SourceId, SourceTrust);

    protected override IngestBatchConfig BuildPipelineConfig(
        IDecomposerContext context, DecomposerOptions options) =>
        IngestPipelineDefaults.CategoryCorrespondence(
            SourceId, BatchLabelPrefix, options, context.Reader);
}

/// <summary>
/// Provider whose input has several parts (e.g. WordNet data/sense/exc/sent, model
/// tokenizer/recipe). Each phase is a <see cref="DecomposerPhase{T}"/> or
/// <see cref="ComposeDecomposerPhase{T}"/> run through <see cref="RunPhaseAsync"/>;
/// <see cref="DecomposeAsync"/> is sealed and subclasses implement <see cref="RunIngestAsync"/>.
/// </summary>
public abstract class DecomposerMultiPhase : IDecomposer
{
    public virtual IngestSourceProfile SizingProfile => IngestSourceProfile.Default;

    private long _runUnitsConsumed;
    private long _runMaxInputUnits;
    private HashSet<string>? _runSelectedArtifactPaths;
    private HashSet<string>? _runVisitedArtifactPaths;
    private readonly object _artifactClaimGate = new();

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

    public virtual IReadOnlyList<string> DeclaredRelations => Array.Empty<string>();

    protected abstract IAsyncEnumerable<SubstrateChange> RunIngestAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct);

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (options.DryRun) yield break;
        _runUnitsConsumed = 0;
        _runMaxInputUnits = options.MaxInputUnits;
        _runSelectedArtifactPaths = context.SelectedArtifacts
            .Select(static artifact => Path.GetFullPath(artifact.Path))
            .ToHashSet(StringComparer.Ordinal);
        _runVisitedArtifactPaths = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var change in RunIngestAsync(context, options, ct))
        {
            yield return change;
            _runUnitsConsumed += change.Metadata.InputUnitsConsumed;
            if (_runMaxInputUnits > 0 && _runUnitsConsumed >= _runMaxInputUnits)
                yield break;
        }
        if (_runMaxInputUnits == 0 && _runSelectedArtifactPaths.Count > 0)
        {
            string[] omitted = _runSelectedArtifactPaths
                .Except(_runVisitedArtifactPaths)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();
            if (omitted.Length > 0)
                throw new InvalidOperationException(
                    $"{SourceName} completed without consuming selected manifest artifacts: "
                    + string.Join(", ", omitted));
        }
    }

    protected async IAsyncEnumerable<SubstrateChange> ApplyBarrierAsync(
        string label,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var barrier = new IngestApplyBarrier();
        yield return IngestBatchPipeline.BuildApplyBarrier(SourceId, label, barrier);
        await barrier.WaitAsync(ct).ConfigureAwait(false);
    }

    protected async IAsyncEnumerable<SubstrateChange> RunPhaseAsync(
        IDecomposer phase,
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        DecomposerOptions phaseOptions = options;
        if (_runMaxInputUnits > 0)
        {
            long remaining = _runMaxInputUnits - _runUnitsConsumed;
            if (remaining <= 0) yield break;
            phaseOptions = options with { MaxInputUnits = remaining };
        }

        try
        {
            await foreach (var change in phase.DecomposeAsync(context, phaseOptions, ct))
                // Each phase's changes keep their own source and prior declarations; the
                // container does not substitute its identity or trust class.
                yield return change;
        }
        finally
        {
            await phase.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs a file-backed phase, publishing the same started/composed/committed journal
    /// events as the multi-file pipeline.
    /// </summary>
    protected async IAsyncEnumerable<SubstrateChange> RunPhaseAsync(
        IDecomposer phase,
        IDecomposerContext context,
        DecomposerOptions options,
        string fileLabel,
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        fileLabel = ClaimArtifact(context, path, fileLabel);
        var observability = Laplace.Ingestion.IngestObservabilityScope.Current;
        long bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
        observability.OnFileStarted(phase.SourceName, fileLabel, bytes);
        long records = 0, entities = 0, physicalities = 0, attestations = 0;
        await foreach (var change in RunPhaseAsync(phase, context, options, ct))
        {
            records += change.Metadata.InputUnitsConsumed;
            entities += change.Entities.Length;
            physicalities += change.Physicalities.Length;
            attestations += change.Attestations.Length;
            yield return IngestBatchPipeline.BindFileLabel(change, fileLabel);
        }
        observability.OnFileComposed(
            phase.SourceName, fileLabel, null,
            records, entities, physicalities, attestations);
        yield return IngestBatchPipeline.BuildPeriodBoundary(phase.SourceId, fileLabel);
    }

    protected string ClaimArtifact(
        IDecomposerContext context, string path, string fallbackLabel)
    {
        if (_runSelectedArtifactPaths is not { Count: > 0 })
            return fallbackLabel;

        string fullPath = Path.GetFullPath(path);
        if (!_runSelectedArtifactPaths.Contains(fullPath))
            throw new InvalidOperationException(
                $"{SourceName} attempted undeclared artifact '{fullPath}'.");
        lock (_artifactClaimGate)
        {
            if (!_runVisitedArtifactPaths!.Add(fullPath))
                throw new InvalidOperationException(
                    $"{SourceName} attempted to consume selected artifact more than once: '{fullPath}'.");
        }
        return context.SelectedArtifacts
            .Single(artifact => string.Equals(
                Path.GetFullPath(artifact.Path), fullPath, StringComparison.Ordinal))
            .FileLabel;
    }
}

/// <summary>
/// Multi-phase provider with sealed Initialize from <typeparamref name="TSource"/>.
/// </summary>
public abstract class DecomposerMultiPhase<TSource, TScope> : ArtifactDecomposerMultiPhase
    where TSource : ISeedSource
    where TScope : ISeedScope
{
    protected ISourceManifest Manifest => SeedSourceManifest<TSource>.Instance;

    public sealed override Hash128 SourceId => TSource.SourceId;
    public sealed override string SourceName => TSource.SourceName;
    public sealed override Hash128 TrustClassId => TSource.TrustClass;

    public sealed override IReadOnlyList<string> DeclaredRelations => TSource.Relations;

    public int EstimatedBytesPerRecord => TSource.Profile.EstBytesPerRecord;
    public int EstimatedComposeUnitsPerRecord => TSource.Profile.EstComposeUnitsPerRecord;
    public override IngestSourceProfile SizingProfile => TSource.Profile;

    /// <summary>Optional vocabulary readback sink filled during sealed Initialize.</summary>
    protected virtual System.Collections.Concurrent.ConcurrentDictionary<string, byte>? VocabularyReadback => null;

    public sealed override async Task InitializeAsync(
        IDecomposerContext context, CancellationToken ct = default)
    {
        await OnBeforeRegisterAsync(context, ct);
        await SourceVocabularyBootstrap.RegisterManifestAsync(
            context, Manifest, VocabularyReadback,
            depositLicense: LayerOrder != 0, ct: ct);
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

    public sealed override IReadOnlyList<string> DeclaredRelations => TSource.Relations;

    public override int EstimatedBytesPerRecord => TSource.Profile.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => TSource.Profile.EstComposeUnitsPerRecord;
    public override IngestSourceProfile SizingProfile => TSource.Profile;

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

/// <summary>Multi-file provider with sealed Initialize from <typeparamref name="TSource"/>.</summary>
public abstract class DecomposerMultiFile<TRecord, TSource, TScope> : DecomposerMultiFile<TRecord>
    where TSource : ISeedSource
    where TScope : ISeedScope
{
    protected ISourceManifest Manifest => SeedSourceManifest<TSource>.Instance;

    public sealed override Hash128 SourceId => TSource.SourceId;
    public sealed override string SourceName => TSource.SourceName;
    public sealed override Hash128 TrustClassId => TSource.TrustClass;

    public sealed override IReadOnlyList<string> DeclaredRelations => TSource.Relations;

    public override int EstimatedBytesPerRecord => TSource.Profile.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => TSource.Profile.EstComposeUnitsPerRecord;
    public override IngestSourceProfile SizingProfile => TSource.Profile;

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

/// <summary>Compose provider with sealed Initialize from compile-time
/// <typeparamref name="TSource"/> / <typeparamref name="TScope"/>.</summary>
public abstract class ComposeDecomposer<TRecord, TSource, TScope> : ComposeDecomposer<TRecord>
    where TSource : ISeedSource
    where TScope : ISeedScope
{
    protected ISourceManifest Manifest => SeedSourceManifest<TSource>.Instance;

    public sealed override Hash128 SourceId => TSource.SourceId;
    public sealed override string SourceName => TSource.SourceName;
    public sealed override Hash128 TrustClassId => TSource.TrustClass;

    public sealed override IReadOnlyList<string> DeclaredRelations => TSource.Relations;

    public override int EstimatedBytesPerRecord => TSource.Profile.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => TSource.Profile.EstComposeUnitsPerRecord;
    public override IngestSourceProfile SizingProfile => TSource.Profile;

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

/// <summary>Multi-file compose provider with sealed manifest initialization.</summary>
public abstract class ComposeDecomposerMultiFile<TRecord, TSource, TScope>
    : ComposeDecomposerMultiFile<TRecord>
    where TSource : ISeedSource
    where TScope : ISeedScope
{
    protected ISourceManifest Manifest => SeedSourceManifest<TSource>.Instance;

    public sealed override Hash128 SourceId => TSource.SourceId;
    public sealed override string SourceName => TSource.SourceName;
    public sealed override Hash128 TrustClassId => TSource.TrustClass;

    public sealed override IReadOnlyList<string> DeclaredRelations => TSource.Relations;
    public override int EstimatedBytesPerRecord => TSource.Profile.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => TSource.Profile.EstComposeUnitsPerRecord;
    public override IngestSourceProfile SizingProfile => TSource.Profile;

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

/// <summary>Multi-file whole-grammar provider with sealed manifest initialization.</summary>
public abstract class GrammarComposeDecomposerMultiFile<TSource, TScope>
    : GrammarComposeDecomposerMultiFile
    where TSource : ISeedSource
    where TScope : ISeedScope
{
    protected ISourceManifest Manifest => SeedSourceManifest<TSource>.Instance;

    public sealed override Hash128 SourceId => TSource.SourceId;
    public sealed override string SourceName => TSource.SourceName;
    public sealed override Hash128 TrustClassId => TSource.TrustClass;

    public sealed override IReadOnlyList<string> DeclaredRelations => TSource.Relations;
    public override int EstimatedBytesPerRecord => TSource.Profile.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => TSource.Profile.EstComposeUnitsPerRecord;
    public override IngestSourceProfile SizingProfile => TSource.Profile;

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

/// <summary>Grammar-ingest provider with sealed Initialize from <typeparamref name="TSource"/>.</summary>
public abstract class GrammarIngestDecomposer<TSource, TScope> : GrammarIngestDecomposer
    where TSource : ISeedSource
    where TScope : ISeedScope
{
    protected ISourceManifest Manifest => SeedSourceManifest<TSource>.Instance;

    public sealed override Hash128 SourceId => TSource.SourceId;
    public sealed override string SourceName => TSource.SourceName;
    public sealed override Hash128 TrustClassId => TSource.TrustClass;

    public sealed override IReadOnlyList<string> DeclaredRelations => TSource.Relations;

    public override int EstimatedBytesPerRecord => TSource.Profile.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => TSource.Profile.EstComposeUnitsPerRecord;
    public override IngestSourceProfile SizingProfile => TSource.Profile;

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

/// <summary>Grammar-compose provider with sealed Initialize from <typeparamref name="TSource"/>.</summary>
public abstract class GrammarComposeDecomposer<TSource, TScope> : GrammarComposeDecomposer
    where TSource : ISeedSource
    where TScope : ISeedScope
{
    protected ISourceManifest Manifest => SeedSourceManifest<TSource>.Instance;

    public sealed override Hash128 SourceId => TSource.SourceId;
    public sealed override string SourceName => TSource.SourceName;
    public sealed override Hash128 TrustClassId => TSource.TrustClass;

    public sealed override IReadOnlyList<string> DeclaredRelations => TSource.Relations;

    public override int EstimatedBytesPerRecord => TSource.Profile.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => TSource.Profile.EstComposeUnitsPerRecord;
    public override IngestSourceProfile SizingProfile => TSource.Profile;

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
