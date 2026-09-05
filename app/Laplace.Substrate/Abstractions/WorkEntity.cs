using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public readonly record struct WorkIdentity(
    Hash128 WorkId,
    Hash128 TitleId,
    Hash128? AuthorId,
    string NormalizedTitle,
    string? NormalizedAuthor);

/// <summary>
/// A work is the ordered native composition <c>[normalized title, normalized author]</c>.
/// Title and author are NFC content roots; the shared native identity-key law trims and collapses
/// Unicode whitespace, applies Unicode full case folding, and emits NFC. If author is absent, the request has one
/// child and the native singleton law returns the title root itself without minting a wrapper.
/// The resulting altitude is therefore derived from its children, never fixed at a tier.
/// </summary>
public static class WorkEntity
{
    public static WorkIdentity Resolve(string title, string? author)
    {
        string normalizedTitle = Normalize(title, nameof(title));
        string? normalizedAuthor = NormalizeOptional(author);
        using TierTree titleTree = Build(normalizedTitle, "title");
        if (normalizedAuthor is null)
            return Compose(
                Root(titleTree), null, default,
                normalizedTitle: normalizedTitle, normalizedAuthor: null);
        using TierTree authorTree = Build(normalizedAuthor, "author");
        return Compose(
            Root(titleTree), Root(authorTree), default,
            normalizedTitle: normalizedTitle, normalizedAuthor: normalizedAuthor);
    }

    public static WorkIdentity Emit(
        SubstrateChangeBuilder builder,
        Hash128 fileId,
        string title,
        string? author)
    {
        ArgumentNullException.ThrowIfNull(builder);
        string normalizedTitle = Normalize(title, nameof(title));
        string? normalizedAuthor = NormalizeOptional(author);
        using TierTree titleTree = Build(normalizedTitle, "title");
        TierTree? authorTree = normalizedAuthor is null ? null : Build(normalizedAuthor, "author");
        try
        {
            OrderedCompositionComponent titleRoot = Root(titleTree);
            OrderedCompositionComponent? authorRoot = authorTree is null ? null : Root(authorTree);
            WorkIdentity identity = Compose(
                titleRoot, authorRoot, fileId, builder.ContentStage,
                normalizedTitle, normalizedAuthor);

            if (!ContentTierSpine.EmitTree(
                    builder, titleTree, fileId, ReadOnlySpan<byte>.Empty, out Hash128 emittedTitle)
                || emittedTitle != identity.TitleId)
                throw new InvalidOperationException("WorkEntity.Emit: title staging changed identity");
            if (authorTree is not null
                && (!ContentTierSpine.EmitTree(
                        builder, authorTree, fileId, ReadOnlySpan<byte>.Empty,
                        out Hash128 emittedAuthor)
                    || emittedAuthor != identity.AuthorId))
                throw new InvalidOperationException("WorkEntity.Emit: author staging changed identity");

            const double trust = SourceTrust.StructuredCorpus;
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                fileId, DocumentSource.Resolve(DocumentRelation.Expresses).Id,
                identity.WorkId, fileId, null, trust));
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                identity.WorkId, DocumentSource.Resolve(DocumentRelation.HasTitle).Id,
                identity.TitleId, fileId, null, trust));
            if (identity.AuthorId is { } authorId)
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    identity.WorkId, DocumentSource.Resolve(DocumentRelation.AuthoredBy).Id,
                    authorId, fileId, null, trust));
            return identity;
        }
        finally
        {
            authorTree?.Dispose();
        }
    }

    private static WorkIdentity Compose(
        in OrderedCompositionComponent title,
        OrderedCompositionComponent? author,
        Hash128 source,
        IntentStage? stage = null,
        string normalizedTitle = "",
        string? normalizedAuthor = null)
    {
        OrderedCompositionComponent[] components = author is { } authorComponent
            ? [title, authorComponent]
            : [title];
        var request = new OrderedCompositionRequest(
            components, EntityTypeRegistry.Document, source, 0);
        OrderedCompositionResult result;
        if (stage is null)
            result = OrderedComposition.ComposeBatch([request])[0];
        else
        {
            Span<OrderedCompositionResult> results = stackalloc OrderedCompositionResult[1];
            OrderedComposition.StageBatch(stage, [request], results);
            result = results[0];
        }
        return new WorkIdentity(
            result.Id, title.Id, author?.Id,
            normalizedTitle, normalizedAuthor);
    }

    private static string Normalize(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        return IdentityKeyNormalization.Normalize(value);
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Normalize(value, nameof(value));

    private static TierTree Build(string value, string field) =>
        ContentTierSpine.BuildTree(Encoding.UTF8.GetBytes(value))
        ?? throw new InvalidOperationException($"WorkEntity: normalized {field} has no content root");

    private static unsafe OrderedCompositionComponent Root(TierTree tree)
    {
        var node = tree.GetNode(tree.NaturalUnitIndex());
        return new OrderedCompositionComponent(
            node.Id, node.Tier, node.Coord[0], node.Coord[1], node.Coord[2], node.Coord[3],
            node.Atom, node.Tier == 0);
    }
}
