namespace Laplace.Pipeline.Abstractions;

using Laplace.Core.Abstractions;

/// <summary>
/// In-process cache of (codepoint integer ↔ atom hash) for the full Unicode
/// codepoint pool. Loaded at startup once Phase 3 has seeded the substrate;
/// immutable thereafter.
///
/// The substrate's tier-0 atom pool is the FULL 1,114,112-codepoint Unicode
/// space (17 planes × 65,536), not just currently-assigned (~155K). Reserved
/// codepoints have atom rows so their S³ positions exist for future Unicode
/// versions to light up.
/// </summary>
public interface ICodepointPool
{
    /// <summary>Total codepoints in the pool — always 1,114,112.</summary>
    int TotalCodepoints { get; }

    /// <summary>Look up the atom hash for a codepoint (0..0x10FFFF).</summary>
    AtomId AtomFor(int codepoint);

    /// <summary>Bulk lookup for a stream of codepoints (avoids per-codepoint dictionary cost in tight loops).</summary>
    void AtomsFor(System.ReadOnlySpan<int> codepoints, System.Span<AtomId> destination);
}
