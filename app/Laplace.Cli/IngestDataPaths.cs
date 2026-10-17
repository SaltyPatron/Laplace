using Laplace.Engine.Core;

namespace Laplace.Cli;

/// <summary>
/// Default on-disk location per ingest source key, relative to the install's ingest root.
/// This records directory layout only; which keys dispatch is IngestDispatchTable's.
/// The media keys `rgba-image` / `track-audio` / `frame-video` share the staged
/// test-data corpora with the `image` / `audio` path keys. Each key has exactly one location:
/// nothing here falls back to a second directory when the declared one is absent.
/// </summary>
internal static class IngestDataPaths
{
    private static readonly Dictionary<string, string> RelativeByCli = new(StringComparer.OrdinalIgnoreCase)
    {
        ["unicode"] = "UCD/Public/UCD/latest",
        ["uca"] = "UCD/Public/UCD/latest/uca",
        ["iso639"] = "ISO639",
        ["document"] = "test-data/text",
        ["project-gutenberg"] = "ProjectGutenberg/text",
        ["cili"] = "CILI",
        ["wordnet"] = "Wordnet",
        ["oewn"] = "OpenEnglishWordNet-2025-plus",
        ["omw"] = "OMW-2.0",
        ["verbnet"] = "VerbNet/verbnet-cu-clear",
        ["verbnet-gl"] = "VerbNet/verbnet-cu-clear/vn-gl",
        ["propbank"] = "PropBank/propbank-frames",
        ["framenet"] = "FrameNet/framenet_v17",
        ["semlink"] = "SemLink",
        ["mapnet"] = "MapNet-0.1",
        ["wordframenet"] = "WordFrameNet",
        ["conceptnet"] = "ConceptNet",
        ["atomic2020"] = "Atomic/Atomic2020",
        ["ud"] = "UD-Treebanks",
        ["wiktionary"] = "Wiktionary",
        ["tatoeba"] = "Tatoeba",
        ["opensubtitles"] = "OpenSubtitles",
        ["stack"] = "../models/stack-v2",
        ["tiny-codes"] = "../models/tiny-codes",
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
        // One declared location per source (Laplace-Engine recipes/BRIEF.md): no second root is
        // probed when it is absent. The coding corpora live in the models directory beside the
        // ingest root (/vault/models), declared as such above; an absent location is never replaced by another.
        string primary = Path.GetFullPath(Path.Combine(ingestRoot, relative));
        return primary;
    }
}
