using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// The shared way every decomposer materializes a cross-resource CATEGORY concept whose stable
/// identifier is a string key — a VerbNet class (<c>51.3.1</c>), a PropBank roleset (<c>drop.01</c>),
/// a FrameNet frame (<c>Body_movement</c>), a WordNet sense (<c>drop%2:38:00</c>). The identity is the
/// key decomposed to content (codepoint floor), shared by every resource that cites the same key, with
/// the category as an <c>IS_A</c> attestation — never an <c>OfCanonical("ns/key")</c> blob that bakes
/// the witness + namespace into the id and converges nothing.
///
/// This is <see cref="ConceptAnchor"/>'s sibling: ConceptAnchor resolves a WordNet offset → ILI first
/// (the concept is language-agnostic); CategoryAnchor emits the key verbatim (the key already IS the
/// stable cross-resource identifier). Both end at <see cref="ContentEmitter"/>.
/// </summary>
public static class CategoryAnchor
{
    /// <summary>
    /// Emit the decomposed key + <c>IS_A categoryTypeId</c>; returns the anchor id (null on empty key
    /// or failed decompose). Single-pass: only use in a builder with attestation capacity. For a
    /// two-pass decomposer, emit content with <see cref="ContentEmitter.Emit"/> in the entity pass and
    /// attest the category with <see cref="AttestCategory"/> in the attestation pass.
    /// </summary>
    public static Hash128? Emit(
        SubstrateChangeBuilder b, string key, Hash128 categoryTypeId, Hash128 source, double trust)
    {
        if (string.IsNullOrEmpty(key)) return null;
        Hash128? id = ContentEmitter.Emit(b, key, source);
        if (id is null) return null;
        AttestCategory(b, id.Value, categoryTypeId, source, trust);
        return id;
    }

    /// <summary>Attest an already-emitted key anchor <c>IS_A categoryTypeId</c>.</summary>
    public static void AttestCategory(
        SubstrateChangeBuilder b, Hash128 anchor, Hash128 categoryTypeId, Hash128 source, double trust)
        => b.AddAttestation(NativeAttestation.Categorical(anchor, "IS_A", categoryTypeId, source, trust));

    /// <summary>
    /// The anchor id for a key without emitting — for referencing the concept as an attestation
    /// subject/object after it was emitted (here or by another decomposer). Needs the perfcache loaded.
    /// </summary>
    public static Hash128? Id(string key) =>
        string.IsNullOrEmpty(key) ? null : ContentEmitter.RootId(key);
}
