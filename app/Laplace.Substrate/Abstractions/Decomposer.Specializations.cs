using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

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
