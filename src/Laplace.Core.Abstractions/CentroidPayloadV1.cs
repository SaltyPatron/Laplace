namespace Laplace.Core.Abstractions;

/// <summary>
/// Managed mirror of the substrate's frozen v1.0 centroid mantissa ABI.
/// Bit positions and field semantics are documented in the native header
/// <c>centroid_abi_v1.h</c>; ANY change here MUST be made in lockstep with
/// the native side and bumps the ABI version.
///
/// Phase 2 / Track D / cross-cutting (centroid mantissa codec).
/// </summary>
public readonly record struct CentroidPayloadV1(
    ulong  PrimeFlags,
    uint   EntityId,
    byte   StructuralFlags,
    ushort LanguageId,
    byte   ModelId,
    byte   Tier,
    uint   Reserved = 0)
{
    public CentroidPayloadV1 WithPrimeFlags(ulong flags)             => this with { PrimeFlags = flags };
    public CentroidPayloadV1 OrPrimeFlags(ulong flags)               => this with { PrimeFlags = PrimeFlags | flags };
    public CentroidPayloadV1 WithStructuralFlags(byte flags)         => this with { StructuralFlags = flags };
    public CentroidPayloadV1 OrStructuralFlags(byte flags)           => this with { StructuralFlags = (byte)(StructuralFlags | flags) };
    public CentroidPayloadV1 WithLanguage(ushort lang)               => this with { LanguageId = lang };
    public CentroidPayloadV1 WithModelId(byte model)                 => this with { ModelId = model };

    public bool HasPrime(ulong mask)     => (PrimeFlags & mask) != 0;
    public bool HasAllPrimes(ulong mask) => (PrimeFlags & mask) == mask;
    public bool HasStructural(byte mask)     => (StructuralFlags & mask) != 0;
    public bool HasAllStructural(byte mask)  => (StructuralFlags & mask) == mask;
}

/// <summary>
/// Frozen v1.0 prime-flag bit constants. These mirror the LAPLACE_FLAG_*
/// macros in <c>centroid_abi_v1.h</c> bit-for-bit. Changing any constant
/// invalidates every centroid in every substrate database.
///
/// The bit positions have NO name in any natural language — they are pure
/// enumerations. cat / neko / gato / chat / кот / 猫 / kissa are peer
/// entities; cross-language equivalence is graph-emergent from ingested
/// sources (WordNet, OMW, Wiktionary, Tatoeba, UD), not from any anchor
/// entity. The flags here just mark per-entity attested categories.
/// </summary>
public static class PrimeFlags
{
    /* Part-of-speech (12 bits, 0..11). UD UPOS-aligned. */
    public const ulong Noun        = 1UL <<  0;
    public const ulong Verb        = 1UL <<  1;
    public const ulong Adjective   = 1UL <<  2;
    public const ulong Adverb      = 1UL <<  3;
    public const ulong Pronoun     = 1UL <<  4;
    public const ulong Preposition = 1UL <<  5;
    public const ulong Determiner  = 1UL <<  6;
    public const ulong Conjunction = 1UL <<  7;
    public const ulong Interjection = 1UL << 8;
    public const ulong Numeral     = 1UL <<  9;
    public const ulong Particle    = 1UL << 10;
    public const ulong Punctuation = 1UL << 11;

    /* Semantic primitives (12 bits, 12..23). */
    public const ulong Animate     = 1UL << 12;
    public const ulong Concrete    = 1UL << 13;
    public const ulong Abstract    = 1UL << 14;
    public const ulong Person      = 1UL << 15;
    public const ulong Place       = 1UL << 16;
    public const ulong Thing       = 1UL << 17;
    public const ulong Action      = 1UL << 18;
    public const ulong Property    = 1UL << 19;
    public const ulong Relation    = 1UL << 20;
    public const ulong Quantity    = 1UL << 21;
    public const ulong Event       = 1UL << 22;
    public const ulong State       = 1UL << 23;

    /* Number (4 bits, 24..27). */
    public const ulong Singular    = 1UL << 24;
    public const ulong Plural      = 1UL << 25;
    public const ulong Dual        = 1UL << 26;
    public const ulong Mass        = 1UL << 27;

    /* Tense / aspect (8 bits, 28..35). */
    public const ulong Past        = 1UL << 28;
    public const ulong Present     = 1UL << 29;
    public const ulong Future      = 1UL << 30;
    public const ulong Perfect     = 1UL << 31;
    public const ulong Imperfect   = 1UL << 32;
    public const ulong Continuous  = 1UL << 33;
    public const ulong Habitual    = 1UL << 34;
    public const ulong Gnomic      = 1UL << 35;

    /* Case (8 bits, 36..43). */
    public const ulong CaseNominative   = 1UL << 36;
    public const ulong CaseAccusative   = 1UL << 37;
    public const ulong CaseDative       = 1UL << 38;
    public const ulong CaseGenitive     = 1UL << 39;
    public const ulong CaseInstrumental = 1UL << 40;
    public const ulong CaseLocative     = 1UL << 41;
    public const ulong CaseAblative     = 1UL << 42;
    public const ulong CaseVocative     = 1UL << 43;

    /* Modality (16 bits, 44..59). All powers of two — OR-combinable bitmask.
     * A composition that spans multiple modalities sets multiple bits. */
    public const ulong Text         = 1UL << 44;
    public const ulong Speech       = 1UL << 45;
    public const ulong Image        = 1UL << 46;
    public const ulong Audio        = 1UL << 47;
    public const ulong Video        = 1UL << 48;
    public const ulong Math         = 1UL << 49;
    public const ulong Code         = 1UL << 50;
    public const ulong Sign         = 1UL << 51;
    public const ulong Structured   = 1UL << 52;
    public const ulong TimeSeries   = 1UL << 53;
    public const ulong Geo          = 1UL << 54;
    public const ulong Network      = 1UL << 55;
    public const ulong Bio          = 1UL << 56;
    public const ulong Cad          = 1UL << 57;
    public const ulong Game         = 1UL << 58;
    public const ulong Encrypted    = 1UL << 59;

    /* Bits 60..63 reserved for v1.x extensions. */
}

/// <summary>
/// Structural flags — 8-bit OR-combinable bitmask at bits 96..103 of the
/// centroid payload. All powers of two; lifted out of prime_flags 52..59
/// to free those bits for modality flag expansion to 16 modalities.
/// </summary>
public static class StructuralFlags
{
    public const byte Negation       = 1 << 0;
    public const byte Interrogative  = 1 << 1;
    public const byte Imperative     = 1 << 2;
    public const byte Conditional    = 1 << 3;
    public const byte Counterfactual = 1 << 4;
    public const byte Modal          = 1 << 5;
    public const byte Evidential     = 1 << 6;
    public const byte SelfReferential = 1 << 7;
}
