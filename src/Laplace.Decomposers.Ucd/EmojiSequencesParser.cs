namespace Laplace.Decomposers.Ucd;

using System.Collections.Generic;
using System.IO;

/// <summary>
/// Parses the emoji directory files from the Unicode FTP that are NOT part
/// of <c>ucd.all.flat.xml</c>: <c>emoji-sequences.txt</c>,
/// <c>emoji-zwj-sequences.txt</c>, <c>emoji-test.txt</c>. Multi-codepoint
/// emoji sequences (skin-tone modifiers, ZWJ-joined family/profession
/// sequences, regional flag pairs) become substrate composition entities at
/// tier 1+ — each sequence is the Merkle hash of its component codepoint
/// LINESTRING.
///
/// Phase 3 / Track E / E2.
///
/// Reuses <see cref="UcdAuxiliaryRangeFile"/> since the format is the same
/// semicolon-range layout (single codepoint, range, or whitespace-separated
/// sequence on the LHS; semicolon-separated values on the RHS).
/// </summary>
public sealed class EmojiSequencesParser
{
    /// <summary>
    /// Parse a single emoji-* file. Returns entries with type tag and
    /// description — the seeder produces the substrate composition entity
    /// for each multi-codepoint sequence.
    /// </summary>
    public static IEnumerable<EmojiSequenceEntry> Parse(string path)
    {
        foreach (var entry in UcdAuxiliaryRangeFile.Parse(path))
        {
            var typeTag    = entry.Values.Count > 0 ? entry.Values[0] : string.Empty;
            var description = entry.Values.Count > 1 ? entry.Values[1] : null;
            yield return new EmojiSequenceEntry(
                StartCodepoint: entry.StartCodepoint,
                EndCodepoint:   entry.EndCodepoint,
                Sequence:       entry.Sequence,
                TypeTag:        typeTag,
                Description:    description);
        }
    }

    /// <summary>Convenience: parse the standard set of emoji files in one pass.</summary>
    public static IEnumerable<EmojiSequenceEntry> ParseAll(string emojiDirectory)
    {
        foreach (var fileName in new[]
        {
            "emoji-sequences.txt",
            "emoji-zwj-sequences.txt",
        })
        {
            var path = Path.Combine(emojiDirectory, fileName);
            if (!File.Exists(path))
            {
                continue;
            }
            foreach (var entry in Parse(path))
            {
                yield return entry;
            }
        }
    }
}

public sealed record EmojiSequenceEntry(
    int StartCodepoint,
    int EndCodepoint,
    IReadOnlyList<int>? Sequence,
    string TypeTag,
    string? Description);
