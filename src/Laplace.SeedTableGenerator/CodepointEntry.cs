namespace Laplace.SeedTableGenerator;

using Laplace.Core.Abstractions;

/// <summary>
/// Per-codepoint generated entry. Carries everything the compiled C
/// codepoint table holds for one tier-0 atom: substrate identity (entity
/// hash, super-Fibonacci position, Hilbert index), categorical bit flags,
/// and the UCD-derived properties needed by decomposers.
///
/// All fields are content-derived. Hash is BLAKE3 of the codepoint's UTF-8
/// bytes. Position is the canonical super-Fibonacci placement at the
/// codepoint's rank in the ordered seed. Flags are sparse for tier-0
/// (typically just the modality bit + Punctuation/Numeral when general
/// category dictates); the rich grammatical/semantic flag set populates at
/// tier-1+ during decomposer ingest.
/// </summary>
public sealed record CodepointEntry(
    int Codepoint,
    AtomId EntityHash,
    Point4D Position,
    ulong HilbertIndex,
    ulong PrimeFlags,
    string GeneralCategory,
    string Script,
    string Block,
    string Age,
    string BidiClass,
    int CanonicalCombiningClass,
    ushort UcaPrimaryWeight,
    string? Name,
    int? UnihanRadical);
