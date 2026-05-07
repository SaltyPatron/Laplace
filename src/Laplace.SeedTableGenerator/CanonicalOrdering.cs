namespace Laplace.SeedTableGenerator;

using System;
using System.Collections.Generic;
using System.Linq;

using Laplace.Decomposers.Ucd;

/// <summary>
/// Canonical placement ordering for tier-0 codepoint atoms on S³. The
/// super-Fibonacci sample at rank <c>i</c> of <c>total</c> is assigned to
/// the codepoint at index <c>i</c> in this ordering. Ordering keys, in
/// priority:
///
///   1. Script (UCD <c>sc</c>)
///   2. General Category (UCD <c>gc</c>)
///   3. UCA primary collation weight (DUCET, allkeys.txt) — falls back to
///      0 when the codepoint has no UCA entry (variable-weight characters
///      collapse together at primary 0)
///   4. Unihan kRSUnicode radical for CJK Unified Ideographs (only relevant
///      for the Hani script); 0 for non-CJK
///   5. Codepoint integer (final tiebreaker, guarantees deterministic order)
///
/// Two codepoints with identical (script, gc, uca_primary, radical)
/// distinguish via the codepoint integer — there are NO ties in the final
/// ordering, so the super-Fibonacci position assignment is bijective.
/// </summary>
public static class CanonicalOrdering
{
    public sealed record OrderingKey(
        int Codepoint,
        UcdCodepointRecord Record,
        ushort UcaPrimary,
        int UnihanRadical);

    /// <summary>
    /// Sort codepoint records by the canonical ordering. Returns a stable
    /// enumeration whose index is the super-Fibonacci rank for the codepoint
    /// at that index. Caller passes the per-codepoint UCA primary weight
    /// (0 if absent) and Unihan radical (0 if absent).
    /// </summary>
    public static IReadOnlyList<OrderingKey> Sort(
        IEnumerable<UcdCodepointRecord> records,
        Func<int, ushort> ucaPrimaryFor,
        Func<int, int> unihanRadicalFor)
    {
        var keys = new List<OrderingKey>();
        foreach (var record in records)
        {
            // Range entries (CJK Ideographs, Hangul Syllables, PUA, Surrogates)
            // expand to one ordering key per codepoint in the range.
            for (int cp = record.FirstCodepoint; cp <= record.LastCodepoint; ++cp)
            {
                keys.Add(new OrderingKey(
                    Codepoint:      cp,
                    Record:         record,
                    UcaPrimary:     ucaPrimaryFor(cp),
                    UnihanRadical:  unihanRadicalFor(cp)));
            }
        }

        keys.Sort(static (a, b) =>
        {
            var c = string.CompareOrdinal(a.Record.Script ?? string.Empty, b.Record.Script ?? string.Empty);
            if (c != 0) { return c; }
            c = string.CompareOrdinal(a.Record.GeneralCategory ?? string.Empty, b.Record.GeneralCategory ?? string.Empty);
            if (c != 0) { return c; }
            c = a.UcaPrimary.CompareTo(b.UcaPrimary);
            if (c != 0) { return c; }
            c = a.UnihanRadical.CompareTo(b.UnihanRadical);
            if (c != 0) { return c; }
            return a.Codepoint.CompareTo(b.Codepoint);
        });

        return keys;
    }
}
