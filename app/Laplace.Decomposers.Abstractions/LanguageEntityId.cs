using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Canonical formula for ISO 639-3 language entity IDs. Every decomposer
/// that references a language entity MUST derive the ID through this class
/// so that cross-decomposer language entity convergence is guaranteed —
/// per ADR 0016 + content-addressing invariant (RULES R5).
/// </summary>
public static class LanguageEntityId
{
    /// <summary>
    /// Returns the canonical BLAKE3-128 entity ID for an ISO 639-3 language
    /// code. The canonical form is <c>"language:{lowercaseCode}"</c> in UTF-8.
    ///
    /// <para>ISODecomposer emits language entities with exactly this ID.
    /// OMW, UD, Wiktionary, Tatoeba, and ConceptNet MUST reference language
    /// entities via this method — not via their own ad-hoc derivations.</para>
    /// </summary>
    public static Hash128 FromIso639_3(string iso3Code)
    {
        ArgumentNullException.ThrowIfNull(iso3Code);
        return Hash128.OfCanonical($"language:{iso3Code.ToLowerInvariant()}");
    }
}
