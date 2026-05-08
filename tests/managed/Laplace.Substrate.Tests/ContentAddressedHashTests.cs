namespace Laplace.Substrate.Tests;

using Laplace.Core;
using Laplace.Core.Abstractions;

using Xunit;

/// <summary>
/// Verifies that the substrate's tier-0 atom hashes ARE content-addressed —
/// i.e., recomputing BLAKE3 of a codepoint's UTF-8 bytes via the same native
/// hashing service the generator used yields the same hash that's stored in
/// entity_tier0.tsv. Per CLAUDE.md invariant 1: identity = content. If this
/// test fails the substrate is not actually content-addressed.
/// </summary>
[Collection("GeneratedSubstrate")]
public class ContentAddressedHashTests
{
    private readonly GeneratedSubstrateFixture _fix;

    public ContentAddressedHashTests(GeneratedSubstrateFixture fix) { _fix = fix; }

    [Theory]
    [InlineData(0x0041)]   // LATIN CAPITAL LETTER A
    [InlineData(0x0061)]   // LATIN SMALL LETTER A
    [InlineData(0x0030)]   // DIGIT ZERO
    [InlineData(0x4E2D)]   // CJK UNIFIED IDEOGRAPH 中
    [InlineData(0x1F600)]  // GRINNING FACE emoji
    [InlineData(0x10000)]  // first SMP codepoint
    [InlineData(0x10FFFF)] // last codepoint
    [InlineData(0xD800)]   // first surrogate (synthetic 4-byte encoding)
    [InlineData(0xFFFE)]   // noncharacter
    public void HashOfUtf8Bytes_MatchesStoredHash(int codepoint)
    {
        Skip.IfNotAvailable(_fix);

        var stored = FindAtom(codepoint);
        var utf8   = EncodeCodepoint(codepoint);
        var hashing = new IdentityHashing();
        var recomputed = hashing.AtomId(utf8);

        var storedSpan = stored.EntityHash.AsSpan();
        var recompSpan = recomputed.AsSpan();
        Assert.Equal(storedSpan.Length, recompSpan.Length);
        for (var i = 0; i < storedSpan.Length; ++i)
        {
            Assert.True(
                storedSpan[i] == recompSpan[i],
                $"hash mismatch at byte {i} for U+{codepoint:X}: stored=0x{storedSpan[i]:X2} recomputed=0x{recompSpan[i]:X2}");
        }
    }

    [Theory]
    [InlineData(0x0041)]   // 'A' should be near other Latin uppercase letters
    [InlineData(0x4E2D)]   // 中 should be near other CJK ideographs
    public void Position_IsOnS3_ForKnownCodepoints(int codepoint)
    {
        Skip.IfNotAvailable(_fix);
        var atom = FindAtom(codepoint);
        var norm = System.Math.Sqrt(atom.X * atom.X + atom.Y * atom.Y + atom.Z * atom.Z + atom.W * atom.W);
        Assert.InRange(norm, 1.0 - 1e-9, 1.0 + 1e-9);
    }

    private GeneratedSubstrateFixture.TierZeroRow FindAtom(int codepoint)
    {
        foreach (var a in _fix.Atoms)
        {
            if (a.Codepoint == codepoint) { return a; }
        }
        throw new System.Collections.Generic.KeyNotFoundException($"U+{codepoint:X}");
    }

    private static byte[] EncodeCodepoint(int codepoint)
    {
        // Match SeedDbRowsEmitter / CodepointEntryBuilder encoding exactly:
        // valid runes via System.Text.Rune; surrogates / out-of-range get a
        // synthetic 4-byte big-endian encoding so every codepoint in
        // [0, 0x10FFFF] has distinct content (and therefore a distinct hash).
        if (codepoint < 0 || codepoint > 0x10FFFF)
        {
            return System.BitConverter.GetBytes(codepoint);
        }
        if (System.Text.Rune.IsValid(codepoint))
        {
            var rune = new System.Text.Rune(codepoint);
            System.Span<byte> buf = stackalloc byte[4];
            var written = rune.EncodeToUtf8(buf);
            return buf[..written].ToArray();
        }
        return new byte[]
        {
            (byte)((codepoint >> 24) & 0xFF),
            (byte)((codepoint >> 16) & 0xFF),
            (byte)((codepoint >>  8) & 0xFF),
            (byte)( codepoint        & 0xFF),
        };
    }
}
