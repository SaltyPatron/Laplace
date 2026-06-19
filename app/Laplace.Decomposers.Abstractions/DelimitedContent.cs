namespace Laplace.Decomposers.Abstractions;

// Splits delimited inline content (e.g. a WordNet gloss's ';'-separated senses) into trimmed,
// non-empty units. The delimiter set is a per-source parameter — each consumer owns its rule
// (WordNet uses ';'; a parenthetical comma-list would configure its own later). Keeps delimited
// content out of mega-blobs so the co-occurrence + content geometry — the field the attention/embed
// dot products are synthesized from — stays clean rather than smearing 150 words into one entity.
public static class DelimitedContent
{
    public static List<string> Split(string content, params char[] delimiters)
    {
        var units = new List<string>();
        if (string.IsNullOrWhiteSpace(content)) return units;
        if (delimiters is null || delimiters.Length == 0)
        {
            units.Add(content.Trim());
            return units;
        }
        foreach (var part in content.Split(delimiters, StringSplitOptions.RemoveEmptyEntries))
        {
            var unit = part.Trim();
            if (unit.Length > 0) units.Add(unit);
        }
        return units;
    }
}
