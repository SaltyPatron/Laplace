using System.Text.RegularExpressions;
using Laplace.Engine.Core;

namespace Laplace.Cli;

/// <summary>Resolves corpus locations from the native recipe estate, never a second path roster.</summary>
internal static class IngestDataPaths
{
    public static string Resolve(string cliSource, string? cliPath = null)
    {
        if (!string.IsNullOrWhiteSpace(cliPath)) return Path.GetFullPath(cliPath);

        var utility = cliSource.ToLowerInvariant() switch
        {
            "document" => "test-data/text",
            "image" or "rgba-image" => "test-data/images",
            "audio" or "track-audio" => "test-data/audio",
            "video" or "frame-video" => "test-data/video",
            _ => null
        };
        if (utility is not null) return LaplaceInstall.ResolvePathUnderIngest(utility);
        if (cliSource is "stack" or "tiny-codes")
            return LaplaceInstall.ResolvePathUnderModels(cliSource == "stack" ? "stack-v2" : "tiny-codes");

        var roots = ResolveRoots(cliSource).SelectMany(ExpandDirectories).Distinct().ToArray();
        return roots.Length switch
        {
            1 => roots[0],
            0 => throw new DirectoryNotFoundException($"No declared root exists for '{cliSource}'."),
            _ => throw new InvalidOperationException($"'{cliSource}' declares multiple roots; select a path explicitly or use its native recipe ingestion route.")
        };
    }

    public static IReadOnlyList<string> ResolveRoots(string cliSource)
    {
        var catalog = NativeRecipeCatalog.Catalog;
        string name = catalog.Resolve(cliSource);
        var source = catalog.Sources.FirstOrDefault(source => source.Name == name)
            ?? throw new InvalidOperationException($"Unknown ingest source '{cliSource}'. Read ingest catalog.");
        var roots = source.Roots.Select(ExpandRoot).OfType<string>().ToArray();
        if (roots.Length == 0)
            roots = source.Files.Select(ExpandRoot).OfType<string>().Select(FilePatternRoot).ToArray();
        return roots
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
    }

    private static string FilePatternRoot(string pattern)
    {
        var at = pattern.IndexOfAny(['*', '?']);
        if (at < 0) return Path.GetDirectoryName(Path.GetFullPath(pattern))!;
        var prefix = pattern[..at];
        return Path.EndsInDirectorySeparator(prefix)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(prefix))
            : Path.GetDirectoryName(Path.GetFullPath(prefix))!;
    }

    private static string? ExpandRoot(string root)
    {
        bool resolved = true;
        string expanded = Regex.Replace(root, @"\$(?:\{(?<name>[A-Z0-9_]+)\}|(?<name>[A-Z0-9_]+))", match =>
        {
            var name = match.Groups["name"].Value;
            string? value = name switch
            {
                "DATA" or "LAPLACE_DATA" => LaplaceInstall.DeclaredIngestRoot(),
                "MODELS" or "LAPLACE_MODELS" => LaplaceInstall.DeclaredModelsRoot(),
                "SRC" or "LAPLACE_SRC" => LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_SRC")
                    ?? (LaplaceInstall.TryRepoRoot(out var repoRoot) && Directory.GetParent(repoRoot) is { } parent
                        ? parent.FullName
                        : throw new InvalidOperationException("Declare LAPLACE_SRC for workspace-code.")),
                "HOME" => NativeRecipeCatalog.HomeDirectory(),
                _ => Environment.GetEnvironmentVariable(name),
            };
            if (string.IsNullOrEmpty(value)) { resolved = false; return ""; }
            return value;
        });
        return resolved ? expanded : null;
    }

    private static IEnumerable<string> ExpandDirectories(string root)
    {
        var absolute = Path.GetFullPath(root);
        if (absolute.IndexOfAny(['*', '?']) < 0) return [absolute];
        var drive = Path.GetPathRoot(absolute)!;
        IEnumerable<string> directories = [drive];
        foreach (var part in absolute[drive.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length == 0) continue;
            directories = part.IndexOfAny(['*', '?']) >= 0
                ? directories.Where(Directory.Exists).SelectMany(directory => Directory.EnumerateDirectories(directory, part))
                : directories.Select(directory => Path.Combine(directory, part));
            directories = directories.ToArray();
        }
        return directories.Where(Directory.Exists);
    }
}
