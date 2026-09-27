using Laplace.Engine.Core;

namespace Laplace.Cli;

/// <summary>
/// Default on-disk location per ingest source key, relative to the install's ingest root.
/// This records directory layout only; which keys dispatch is IngestDispatchTable's.
/// The media keys `rgba-image` / `track-audio` / `frame-video` share the staged
/// test-data corpora with the `image` / `audio` path keys.
/// </summary>
internal static class IngestDataPaths
{
    private static readonly Dictionary<string, string> RelativeByCli = new(StringComparer.OrdinalIgnoreCase)
    {
        ["unicode"] = "UCD/Public/UCD/latest",
        ["iso639"] = "ISO639",
        ["document"] = "test-data/text",
        ["cili"] = "CILI",
        ["wordnet"] = "Wordnet",
        ["omw"] = "OMW",
        ["verbnet"] = "VerbNet",
        ["propbank"] = "PropBank",
        ["framenet"] = "FrameNet/framenet_v17",
        ["semlink"] = "SemLink",
        ["mapnet"] = "MapNet-0.1",
        ["wordframenet"] = "WordFrameNet",
        ["conceptnet"] = "ConceptNet",
        ["atomic2020"] = "Atomic2020",
        ["ud"] = "UD-Treebanks",
        ["wiktionary"] = "Wiktionary",
        ["tatoeba"] = "Tatoeba",
        ["opensubtitles"] = "OpenSubtitles",
        ["stack"] = "stack-v2",
        ["tiny-codes"] = "tiny-codes",
        ["image"] = "test-data/images",
        ["audio"] = "test-data/audio",
        ["rgba-image"] = "test-data/images",
        ["track-audio"] = "test-data/audio",
        ["frame-video"] = "test-data/video",
    };

    public static string Resolve(string cliSource, string? cliPath = null)
    {
        if (!string.IsNullOrWhiteSpace(cliPath))
            return Path.GetFullPath(cliPath);

        if (cliSource.Equals("operational", StringComparison.OrdinalIgnoreCase))
            return Laplace.Decomposers.Operational.OperationalDecomposer.BundledPath;

        if (!RelativeByCli.TryGetValue(cliSource, out var relative))
            throw new InvalidOperationException($"no manifest path for ingest source '{cliSource}'");

        string ingestRoot = LaplaceInstall.ResolveIngestRoot();
        string primary = Path.GetFullPath(Path.Combine(ingestRoot, relative));
        // Coding corpora may live in the sibling models directory; an explicit CLI path or
        // an existing ingest-root corpus takes priority.
        if ((cliSource.Equals("stack", StringComparison.OrdinalIgnoreCase)
             || cliSource.Equals("tiny-codes", StringComparison.OrdinalIgnoreCase))
            && !Directory.Exists(primary) && !File.Exists(primary))
        {
            string corpus = Path.GetFullPath(Path.Combine(ingestRoot, "..", "models", relative));
            if (Directory.Exists(corpus)) return corpus;
        }
        return primary;
    }
}
