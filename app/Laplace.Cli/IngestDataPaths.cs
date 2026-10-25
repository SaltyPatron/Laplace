using System.Text.RegularExpressions;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Cli;

/// <summary>Resolves corpus locations from the native recipe estate, never a second path roster.</summary>
internal static class IngestDataPaths
{
    public static string Resolve(string cliSource, string? cliPath = null)
    {
        if (!string.IsNullOrWhiteSpace(cliPath)) return Path.GetFullPath(cliPath);
        if (cliSource.Equals("operational", StringComparison.OrdinalIgnoreCase))
            return Laplace.Decomposers.Operational.OperationalDecomposer.BundledPath;

        // Operator-supplied media/model lanes are not corpus-order entries.
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
        var catalog = EtlManifest.Catalog;
        string name = catalog.Resolve(cliSource);
        var source = catalog.Sources.FirstOrDefault(source => source.Name == name)
            ?? throw new InvalidOperationException($"Unknown ingest source '{cliSource}'. Read ingest catalog.");
        var roots = source.Roots.Select(ExpandRoot).ToArray();
        // A file-only source (tokenizers) declares its roots through absolute
        // file patterns. The stable prefix is configuration, not a corpus scan.
        if (roots.Length == 0)
            roots = source.Files.Select(line => FilePatternRoot(ExpandRoot(line))).ToArray();
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

    private static string ExpandRoot(string root) => Regex.Replace(root, @"\$(?:\{(?<name>[A-Z_]+)\}|(?<name>[A-Z_]+))", match =>
    {
        var name = match.Groups["name"].Value;
        return name switch
        {
            "DATA" or "LAPLACE_DATA" => LaplaceInstall.DeclaredIngestRoot(),
            "MODELS" or "LAPLACE_MODELS" => LaplaceInstall.DeclaredModelsRoot(),
            "SRC" or "LAPLACE_SRC" => Environment.GetEnvironmentVariable("LAPLACE_SRC")
                ?? throw new InvalidOperationException("Declare LAPLACE_SRC for workspace-code."),
            _ => throw new InvalidDataException($"Unsupported recipe-root variable: {name}")
        };
    });

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
            // Materialize each frontier before the next component captures it.
            directories = directories.ToArray();
        }
        return directories.Where(Directory.Exists);
    }
}
