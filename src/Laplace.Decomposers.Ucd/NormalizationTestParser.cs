namespace Laplace.Decomposers.Ucd;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

/// <summary>
/// Parses <c>NormalizationTest.txt</c> from the UCD root. The file defines
/// the conformance test suite for Unicode Normalization Forms (NFC, NFD,
/// NFKC, NFKD). Each non-comment line carries six semicolon-separated
/// columns: <c>source; NFC; NFD; NFKC; NFKD; # comment</c> (the trailing
/// semicolon and comment are present in the actual file).
///
/// Section markers (<c>@Part0</c> through <c>@Part6</c>) separate the test
/// sections defined in UAX #15 — the parser surfaces each section start as
/// a <see cref="NormalizationTestSectionMarker"/> entry so callers can
/// group invariant checks per section.
///
/// Phase 3 / Track E / E2.
///
/// Used to verify <c>UnicodeIcuService</c> (B13) — every form must round-trip
/// per the invariants in the file's header comment.
/// </summary>
public sealed class NormalizationTestParser
{
    public static IEnumerable<INormalizationTestEntry> Parse(string path)
    {
        using var reader = new StreamReader(path);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty)
            {
                continue;
            }
            if (trimmed[0] == '@')
            {
                /* @Part0 / @Part1 / ... section marker. */
                var hash = line.IndexOf('#', StringComparison.Ordinal);
                var name = (hash >= 0 ? line[..hash] : line).Trim();
                yield return new NormalizationTestSectionMarker(name);
                continue;
            }
            if (trimmed[0] == '#')
            {
                continue;
            }

            var hashIdx = line.IndexOf('#', StringComparison.Ordinal);
            var data    = (hashIdx >= 0 ? line[..hashIdx] : line).Trim();
            if (data.Length == 0)
            {
                continue;
            }
            var parts = data.TrimEnd(';').Split(';');
            if (parts.Length < 5)
            {
                continue;
            }
            yield return new NormalizationTestCase(
                Source: ParseHexSequence(parts[0]),
                Nfc:    ParseHexSequence(parts[1]),
                Nfd:    ParseHexSequence(parts[2]),
                Nfkc:   ParseHexSequence(parts[3]),
                Nfkd:   ParseHexSequence(parts[4]));
        }
    }

    private static int[] ParseHexSequence(string s)
    {
        var trimmed = s.Trim();
        if (trimmed.Length == 0)
        {
            return Array.Empty<int>();
        }
        var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var result = new int[tokens.Length];
        for (int i = 0; i < tokens.Length; ++i)
        {
            result[i] = int.Parse(tokens[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        return result;
    }
}

public interface INormalizationTestEntry { }

public sealed record NormalizationTestSectionMarker(string Name) : INormalizationTestEntry;

public sealed record NormalizationTestCase(
    IReadOnlyList<int> Source,
    IReadOnlyList<int> Nfc,
    IReadOnlyList<int> Nfd,
    IReadOnlyList<int> Nfkc,
    IReadOnlyList<int> Nfkd) : INormalizationTestEntry;
