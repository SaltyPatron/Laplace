using System.Text.RegularExpressions;

namespace Laplace.Decomposers.Abstractions;

public sealed record NativeIngestFormat(string Name, string Grammar, string Like, IReadOnlyList<string> Match);
public sealed record NativeIngestSource(string Name, IReadOnlyList<string> Aliases, string Witness,
    string Lineage, string Held, IReadOnlyList<string> Roots, IReadOnlyList<string> Files,
    IReadOnlyList<string> After, IReadOnlyList<string> Reads, IReadOnlyList<NativeIngestFormat> Recipes);

/// <summary>A projection of the Engine's source and recipe declarations, with no database or grammar loading.</summary>
public sealed record NativeIngestCatalog(IReadOnlyList<NativeIngestSource> Sources, IReadOnlyList<NativeIngestFormat> Formats)
{
    public string Resolve(string name)
    {
        string key = name.ToLowerInvariant();
        return Sources.FirstOrDefault(s => s.Name == key || s.Aliases.Contains(key, StringComparer.Ordinal))?.Name ?? key;
    }

    public static NativeIngestCatalog Read(string root)
    {
        var order = ReadSourceOrder(root).ToList();
        var sources = new Dictionary<string, NativeIngestSource>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            string path = Path.Combine(directory, "source");
            if (!File.Exists(path)) continue;
            var fields = Fields(path);
            string name = Value(fields, "name");
            string[] aliases = Words(fields, "aliases");
            if (name != Path.GetFileName(directory)) throw new InvalidDataException($"Source name differs from its directory: {path}");
            foreach (string key in new[] { name }.Concat(aliases))
                if (!Regex.IsMatch(key, "^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant) || !names.Add(key))
                    throw new InvalidDataException($"Invalid or duplicate source name or alias: {key}");
            sources.Add(name, new(name, aliases, Value(fields, "witness"), Value(fields, "lineage"), Value(fields, "held"),
                Values(fields, "root"), Values(fields, "files"), Words(fields, "after"), Words(fields, "reads"),
                Directory.EnumerateFiles(directory, "*.recipe").Order(StringComparer.Ordinal).Select(path => Format(path, root)).ToArray()));
        }
        var admitted = order.ToHashSet(StringComparer.Ordinal);
        while (order.Count < sources.Count)
        {
            string? next = sources.Keys.FirstOrDefault(name => !admitted.Contains(name) && sources[name].After.All(admitted.Contains));
            if (next is null) throw new InvalidDataException("Sources have missing or cyclic dependencies.");
            order.Add(next); admitted.Add(next);
        }
        var formats = Directory.EnumerateFiles(root, "*.recipe").Order(StringComparer.Ordinal).Select(path => Format(path, root)).ToArray();
        var recipes = sources.Values.SelectMany(s => s.Recipes).Concat(formats).ToDictionary(r => r.Name, StringComparer.Ordinal);
        NativeIngestFormat Inherit(NativeIngestFormat format, HashSet<string> active)
        {
            if (format.Like.Length == 0) return format;
            if (!active.Add(format.Like) || !recipes.TryGetValue(format.Like, out var parent))
                throw new InvalidDataException($"Missing or cyclic recipe inheritance: {format.Name} -> {format.Like}");
            return format with { Grammar = Inherit(parent, active).Grammar, Like = "" };
        }
        NativeIngestFormat ResolveFormat(NativeIngestFormat format) => Inherit(format, new(StringComparer.Ordinal) { format.Name });
        return new(order.Select(name => sources[name] with { Recipes = sources[name].Recipes.Select(ResolveFormat).ToArray() }).ToArray(),
            formats.Select(ResolveFormat).ToArray());
    }

    public static IReadOnlyList<string> ReadSourceOrder(string root)
    {
        var order = new List<string>();
        var earlier = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(Path.Combine(root, "order")))
        {
            string name = line.Split('#', 2)[0].Trim();
            if (name.Length == 0) continue;
            if (!Regex.IsMatch(name, "^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant) || earlier.Contains(name))
                throw new InvalidDataException($"Invalid or duplicate source in recipes/order: {name}");
            string path = Path.Combine(root, name, "source");
            if (!File.Exists(path)) throw new InvalidDataException($"recipes/order source has no declaration: {name}");
            if (Words(Fields(path), "after").Any(dependency => !earlier.Contains(dependency)))
                throw new InvalidDataException($"Source '{name}' has dependencies missing or later in recipes/order.");
            order.Add(name); earlier.Add(name);
        }
        if (order.Count == 0) throw new InvalidDataException("recipes/order is empty.");
        return order.AsReadOnly();
    }

    private static NativeIngestFormat Format(string path, string root)
    {
        var fields = Fields(path);
        string grammar = Value(fields, "grammar"), format = Value(fields, "format");
        if (format.Length > 0)
        {
            if (!Regex.IsMatch(format, "^[a-zA-Z0-9_-]+$", RegexOptions.CultureInvariant)) throw new InvalidDataException($"Invalid format name: {format}");
            string declared = Value(Fields(Path.Combine(root, "formats", format + ".format")), "grammar");
            if (declared.Length > 0) grammar = declared;
        }
        return new(Value(fields, "name"), grammar, Value(fields, "like"), Words(fields, "match"));
    }
    private static Dictionary<string, List<string>> Fields(string path)
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string raw in File.ReadLines(path))
        {
            string line = Regex.Split(raw, @"(?:^|\s)#", RegexOptions.CultureInvariant)[0].Trim();
            if (line.Length == 0) continue;
            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (!fields.TryGetValue(parts[0], out var values)) fields[parts[0]] = values = [];
            values.Add(parts.Length > 1 ? parts[1] : "");
        }
        return fields;
    }
    private static string Value(Dictionary<string, List<string>> fields, string key) => fields.TryGetValue(key, out var values) ? values[0] : "";
    private static string[] Values(Dictionary<string, List<string>> fields, string key) => fields.TryGetValue(key, out var values) ? values.ToArray() : [];
    private static string[] Words(Dictionary<string, List<string>> fields, string key) => Values(fields, key).SelectMany(v => v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToArray();
}
