using Laplace.Engine.Core;

namespace Laplace.Cli;

/// <summary>
/// Default on-disk location per ingest source, under the install's ingest root.
/// NOT a copy of the dispatch roster, and not a copy of EtlSource.DataKey (which
/// is a logical key, identical to the CLI name in every manifest row) — this is
/// the one place that records a source's directory layout.
///
/// Legacy `image` / `audio` path keys remain for staged corpora under test-data/.
/// Live media dispatch: `rgba-image` / `track-audio` / `frame-video` (generic lanes;
/// stub ImageDecomposer/AudioDecomposer names stay banned by integrity gates).
/// Corpora (e.g. Tatoeba) are sources, not media format keys.
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

        if (!RelativeByCli.TryGetValue(cliSource, out var relative))
            throw new InvalidOperationException($"no manifest path for ingest source '{cliSource}'");

        return LaplaceInstall.ResolvePathUnderIngest(relative);
    }
}
