using System.Collections.Concurrent;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Monolith / few-file relation-triple source. Subclass implements
/// <see cref="RelationTripleDecomposer.ExtractFileAsync"/> (one file → records) and
/// <see cref="RelationTripleDecomposer.ListInputFiles"/>. Compose/dedupe/COPY is
/// <see cref="RelationTripleHandler"/> via the shared pipeline.
/// </summary>
public abstract class RelationTripleDecomposerBase : RelationTripleDecomposer;

/// <summary>
/// Relation-triple lane with sealed Initialize from compile-time
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
/// Multi-file relation-triple source. Per-file unit is
/// <see cref="DecomposerMultiFile{TRecord}.ExtractFileAsync"/> — same masticate-to-
/// <see cref="RelationTripleRecord"/> job as the monolith base; the pool calls it once
/// per path. Handler is always <see cref="RelationTripleHandler"/>.
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
