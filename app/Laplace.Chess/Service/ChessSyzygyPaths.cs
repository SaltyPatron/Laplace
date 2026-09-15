namespace Laplace.Chess.Service;

/// <summary>One package inventory across every explicitly selected Syzygy directory.</summary>
internal static class ChessSyzygyPaths
{
    internal static IReadOnlyList<string> Roots(string pathList)
    {
        var roots = pathList.Split(Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
        if (roots.Length == 0) throw new ChessInputException("chess-syzygy: no tablebase package directories were selected.");
        return roots;
    }

    internal static IReadOnlyList<string> Packages(string pathList, bool requirePairs = false)
    {
        var roots = Roots(pathList);
        foreach (var root in roots)
            if (!Directory.Exists(root))
                throw new ChessInputException($"chess-syzygy: selected package directory '{root}' does not exist.");
        var paths = roots.SelectMany(static root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(static path => ChessInput.HasExtension(path, ChessSyzygyDecomposer.PackageExtensions))
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .OrderBy(static path => path, StringComparer.Ordinal).ToArray();
        if (paths.Length == 0)
            throw new ChessInputException($"chess-syzygy: selected directories '{pathList}' contain no .rtbw/.rtbz tables.");
        if (requirePairs)
        {
            foreach (var path in paths)
                if (new FileInfo(path).Length == 0)
                    throw new ChessInputException($"chess-syzygy: selected table file '{path}' is empty.");
            var wdl = paths.Where(static path => Path.GetExtension(path).Equals(".rtbw", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);
            var dtz = paths.Where(static path => Path.GetExtension(path).Equals(".rtbz", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);
            if (wdl.Count == 0 || !wdl.SetEquals(dtz))
                throw new ChessInputException("chess-syzygy: selected directories require matching nonempty WDL/DTZ material files; "
                    + $"missing DTZ: {string.Join(", ", wdl.Except(dtz).Order(StringComparer.Ordinal))}; "
                    + $"missing WDL: {string.Join(", ", dtz.Except(wdl).Order(StringComparer.Ordinal))}.");
        }
        return paths;
    }

    internal static string Resolve(string pathList)
    {
        _ = Packages(pathList, requirePairs: true);
        return string.Join(Path.PathSeparator, Roots(pathList));
    }

    internal static string ProbePath(string pathList)
        => string.Join(Path.PathSeparator, Packages(pathList, requirePairs: true)
            .Select(static path => Path.GetDirectoryName(path)!)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .OrderBy(static path => path, StringComparer.Ordinal));

    internal static int RequireNativeSelection(string selected, int largest, bool explicitlySelected)
    {
        if (explicitlySelected && largest <= 0)
            throw new ChessInputException($"chess-syzygy: Fathom could not initialize any readable tables from the explicitly selected directories '{selected}' (init={largest}).");
        return largest;
    }
}
