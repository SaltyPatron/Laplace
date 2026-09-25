using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// A source is the witness of its observations: WordNet did not make "dog", it
/// observed that dog is a noun. The witness's identity is the content composition of
/// its own self-description [authority, release] (for example [OMW, 2.0]); it is
/// staged as ordinary content and appears on attestations as their source. Entities and
/// physicalities carry no source.
/// </summary>
public static class SourceWitness
{
    public static Hash128 Id(string authority, string release) =>
        Hash128.Merkle(EntityTier.Document, [Root(authority), Root(release)]);

    public static Hash128 Stage(SubstrateChangeBuilder builder, string authority, string release)
    {
        ArgumentNullException.ThrowIfNull(builder);
        Hash128 id = Id(authority, release);
        OrderedCompositionComponent a = ContentEmitter.StageComponent(builder, authority, id)
            ?? throw new InvalidOperationException($"witness authority could not be composed: {authority}");
        OrderedCompositionComponent r = ContentEmitter.StageComponent(builder, release, id)
            ?? throw new InvalidOperationException($"witness release could not be composed: {release}");
        Span<OrderedCompositionResult> composed = stackalloc OrderedCompositionResult[1];
        OrderedComposition.StageBatch(builder.ContentStage,
            [new OrderedCompositionRequest([a, r], EntityTypeRegistry.SourceVersion, id, 0)], composed);
        if (composed[0].Id != id)
            throw new InvalidOperationException("source witness identity changed during composition");
        return id;
    }

    private static Hash128 Root(string value) =>
        ContentEmitter.RootId(value)
        ?? throw new InvalidOperationException($"source witness fragment could not be composed: {value}");
}
