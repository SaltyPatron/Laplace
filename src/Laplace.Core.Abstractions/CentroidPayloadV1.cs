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
    byte   Modality,
    ushort LanguageId,
    byte   ModelId,
    byte   Tier,
    uint   Reserved = 0)
{
    public CentroidPayloadV1 WithPrimeFlags(ulong flags)        => this with { PrimeFlags = flags };
    public CentroidPayloadV1 OrPrimeFlags(ulong flags)          => this with { PrimeFlags = PrimeFlags | flags };
    public CentroidPayloadV1 WithModality(byte modality)        => this with { Modality = modality };
    public CentroidPayloadV1 WithLanguage(ushort lang)          => this with { LanguageId = lang };
    public CentroidPayloadV1 WithModelId(byte model)            => this with { ModelId = model };

    public bool HasPrime(ulong mask)     => (PrimeFlags & mask) != 0;
    public bool HasAllPrimes(ulong mask) => (PrimeFlags & mask) == mask;
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

    /* Modality kind flags (8 bits, 44..51). Distinct from the per-centroid
     * modality enum byte at bits 96..103: this flag bitmask records which
     * modalities a composition spans (a parallel-corpus sentence might
     * have Text ∪ Audio if it has an audio recording). */
    public const ulong Text     = 1UL << 44;
    public const ulong Speech   = 1UL << 45;
    public const ulong Image    = 1UL << 46;
    public const ulong Audio    = 1UL << 47;
    public const ulong Video    = 1UL << 48;
    public const ulong Math     = 1UL << 49;
    public const ulong Code     = 1UL << 50;
    public const ulong Sign     = 1UL << 51;

    /* Structural (8 bits, 52..59). */
    public const ulong SelfReferential = 1UL << 52;
    public const ulong Negation        = 1UL << 53;
    public const ulong Interrogative   = 1UL << 54;
    public const ulong Imperative      = 1UL << 55;
    public const ulong Conditional     = 1UL << 56;
    public const ulong Counterfactual  = 1UL << 57;
    public const ulong Modal           = 1UL << 58;
    public const ulong Evidential      = 1UL << 59;

    /* Bits 60..63 reserved for v1.x extensions. */
}

/// <summary>Modality enum constants — bits 96..103 of the centroid payload.</summary>
public static class ModalityKind
{
    public const byte Unknown     = 0;
    public const byte Text        = 1;
    public const byte Speech      = 2;
    public const byte Image       = 3;
    public const byte Audio       = 4;
    public const byte Video       = 5;
    public const byte Math        = 6;
    public const byte Code        = 7;
    public const byte Sign        = 8;
    public const byte Structured  = 9;
    public const byte TimeSeries  = 10;
    public const byte Geo         = 11;
    public const byte Network     = 12;
    public const byte Bio         = 13;
    public const byte Cad         = 14;
    public const byte Game        = 15;
    public const byte Encrypted   = 16;
    public const byte Compressed  = 17;
    public const byte Filesystem  = 18;
    /* 19..255 reserved for v1.x. */
}
