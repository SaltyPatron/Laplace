using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.ConceptNet;

internal static class ConceptNetWnTopicMap
{
    private static readonly Dictionary<string, (long Offset, char Pos)> Map = new(StringComparer.Ordinal)
    {
        ["act|n"] = (34479, 'n'),
        ["animal|n"] = (1313093, 'n'),
        ["artifact|n"] = (2665985, 'n'),
        ["attribute|n"] = (4615866, 'n'),
        ["body|n"] = (5216365, 'n'),
        ["body|v"] = (1740, 'v'),
        ["change|v"] = (109660, 'v'),
        ["cognition|n"] = (5611302, 'n'),
        ["cognition|v"] = (588221, 'v'),
        ["communication|n"] = (6252138, 'n'),
        ["communication|v"] = (740577, 'v'),
        ["competition|v"] = (1072262, 'v'),
        ["consumption|v"] = (1156834, 'v'),
        ["contact|v"] = (1205696, 'v'),
        ["creation|v"] = (1617192, 'v'),
        ["emotion|v"] = (1759326, 'v'),
        ["event|n"] = (7283364, 'n'),
        ["feeling|n"] = (7479926, 'n'),
        ["food|n"] = (7555863, 'n'),
        ["group|n"] = (7938773, 'n'),
        ["location|n"] = (8489497, 'n'),
        ["motion|v"] = (1831531, 'v'),
        ["motive|n"] = (9178727, 'n'),
        ["object|n"] = (9186064, 'n'),
        ["perception|v"] = (2105810, 'v'),
        ["person|n"] = (9483738, 'n'),
        ["phenomenon|n"] = (11408559, 'n'),
        ["plant|n"] = (11529603, 'n'),
        ["possession|n"] = (13240514, 'n'),
        ["possession|v"] = (2199590, 'v'),
        ["process|n"] = (13423405, 'n'),
        ["quantity|n"] = (13575869, 'n'),
        ["relation|n"] = (13780449, 'n'),
        ["shape|n"] = (13860793, 'n'),
        ["social|v"] = (2367032, 'v'),
        ["state|n"] = (13988498, 'n'),
        ["stative|v"] = (2603699, 'v'),
        ["substance|n"] = (14580597, 'n'),
        ["time|n"] = (15122231, 'n'),
        ["weather|v"] = (2756558, 'v'),
    };

    public static Hash128? Resolve(string topic, char? pos)
    {
        if (string.IsNullOrEmpty(topic)) return null;
        if (pos is { } p)
        {
            if (Map.TryGetValue($"{topic}|{p}", out var exact))
                return ConceptAnchor.SynsetId(exact.Offset, exact.Pos);
            return null;
        }

        foreach (var (key, entry) in Map)
        {
            if (!key.StartsWith(topic + '|', StringComparison.Ordinal)) continue;
            return ConceptAnchor.SynsetId(entry.Offset, entry.Pos);
        }
        return null;
    }
}
