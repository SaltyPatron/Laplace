namespace Laplace.Decomposers.Ucd;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

/// <summary>
/// Generic semicolon-separated range-file parser for auxiliary Unicode FTP
/// files NOT covered by <c>ucd.all.flat.xml</c>: emoji-sequences.txt,
/// IdnaMappingTable.txt, IdentifierStatus.txt, IdentifierType.txt,
/// emoji-test.txt, etc. Format:
///   <c>FFFF[..FFFF] ; field1 [; field2 [; ...]] # comment</c>
///
/// Phase 3 / Track E / E2.
///
/// Yields one entry per data line. Comments and blank lines are skipped.
/// </summary>
public sealed class UcdAuxiliaryRangeFile
{
    public static IEnumerable<UcdAuxiliaryRangeEntry> Parse(string path)
    {
        using var reader = new StreamReader(path);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.AsSpan().TrimStart();
            if (trimmed.IsEmpty || trimmed[0] == '#' || trimmed[0] == '@')
            {
                continue;
            }
            var hashIdx = line.IndexOf('#', StringComparison.Ordinal);
            var data    = (hashIdx >= 0 ? line[..hashIdx] : line).Trim();
            if (data.Length == 0)
            {
                continue;
            }
            var parts = data.Split(';');
            if (parts.Length < 2)
            {
                continue;
            }

            var rangeText = parts[0].Trim();
            int start, end;
            var dotIdx = rangeText.IndexOf("..", StringComparison.Ordinal);
            if (dotIdx >= 0)
            {
                start = int.Parse(rangeText[..dotIdx], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                end   = int.Parse(rangeText[(dotIdx + 2)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            else
            {
                /* For multi-codepoint sequences (emoji-sequences.txt has lines
                 * like "1F468 200D 1F469 ; ..."), the "range" field is actually
                 * a sequence. Split on whitespace and treat as a sequence. */
                var seqParts = rangeText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (seqParts.Length > 1)
                {
                    var seq = new int[seqParts.Length];
                    for (int i = 0; i < seqParts.Length; ++i)
                    {
                        seq[i] = int.Parse(seqParts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    }
                    var values = new string[parts.Length - 1];
                    for (int i = 1; i < parts.Length; ++i)
                    {
                        values[i - 1] = parts[i].Trim();
                    }
                    yield return new UcdAuxiliaryRangeEntry(
                        StartCodepoint: seq[0],
                        EndCodepoint:   seq[0],
                        Sequence:       seq,
                        Values:         values);
                    continue;
                }
                start = int.Parse(rangeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                end   = start;
            }

            var fieldValues = new string[parts.Length - 1];
            for (int i = 1; i < parts.Length; ++i)
            {
                fieldValues[i - 1] = parts[i].Trim();
            }
            yield return new UcdAuxiliaryRangeEntry(start, end, null, fieldValues);
        }
    }
}

/// <summary>
/// One row from a semicolon-separated UCD-style auxiliary range file.
/// <see cref="Sequence"/> is non-null for multi-codepoint sequences
/// (e.g., zwj emoji sequences); otherwise <see cref="StartCodepoint"/>
/// and <see cref="EndCodepoint"/> describe the range.
/// </summary>
public sealed record UcdAuxiliaryRangeEntry(
    int StartCodepoint,
    int EndCodepoint,
    IReadOnlyList<int>? Sequence,
    IReadOnlyList<string> Values);
