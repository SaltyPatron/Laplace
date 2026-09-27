using System.Collections.Concurrent;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Provider base for relation-triple sources read as one or a few files. A subclass
/// implements <see cref="RelationTripleDecomposer.ExtractFileAsync"/> (one file → records)
/// and <see cref="RelationTripleDecomposer.ListInputFiles"/>; compose, dedupe and bulk COPY
/// are <see cref="RelationTripleHandler"/> in the shared pipeline.
/// </summary>
public abstract class RelationTripleDecomposerBase : RelationTripleDecomposer;

/// <summary>
/// Relation-triple provider whose Initialize is sealed and driven by compile-time
/// <typeparamref name="TSource"/> / <typeparamref name="TScope"/>.
/// </summary>
public abstract class RelationTripleDecomposerBase<TSource, TScope> : RelationTripleDecomposerBase
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

    protected virtual ConcurrentDictionary<string, byte>? VocabularyReadback => null;

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

/// <summary>
/// Multi-file relation-triple provider. The per-file unit is
/// <see cref="DecomposerMultiFile{TRecord}.ExtractFileAsync"/>, which yields
/// <see cref="RelationTripleRecord"/>s; the shared multi-file pool calls it once per path,
/// and <see cref="RelationTripleHandler"/> composes and persists them.
/// </summary>
public abstract class RelationTripleMultiFileDecomposerBase<TSource, TScope>
    : DecomposerMultiFile<RelationTripleRecord, TSource, TScope>
    where TSource : ISeedSource
    where TScope : ISeedScope
{
    public override int EstimatedBytesPerRecord => IngestSourceProfile.RelationTriple.EstBytesPerRecord;

    public override int EstimatedComposeUnitsPerRecord =>
        IngestSourceProfile.RelationTriple.EstComposeUnitsPerRecord;

    public override bool PerFileCompletion => true;

    protected sealed override IIngestRecordHandler<RelationTripleRecord> CreateHandlerForFile(
        string fileLabel, DecomposerOptions options) =>
        new RelationTripleHandler(SourceId, SourceTrust);

    protected sealed override IngestBatchConfig ConfigForFile(
        string fileLabel, ISubstrateReader? reader, DecomposerOptions options) =>
        IngestPipelineDefaults.RelationTriple(SourceId, fileLabel, options, reader);
}
