namespace Laplace.Decomposers.Abstractions;






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
