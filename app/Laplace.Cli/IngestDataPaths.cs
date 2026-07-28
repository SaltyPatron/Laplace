using Laplace.Engine.Core;

namespace Laplace.Cli;

/// <summary>
/// Default on-disk location per ingest source, under the install's ingest root.
/// NOT a copy of the dispatch roster, and not a copy of EtlSource.DataKey (which
/// is a logical key, identical to the CLI name in every manifest row) — this is
/// the one place that records a source's directory layout.
///
/// `image` and `audio` have no dispatch route yet. They are DECLARED MODALITY
/// LANES, not dead entries: this substrate is omni-modal by construction (text,
/// chess, code, AI models, each with its own tier ladder under one identity law),
/// and their corpora are already staged under test-data/. Do not delete them
/// because nothing calls them — "no caller" here means "not yet implemented", and
/// removing a lane on that basis erases the intent instead of building it.
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
