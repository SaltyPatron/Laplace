namespace Laplace.SeedTableGenerator;

using System.Collections.Generic;
using System.Globalization;
using System.Text;

using Laplace.Core;
using Laplace.Core.Abstractions;
using Laplace.Decomposers.Ucd;

/// <summary>
/// Assembles <see cref="CodepointEntry"/> values from canonically-ordered
/// codepoint records. Calls native services (BLAKE3 hashing, super-Fibonacci
/// placement, Hilbert linearization) via P/Invoke. Pure pipeline — no I/O.
///
/// Per substrate invariants: the entity_hash is BLAKE3 of the codepoint's
/// UTF-8 byte sequence (content). The position is the super-Fibonacci sample
/// at the codepoint's canonical rank (content-derived). The Hilbert index is
/// computed from the position. None of these depend on attestations or
/// mutate over time — the seed is deterministic given the Unicode version.
/// </summary>
public sealed class CodepointEntryBuilder
{
    private readonly IIdentityHashing _hashing;
    private readonly ISuperFibonacci  _superFib;
    private readonly IHilbertCurve    _hilbert;

    public CodepointEntryBuilder(
        IIdentityHashing hashing,
        ISuperFibonacci  superFib,
        IHilbertCurve    hilbert)
    {
        _hashing  = hashing;
        _superFib = superFib;
        _hilbert  = hilbert;
    }

    /// <summary>
    /// Build the full set of CodepointEntry values for the given canonical
    /// ordering. Total = ordering.Count; each codepoint's super-Fibonacci
    /// position is at rank-of-codepoint within ordering.
    /// </summary>
    public IReadOnlyList<CodepointEntry> Build(
        IReadOnlyList<CanonicalOrdering.OrderingKey> ordering)
    {
        var total = ordering.Count;
        var positions = _superFib.Range(0, total, total);

        var result = new CodepointEntry[total];
        for (int rank = 0; rank < total; ++rank)
        {
            var key      = ordering[rank];
            var utf8     = EncodeUtf8(key.Codepoint);
            var hash     = _hashing.AtomId(utf8);
            var position = positions[rank];
            var hilbert  = _hilbert.Index(position);
            var primes   = UcdPropertyToFlags.FromUcd(key.Record.GeneralCategory ?? string.Empty);

            result[rank] = new CodepointEntry(
                Codepoint:               key.Codepoint,
                EntityHash:              hash,
                Position:                position,
                HilbertIndex:            hilbert,
                PrimeFlags:              primes,
                GeneralCategory:         key.Record.GeneralCategory ?? string.Empty,
                Script:                  key.Record.Script ?? string.Empty,
                Block:                   key.Record.Block ?? string.Empty,
                Age:                     key.Record.Age ?? string.Empty,
                BidiClass:               key.Record.BidiClass ?? string.Empty,
                CanonicalCombiningClass: ParseInt(key.Record.CanonicalCombiningClass),
                UcaPrimaryWeight:        key.UcaPrimary,
                Name:                    key.Record.Name,
                UnihanRadical:           key.UnihanRadical == 0 ? null : key.UnihanRadical);
        }
        return result;
    }

    private static byte[] EncodeUtf8(int codepoint)
    {
        // Surrogates and noncharacters still get their UTF-8 representation
        // (the bytes that would encode them if written via System.Text.Rune).
        // For values outside the assigned Unicode range, fall back to a
        // 4-byte synthetic encoding so every codepoint in [0, 1114111] has
        // distinct content and therefore a distinct entity hash.
        if (codepoint < 0 || codepoint > 0x10FFFF)
        {
            return System.BitConverter.GetBytes(codepoint);
        }
        if (System.Text.Rune.IsValid(codepoint))
        {
            var rune = new System.Text.Rune(codepoint);
            Span<byte> buf = stackalloc byte[4];
            var written = rune.EncodeToUtf8(buf);
            return buf[..written].ToArray();
        }
        // Surrogates (U+D800..U+DFFF) and noncharacters: emit a synthetic
        // 4-byte big-endian encoding so the entity hash is distinct.
        return new byte[]
        {
            (byte)((codepoint >> 24) & 0xFF),
            (byte)((codepoint >> 16) & 0xFF),
            (byte)((codepoint >>  8) & 0xFF),
            (byte)( codepoint        & 0xFF),
        };
    }

    private static int ParseInt(string? s) =>
        string.IsNullOrEmpty(s)
            ? 0
            : int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
}
