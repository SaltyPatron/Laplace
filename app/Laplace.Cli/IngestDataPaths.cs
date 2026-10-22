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
        ["iso-639"] = "ISO639",
        ["document"] = "test-data/text",
        ["project-gutenberg"] = "ProjectGutenberg/text",
        ["cili"] = "CILI",
        ["wordnet"] = "Wordnet",
        ["princeton-wordnet"] = "Wordnet",
        ["oewn"] = "OpenEnglishWordNet-2025-plus",
        ["open-english-wordnet"] = "OpenEnglishWordNet-2025-plus",
        ["omw"] = "OMW-2.0",
        ["open-multilingual-wordnet"] = "OMW-2.0",
        ["verbnet"] = "VerbNet/verbnet-cu-clear",
        ["verbnet-gl"] = "VerbNet/verbnet-cu-clear/vn-gl",
        ["propbank"] = "PropBank/propbank-frames",
        ["framenet"] = "FrameNet/framenet_v17",
        ["semlink"] = "SemLink",
        ["predicate-matrix"] = "PredicateMatrix.v1.3",
        ["mapnet"] = "MapNet-0.1",
        ["verbatlas"] = "VerbAtlas-1.1/VerbAtlas-1.1.0",
        ["wordframenet"] = "WordFrameNet",
        ["universal-dependencies-tools"] = "UD-Tools",
        ["universal-dependencies-documentation"] = "UD-Docs/docs-pages-source",
        ["ud"] = "UD-Treebanks",
        ["universal-dependencies"] = "UD-Treebanks",
        ["wsd-evaluation-framework"] = "WSD",
        ["wiktionary-kaikki"] = "Wiktionary",
        ["wiktionary"] = "Wiktionary",
        ["conceptnet"] = "ConceptNet",
        ["atomic2020"] = "Atomic/Atomic2020",
        ["atomic-2020"] = "Atomic/Atomic2020",
        ["framebase"] = "FrameBase-2.0",
        ["atomic-10x"] = "Atomic",
        ["tatoeba"] = "Tatoeba",
        ["opensubtitles"] = "OpenSubtitles",
        ["geonames"] = "GeoNames",
        ["hatecheck"] = "Safety/HateCheck",
        ["sghatecheck"] = "Safety/SGHateCheck",
        ["xstest"] = "Safety/XSTest",
        ["social-bias-frames"] = "Safety/SocialBiasFrames",
        ["social-chemistry-101"] = "Safety/SocialChemistry101/social-chem-101",
        ["prosocial-dialog"] = "Safety/ProsocialDialog",
        ["real-toxicity-prompts"] = "Safety/RealToxicityPrompts",
        ["toxigen"] = "Safety/ToxiGen",
        ["measuring-hate-speech"] = "Safety/MeasuringHateSpeech",
        ["civil-comments"] = "Safety/CivilComments",
        ["chess-com"] = "Games/Chess",
        ["twic"] = "Games/Chess/twic",
        ["lumbras-gigabase"] = "Games/Chess/Lumbras",
        ["image"] = "test-data/images",
        ["audio"] = "test-data/audio",
        ["rgba-image"] = "test-data/images",
        ["track-audio"] = "test-data/audio",
        ["frame-video"] = "test-data/video",
    };

    private static readonly Dictionary<string, string> ModelsRelativeByCli = new(StringComparer.OrdinalIgnoreCase)
    {
        ["stack"] = "stack-v2",
        ["tiny-codes"] = "tiny-codes",
        ["tokenizers"] = ".",
    };

    public static string Resolve(string cliSource, string? cliPath = null)
    {
        if (!string.IsNullOrWhiteSpace(cliPath))
            return Path.GetFullPath(cliPath);

        if (cliSource.Equals("operational", StringComparison.OrdinalIgnoreCase))
            return Laplace.Decomposers.Operational.OperationalDecomposer.BundledPath;

        if (ModelsRelativeByCli.TryGetValue(cliSource, out var modelsRelative))
            return LaplaceInstall.ResolvePathUnderModels(modelsRelative);

        if (!RelativeByCli.TryGetValue(cliSource, out var relative))
            throw new InvalidOperationException($"no manifest path for ingest source '{cliSource}'");

        string ingestRoot = LaplaceInstall.ResolveIngestRoot();
        // One declared location per source (Laplace-Engine recipes/BRIEF.md): no second root is
        // probed when it is absent. The coding corpora live in LAPLACE_MODELS (or ../models beside
        // the ingest root), declared as such above; an absent location is never replaced by another.
        string primary = Path.GetFullPath(Path.Combine(ingestRoot, relative));
        return primary;
    }
}
