namespace Laplace.Decomposers.WordNet;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

/// <summary>
/// Streaming parser for Princeton WordNet 3.0/3.1 <c>data.{noun,verb,adj,adv}</c>
/// files. Yields one <see cref="WordNetSynsetRecord"/> per non-comment line.
/// Lines starting with two spaces are header comments and are skipped.
///
/// Phase 4 / Track F4 / WordNet seed.
/// </summary>
public sealed class WordNetDataParser
{
    private readonly WordNetSynsetType _defaultType;

    public WordNetDataParser(WordNetSynsetType defaultType)
    {
        _defaultType = defaultType;
    }

    public IEnumerable<WordNetSynsetRecord> Parse(string path)
    {
        using var reader = new StreamReader(path);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0 || line.StartsWith("  ", StringComparison.Ordinal))
            {
                continue; // copyright / format header lines
            }
            var record = ParseLine(line);
            if (record is not null)
            {
                yield return record;
            }
        }
    }

    private WordNetSynsetRecord? ParseLine(string line)
    {
        // Split off the gloss after the |
        var pipeIdx = line.IndexOf('|', StringComparison.Ordinal);
        var head    = pipeIdx >= 0 ? line[..pipeIdx].TrimEnd()    : line;
        var gloss   = pipeIdx >= 0 ? line[(pipeIdx + 1)..].Trim() : string.Empty;

        var tokens = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 6) { return null; }

        int    cursor       = 0;
        long   synsetOffset = long.Parse(tokens[cursor++], CultureInfo.InvariantCulture);
        int    lexFileNum   = int.Parse(tokens[cursor++], CultureInfo.InvariantCulture);
        var    ssType       = ParseSynsetType(tokens[cursor++]);
        int    wordCount    = int.Parse(tokens[cursor++], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        var lemmas = new List<WordNetLemma>(wordCount);
        for (int i = 0; i < wordCount; ++i)
        {
            if (cursor + 1 >= tokens.Length) { return null; }
            var word  = tokens[cursor++];
            var lexId = int.Parse(tokens[cursor++], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            // WordNet replaces spaces in multi-word lemmas with underscores; restore.
            lemmas.Add(new WordNetLemma(word.Replace('_', ' '), lexId));
        }

        if (cursor >= tokens.Length) { return new WordNetSynsetRecord(synsetOffset, lexFileNum, ssType, lemmas, Array.Empty<WordNetPointer>(), gloss); }
        int ptrCount = int.Parse(tokens[cursor++], CultureInfo.InvariantCulture);
        var pointers = new List<WordNetPointer>(ptrCount);
        for (int i = 0; i < ptrCount; ++i)
        {
            if (cursor + 3 >= tokens.Length) { break; }
            var sym = tokens[cursor++];
            var off = long.Parse(tokens[cursor++], CultureInfo.InvariantCulture);
            var pos = ParseSynsetType(tokens[cursor++]);
            // source/target lemma indices: 4 hex chars combined "ssTT" — ss = source, TT = target
            var positions = tokens[cursor++];
            var srcIdx = int.Parse(positions[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var tgtIdx = int.Parse(positions[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            pointers.Add(new WordNetPointer(sym, off, pos, srcIdx, tgtIdx));
        }

        // Verb frames (f_cnt + frames) are not modeled here; the substrate's
        // grammatical edges come from UD, not WordNet's per-verb frame list.

        return new WordNetSynsetRecord(synsetOffset, lexFileNum, ssType, lemmas, pointers, gloss);
    }

    private WordNetSynsetType ParseSynsetType(string s) => s switch
    {
        "n" => WordNetSynsetType.Noun,
        "v" => WordNetSynsetType.Verb,
        "a" => WordNetSynsetType.Adjective,
        "s" => WordNetSynsetType.AdjectiveSatellite,
        "r" => WordNetSynsetType.Adverb,
        _   => _defaultType,
    };
}
