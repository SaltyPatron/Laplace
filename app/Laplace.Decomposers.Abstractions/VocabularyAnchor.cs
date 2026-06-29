using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// The single substrate-native way to make a vocabulary/category/annotation entity legible and
/// walkable in the merkle DAG. Generalizes the complete pattern that previously lived only in
/// <see cref="RelationTypeRegistry.SeedCanonical"/> and <see cref="BootstrapIntentBuilder.AddType"/>:
///
///   (a) the entity row (Vocabulary tier),
///   (b) an optional parent edge (IS_A / IS_TYPED_AS) so the node is reachable up the type hierarchy,
///   (c) a substrate-native HAS_NAME_ALIAS whose target is a codepoint-walk content entity
///       (<see cref="ContentWitnessBatch.Emit"/>) — so render()/realize()/label() reconstruct the name
///       from its own codepoints with NO canonical_names code-table row.
///
/// Every dynamic family (deprel, enhanced-deprel, feature, POS, sense, synset, frame, roleset, lex
/// category) routes through here instead of minting path-hash ids + VocabularyNames readback entries.
/// That is what lets the DAG be walked without an out-of-band side table.
/// </summary>
public static class VocabularyAnchor
{
    /// <summary>
    /// Emit a vocabulary entity legibly. <paramref name="canonicalName"/> becomes a content-walk name
    /// reached by HAS_NAME_ALIAS. When <paramref name="parentId"/> is set, a <paramref name="parentRelation"/>
    /// edge (default IS_A) is attested so the node folds into the type hierarchy. Idempotent within a run
    /// via <paramref name="seen"/> — the name alias + parent edge are emitted at most once per id.
    /// </summary>
    public static void Emit(
        SubstrateChangeBuilder builder,
        Hash128 id,
        Hash128 metaTypeId,
        string canonicalName,
        Hash128 sourceId,
        double trust,
        ISet<Hash128> seen,
        Hash128? parentId = null,
        string parentRelation = "IS_A")
    {
        if (!seen.Add(id)) return;

        builder.AddEntity(new EntityRow(id, EntityTier.Word, metaTypeId, sourceId));

        if (parentId is { } parent)
        {
            builder.AddEntity(new EntityRow(parent, EntityTier.Word, metaTypeId, sourceId));
            builder.AddAttestation(NativeAttestation.Categorical(
                id, parentRelation, parent, sourceId, null, trust));
        }

        // Substrate-native legibility: the name is a codepoint-walk content entity reached by
        // HAS_NAME_ALIAS, so the type never surfaces as a bare hash and needs no canonical_names row.
        if (ContentWitnessBatch.Emit(builder, canonicalName, sourceId) is { } nameId)
            builder.AddAttestation(NativeAttestation.Categorical(
                id, "HAS_NAME_ALIAS", nameId, sourceId, null, trust));
    }
}
