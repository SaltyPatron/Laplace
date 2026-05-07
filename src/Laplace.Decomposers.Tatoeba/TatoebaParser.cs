namespace Laplace.Decomposers.Tatoeba;

using System.Collections.Generic;
using System.Globalization;
using System.IO;

/// <summary>
/// Streaming parsers for Tatoeba's tab-separated dumps. sentences.csv
/// (id\tlang\ttext) and links.csv (source_id\ttarget_id).
/// </summary>
public sealed class TatoebaParser
{
    public static IEnumerable<TatoebaSentenceRecord> ParseSentences(string path)
    {
        using var reader = new StreamReader(path);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split('\t');
            if (parts.Length < 3) { continue; }
            if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) { continue; }
            yield return new TatoebaSentenceRecord(id, parts[1], parts[2]);
        }
    }

    public static IEnumerable<TatoebaLinkRecord> ParseLinks(string path)
    {
        using var reader = new StreamReader(path);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split('\t');
            if (parts.Length < 2) { continue; }
            if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var source)) { continue; }
            if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var target)) { continue; }
            yield return new TatoebaLinkRecord(source, target);
        }
    }
}
