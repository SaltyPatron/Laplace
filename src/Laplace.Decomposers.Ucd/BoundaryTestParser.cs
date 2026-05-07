namespace Laplace.Decomposers.Ucd;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

/// <summary>
/// Parses the UAX #29 / UAX #14 boundary-test files in
/// <c>ucd/auxiliary/</c>: <c>WordBreakTest.txt</c>,
/// <c>SentenceBreakTest.txt</c>, <c>GraphemeBreakTest.txt</c>, and
/// <c>LineBreakTest.txt</c>. All four share the same line format:
///
///   <c>÷ FFFF [÷|×] FFFF [÷|×] ... ÷  # comment</c>
///
/// where U+00F7 (÷) marks a break opportunity and U+00D7 (×) marks "no
/// break here". Used to verify the <c>UnicodeIcuService</c> (B13) produces
/// boundaries identical to the official Unicode conformance set.
///
/// Phase 3 / Track E / E2.
/// </summary>
public sealed class BoundaryTestParser
{
    public const char BreakMarker   = '÷'; // ÷
    public const char NoBreakMarker = '×'; // ×

    public static IEnumerable<BoundaryTestCase> Parse(string path)
    {
        using var reader = new StreamReader(path);
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) != null)
        {
            ++lineNumber;
            var trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty || trimmed[0] == '#')
            {
                continue;
            }
            var hashIdx = line.IndexOf('#', StringComparison.Ordinal);
            var data    = (hashIdx >= 0 ? line[..hashIdx] : line).Trim();
            if (data.Length == 0)
            {
                continue;
            }

            var tokens = data.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            var codepoints = new List<int>();
            var breaks     = new List<bool>(); // breaks[i] = true if there is a break BEFORE codepoints[i] (and one trailing for after the last)

            foreach (var tok in tokens)
            {
                if (tok.Length == 1 && tok[0] == BreakMarker)
                {
                    breaks.Add(true);
                }
                else if (tok.Length == 1 && tok[0] == NoBreakMarker)
                {
                    breaks.Add(false);
                }
                else
                {
                    var cp = int.Parse(tok, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    codepoints.Add(cp);
                }
            }

            yield return new BoundaryTestCase(
                LineNumber: lineNumber,
                Codepoints: codepoints,
                /* breaks length should be codepoints.Count + 1 (boundary
                 * marker before each codepoint, plus one after the last). */
                BreaksBetween: breaks);
        }
    }
}

/// <summary>
/// One UAX #29 / UAX #14 boundary test case. <see cref="BreaksBetween"/>
/// has length <c>Codepoints.Count + 1</c>: position 0 is "break before
/// codepoint 0" (always true at start of string), positions 1..N-1 are
/// "break between codepoint k-1 and k", position N is "break after last
/// codepoint" (always true).
/// </summary>
public sealed record BoundaryTestCase(
    int LineNumber,
    IReadOnlyList<int> Codepoints,
    IReadOnlyList<bool> BreaksBetween);
