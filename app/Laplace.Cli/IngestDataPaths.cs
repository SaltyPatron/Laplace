namespace Laplace.Cli;

/// <summary>
/// Default ingest paths from <c>LAPLACE_DATA_ROOT</c> + witness-manifest relative paths.
/// Windows standard vault: <c>D:\Data\Ingest</c> (set via <c>env.cmd</c> as INGEST / LAPLACE_DATA_ROOT).
/// CLI path arguments override these defaults; platform defaults apply when the env is unset.
/// </summary>
internal static class IngestDataPaths
{
    private static string DataRoot =>
        Environment.GetEnvironmentVariable("LAPLACE_DATA_ROOT")
        ?? (OperatingSystem.IsWindows() ? @"D:\Data\Ingest" : "/vault/Data");

    /// <summary>Manifest-relative paths (scripts/win/witness-manifest.json).</summary>
    private static readonly Dictionary<string, string> RelativeByCli = new(StringComparer.OrdinalIgnoreCase)
    {
        ["unicode"]       = "UCD/Public/UCD/latest",
        ["iso639"]        = "ISO639",
        ["document"]      = "test-data/text",
        ["wordnet"]       = "Wordnet",
        ["omw"]           = "OMW",
        ["verbnet"]       = "VerbNet",
        ["propbank"]      = "PropBank",
        ["framenet"]      = "FrameNet/framenet_v17",
        ["semlink"]       = "SemLink",
        ["mapnet"]        = "MapNet-0.1",
        ["wordframenet"]  = "WordFrameNet",
        ["conceptnet"]    = "ConceptNet",
        ["atomic2020"]    = "Atomic2020",
        ["ud"]            = "UD-Treebanks",
        ["wiktionary"]    = "Wiktionary",
        ["tatoeba"]       = "Tatoeba",
        ["opensubtitles"] = "OpenSubtitles",
        ["stack"]         = "stack-v2",
        ["tiny-codes"]    = "tiny-codes",
        ["image"]         = "test-data/images",
        ["audio"]         = "test-data/audio",
    };

    public static string Resolve(string cliSource, string? cliPath = null)
    {
        if (!string.IsNullOrWhiteSpace(cliPath))
            return Path.GetFullPath(cliPath);

        if (!RelativeByCli.TryGetValue(cliSource, out var relative))
            throw new InvalidOperationException($"no manifest path for ingest source '{cliSource}'");

        var path = Path.Combine(DataRoot, relative);
        // Legacy self-hosted layout used lowercase omw/.
        if (cliSource.Equals("omw", StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(path))
        {
            var legacy = Path.Combine(DataRoot, "omw");
            if (Directory.Exists(legacy))
                path = legacy;
        }

        if (cliSource.Equals("mapnet", StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(path))
        {
            var legacy = Path.Combine(DataRoot, "MapNet");
            if (Directory.Exists(legacy))
                path = legacy;
        }

        if (cliSource.Equals("wordframenet", StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(path))
        {
            var legacy = Path.Combine(DataRoot, "eXtendedWFN");
            if (Directory.Exists(legacy))
                path = legacy;
        }

        return Path.GetFullPath(path);
    }
}
