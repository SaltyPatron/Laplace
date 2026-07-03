namespace Laplace.Decomposers.Abstractions;

public static class DecomposerFileDiscovery
{
    public static IEnumerable<string> Enumerate(
        string root,
        string pattern,
        SearchOption search = SearchOption.TopDirectoryOnly,
        params string[] fallbackSubdirs)
    {
        var candidates = fallbackSubdirs.Length > 0
            ? fallbackSubdirs.Select(s => Path.Combine(root, s)).Append(root)
            : new[] { root }.AsEnumerable();

        foreach (var dir in candidates)
        {
            if (!Directory.Exists(dir)) continue;
            var files = Directory.EnumerateFiles(dir, pattern, search)
                                 .OrderBy(p => p, StringComparer.Ordinal);
            if (search == SearchOption.AllDirectories || files.Any())
                return files;
        }
        return Enumerable.Empty<string>();
    }

    public static string ResolveSubdir(string root, string pattern, params string[] subdirs)
    {
        foreach (var sub in subdirs)
        {
            var dir = Path.Combine(root, sub);
            if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, pattern).Any())
                return dir;
        }
        return root;
    }
}
