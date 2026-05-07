namespace Laplace.Decomposers.Ucd;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

/// <summary>
/// Parses <c>UCA/allkeys.txt</c> — the DUCET (Default Unicode Collation
/// Element Table) defined by UTS #10. The substrate uses the UCA primary
/// collation weight as part of the canonical S³ placement ordering for
/// tier-0 codepoint atoms (after script + general_category, before Unihan
/// radical for CJK, before codepoint integer). Phase 3 / Track E / E3.
///
/// Format per UTS #10:
///   `@version 17.0.0`                                   directive
///   `@implicitweights 17000..187FF; FB00 # Tangut`      implicit-weights directive
///   `0061  ; [.1C47.0020.0002] # LATIN SMALL LETTER A`  per-codepoint mapping
///   `0041  ; [.1C47.0020.0008] # LATIN CAPITAL LETTER A`
///   `0066 0066 ; [.1D27.0020.0002][.0000.0000.0000]    # contraction`
///
/// A collation element is `[VAR.PRIMARY.SECONDARY.TERTIARY]` where VAR
/// is `.` (non-variable) or `*` (variable). One source codepoint sequence
/// maps to one or more collation elements.
/// </summary>
public sealed class UcaAllKeysParser
{
    public static IEnumerable<UcaEntry> Parse(string path)
    {
        using var reader = new StreamReader(path);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty || trimmed[0] == '#')
            {
                continue;
            }

            if (trimmed[0] == '@')
            {
                /* @version / @implicitweights / @backwards / @maxVariable / etc.
                 * The substrate seeder treats @implicitweights as range-coverage
                 * for codepoints not enumerated explicitly. */
                if (TryParseImplicit(line, out var implicitEntry))
                {
                    yield return implicitEntry;
                }
                continue;
            }

            /* Strip any inline `# ...` comment. */
            var hashIdx = line.IndexOf('#', StringComparison.Ordinal);
            var data    = (hashIdx >= 0 ? line[..hashIdx] : line).Trim();
            if (data.Length == 0)
            {
                continue;
            }

            var semi = data.IndexOf(';', StringComparison.Ordinal);
            if (semi < 0)
            {
                continue;
            }

            var lhs = data[..semi].Trim();
            var rhs = data[(semi + 1)..].Trim();

            var cpParts = lhs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var cps     = new int[cpParts.Length];
            for (int i = 0; i < cpParts.Length; ++i)
            {
                cps[i] = int.Parse(cpParts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            var elements = ParseElements(rhs);
            yield return new UcaEntry(cps, elements, IsImplicit: false);
        }
    }

    private static bool TryParseImplicit(string line, out UcaEntry result)
    {
        result = null!;
        const string Prefix = "@implicitweights";
        var idx = line.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return false;
        }
        var rest = line[(idx + Prefix.Length)..].Trim();
        var hash = rest.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            rest = rest[..hash].Trim();
        }
        var semi = rest.IndexOf(';', StringComparison.Ordinal);
        if (semi < 0)
        {
            return false;
        }
        var rangeText = rest[..semi].Trim();
        var weightHex = rest[(semi + 1)..].Trim();

        int start, end;
        var dotIdx = rangeText.IndexOf("..", StringComparison.Ordinal);
        if (dotIdx >= 0)
        {
            start = int.Parse(rangeText[..dotIdx], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            end   = int.Parse(rangeText[(dotIdx + 2)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        else
        {
            start = int.Parse(rangeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            end   = start;
        }
        var weight = (ushort) int.Parse(weightHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        var implicitElement = new UcaCollationElement(
            Variable: false,
            Primary:  weight,
            Secondary: 0x0020,
            Tertiary:  0x0002);

        var seq      = new int[end - start + 1];
        for (int i = 0; i < seq.Length; ++i) { seq[i] = start + i; }
        result = new UcaEntry(seq, new[] { implicitElement }, IsImplicit: true);
        return true;
    }

    private static List<UcaCollationElement> ParseElements(string rhs)
    {
        var result = new List<UcaCollationElement>(1);
        var i      = 0;
        while (i < rhs.Length)
        {
            var open = rhs.IndexOf('[', i);
            if (open < 0)
            {
                break;
            }
            var close = rhs.IndexOf(']', open + 1);
            if (close < 0)
            {
                break;
            }
            var inside = rhs[(open + 1)..close];
            if (inside.Length < 1)
            {
                i = close + 1;
                continue;
            }
            var variable = inside[0] == '*';
            var triple   = inside[1..].Split('.');
            if (triple.Length < 3)
            {
                i = close + 1;
                continue;
            }
            result.Add(new UcaCollationElement(
                Variable:  variable,
                Primary:   ParseHex16(triple[0]),
                Secondary: ParseHex16(triple[1]),
                Tertiary:  ParseHex16(triple[2])));
            i = close + 1;
        }
        return result;
    }

    private static ushort ParseHex16(string s) =>
        ushort.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}

/// <summary>
/// One UCA collation entry — a source codepoint sequence and the ordered
/// list of collation elements it maps to. <see cref="IsImplicit"/> marks
/// entries derived from <c>@implicitweights</c> directives covering large
/// CJK / Tangut blocks.
/// </summary>
public sealed record UcaEntry(
    IReadOnlyList<int> SourceCodepoints,
    IReadOnlyList<UcaCollationElement> Elements,
    bool IsImplicit);

public sealed record UcaCollationElement(
    bool Variable,
    ushort Primary,
    ushort Secondary,
    ushort Tertiary);
