using System.Collections.Concurrent;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Base for every relation-triple source (ATOMIC, ConceptNet, …). A subclass implements
/// ONLY <see cref="Decomposer{TRecord}.ExtractRecordsAsync"/> — pure content → (subject, relation, object)
/// records. Each record composes two tier trees (subject + object) before the edge is
/// emitted; batch/probe/commit sizing uses <see cref="IngestSourceProfile.RelationTriple"/>.
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
