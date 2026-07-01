namespace Laplace.Cli;

internal static class IngestDataPaths
{
    private static string DataRoot =>
        Environment.GetEnvironmentVariable("LAPLACE_DATA_ROOT")
        ?? (OperatingSystem.IsWindows() ? @"D:\Data\Ingest" : "/vault/Data");

    private static readonly Dictionary<string, string> RelativeByCli = new(StringComparer.OrdinalIgnoreCase)
    {
        ["unicode"]       = "UCD/Public/UCD/latest",
        ["iso639"]        = "ISO639",
        ["document"]      = "test-data/text",
        ["cili"]          = "CILI",
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
        return Path.GetFullPath(path);
    }
}
