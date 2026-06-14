using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// The one place every wordnet-family decomposer (WordNet, OMW, VerbNet, and FrameNet/PropBank
/// via the Predicate Matrix) materializes a synset — so they all converge on the same anchor and
/// no decomposer carries its own keyer. The anchor IS the ILI concept id decomposed to codepoints
/// (content-addressed, deduped at the codepoint floor), with the category as an <c>IS_A</c>
/// attestation and the witness in <c>source_id</c> — never a <c>wordnet/synset/{…}</c> blob.
/// </summary>
public static class ConceptAnchor
{
    private static readonly Hash128 SynsetTypeId = EntityTypeRegistry.WordNetSynset;

    /// <summary>
    /// Pass-1 (content) emit of the decomposed ILI anchor — resolve (offset, RAW ss_type) → ILI and
    /// materialize it as content (codepoints → merkle DAG), returning the anchor id. Adds NO
    /// attestation, so it is safe inside an entities-only builder (the two-pass decomposers commit
    /// attestations with <c>attestationCapacity 0</c> in the entity pass). Returns null when the
    /// synset is unmapped (CILI absent / perfcache not loaded) so the caller skips rather than
    /// fabricate a blob. Pass the raw ss_type (n/v/a/s/r) — satellites must not fold to 'a'.
    /// </summary>
    public static Hash128? EmitAnchor(SubstrateChangeBuilder b, long offset, char ssType, Hash128 source)
    {
        string? ili = SourceEntityIdConventions.WordNetIli(offset, ssType);
        return ili is null ? null : ContentEmitter.Emit(b, ili, source);
    }

    /// <summary>
    /// Pass-2 (category) — attest the already-emitted anchor <c>IS_A WordNet_Synset</c>. This is how
    /// "synset-ness" stays queryable once the identity is content, not a typed blob row.
    /// </summary>
    public static void AttestSynsetCategory(SubstrateChangeBuilder b, Hash128 synId, Hash128 source, double trust)
        => b.AddAttestation(NativeAttestation.Categorical(synId, "IS_A", SynsetTypeId, source, trust));

    /// <summary>
    /// Single-pass convenience (emit anchor + category together) for decomposers that commit
    /// entities and attestations in one builder (VerbNet / FrameNet / PropBank). Two-pass
    /// decomposers (WordNet) use <see cref="EmitAnchor"/> in the entity pass and
    /// <see cref="AttestSynsetCategory"/> in the attestation pass instead.
    /// </summary>
    public static Hash128? EmitSynset(
        SubstrateChangeBuilder b, long offset, char ssType, Hash128 source, double trust)
    {
        Hash128? id = EmitAnchor(b, offset, ssType, source);
        if (id is null) return null;
        AttestSynsetCategory(b, id.Value, source, trust);
        return id;
    }

    /// <summary>
    /// The anchor id for (offset, RAW ss_type) without emitting — for use as an attestation
    /// object/subject after the entity has been emitted elsewhere. Needs the perfcache loaded.
    /// </summary>
    public static Hash128? SynsetId(long offset, char ssType)
    {
        string? ili = SourceEntityIdConventions.WordNetIli(offset, ssType);
        return ili is null ? null : ContentEmitter.RootId(ili);
    }
}
