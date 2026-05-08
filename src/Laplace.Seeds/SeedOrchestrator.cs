namespace Laplace.Seeds;

using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Decomposers.Atomic;
using Laplace.Decomposers.Iso639;
using Laplace.Decomposers.Omw;
using Laplace.Decomposers.Tatoeba;
using Laplace.Decomposers.Ud;
using Laplace.Decomposers.Wiktionary;
using Laplace.Decomposers.WordNet;

/// <summary>
/// Sequences the foundational + text-bearing seed decomposers in dependency
/// order so the convergence gates G3 (foundational substrate populated) and
/// G4 (cross-source dedup verified) can fire end-to-end.
///
/// Dependency order, derived from the master plan's Track E + Track F1/F4:
///   1. Iso639Decomposer        — language entities (no upstream substrate deps)
///   2. WordNetDecomposer       — synset entities + has_sense + lemma F1 emission
///   3. WordNetPointerLinker    — inter-synset typed edges (deps: 2)
///   4. OmwDecomposer           — per-language has_sense / has_gloss / has_example
///                                (deps: 2 — recomputes synset hashes from WordNet
///                                data files so OMW lemmas attach to the same
///                                content-addressed synset entity)
///   5. TatoebaDecomposer       — sentence entities + parallel_translation edges
///   6. UdDecomposer            — per-treebank dependency edges + UPOS attestation
///   7. AtomicDecomposer        — commonsense triples (event-to-event edges)
///   8. WiktionaryDecomposer    — surface-form detail (PoS / sense / etymology /
///                                pronunciation / forms / lexical relations /
///                                cross-language translation)
///
/// All decomposers route content through the F1 TextDecomposer the
/// composition root supplies, so cross-source dedup is automatic across
/// every step. The orchestrator does NOT manage the F1 instance; that
/// lives on each decomposer's constructor.
///
/// OMW + UD enumerate per-file inputs from the supplied root directories.
/// Missing data files are skipped silently — the orchestrator's job is
/// sequencing, not validation.
///
/// Phase 4 / Track F4 integration glue. CLI subcommand
/// <c>seed-foundational</c> (Phase 6 / Track K) is the eventual caller.
/// </summary>
public sealed class SeedOrchestrator
{
    private readonly Iso639Decomposer       _iso639;
    private readonly WordNetDecomposer      _wordnet;
    private readonly WordNetPointerLinker   _wordnetPointers;
    private readonly OmwDecomposer          _omw;
    private readonly TatoebaDecomposer      _tatoeba;
    private readonly UdDecomposer           _ud;
    private readonly AtomicDecomposer       _atomic;
    private readonly WiktionaryDecomposer   _wiktionary;

    public SeedOrchestrator(
        Iso639Decomposer       iso639,
        WordNetDecomposer      wordnet,
        WordNetPointerLinker   wordnetPointers,
        OmwDecomposer          omw,
        TatoebaDecomposer      tatoeba,
        UdDecomposer           ud,
        AtomicDecomposer       atomic,
        WiktionaryDecomposer   wiktionary)
    {
        _iso639          = iso639;
        _wordnet         = wordnet;
        _wordnetPointers = wordnetPointers;
        _omw             = omw;
        _tatoeba         = tatoeba;
        _ud              = ud;
        _atomic          = atomic;
        _wiktionary      = wiktionary;
    }

    public async Task RunAsync(SeedConfig config, CancellationToken cancellationToken)
    {
        // 1. ISO 639-3 — language entities first; downstream sources reference them.
        if (!string.IsNullOrEmpty(config.Iso639Directory) && Directory.Exists(config.Iso639Directory))
        {
            await _iso639.DecomposeAsync(config.Iso639Directory, cancellationToken).ConfigureAwait(false);
        }

        // 2. Princeton WordNet — synset entities + lemma F1 emissions.
        if (!string.IsNullOrEmpty(config.WordnetDictionaryDirectory) && Directory.Exists(config.WordnetDictionaryDirectory))
        {
            await _wordnet.DecomposeAsync(config.WordnetDictionaryDirectory, cancellationToken).ConfigureAwait(false);

            // 3. Inter-synset pointer edges.
            await _wordnetPointers.LinkAsync(config.WordnetDictionaryDirectory, cancellationToken).ConfigureAwait(false);
        }

        // 4. OMW — enumerate every wn-data-{lang}.tab under <root>/wns/<wn>/.
        if (!string.IsNullOrEmpty(config.OmwRoot) && Directory.Exists(config.OmwRoot)
            && !string.IsNullOrEmpty(config.WordnetDictionaryDirectory))
        {
            var wnsRoot = Path.Combine(config.OmwRoot, "wns");
            var omwDir  = Directory.Exists(wnsRoot) ? wnsRoot : config.OmwRoot;
            foreach (var dataFile in Directory.EnumerateFiles(omwDir, "wn-data-*.tab", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var langCode = ExtractLanguageCodeFromOmwFileName(Path.GetFileName(dataFile));
                await _omw.DecomposeAsync(
                    dataFile,
                    config.WordnetDictionaryDirectory,
                    langCode,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        // 5. Tatoeba — sentence corpus + parallel-translation links.
        if (!string.IsNullOrEmpty(config.TatoebaDirectory) && Directory.Exists(config.TatoebaDirectory))
        {
            await _tatoeba.DecomposeAsync(config.TatoebaDirectory, cancellationToken).ConfigureAwait(false);
        }

        // 6. UD — enumerate every .conllu under each treebank subdir.
        if (!string.IsNullOrEmpty(config.UdTreebanksRoot) && Directory.Exists(config.UdTreebanksRoot))
        {
            foreach (var treebankDir in Directory.EnumerateDirectories(config.UdTreebanksRoot, "UD_*"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var canonicalName = Path.GetFileName(treebankDir);
                foreach (var conllu in Directory.EnumerateFiles(treebankDir, "*.conllu"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await _ud.DecomposeTreebankAsync(conllu, canonicalName, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // 7. ATOMIC 2020 — every TSV split (train/dev/test) listed.
        foreach (var tsvFile in config.AtomicTsvFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(tsvFile) || !File.Exists(tsvFile)) { continue; }
            await _atomic.DecomposeAsync(tsvFile, cancellationToken).ConfigureAwait(false);
        }

        // 8. Wiktionary (kaikki) — last because its lexical relations and
        //    translations cross-reference content the prior sources have
        //    already emitted; running it after maximizes the immediate
        //    cross-source provenance density on shared word entities.
        if (!string.IsNullOrEmpty(config.WiktionaryJsonlFile) && File.Exists(config.WiktionaryJsonlFile))
        {
            await _wiktionary.DecomposeAsync(config.WiktionaryJsonlFile, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Recover the ISO 639-3 language code from an OMW file name of the
    /// form <c>wn-data-{lang}.tab</c>. Falls back to the empty string if
    /// the name doesn't match — the OMW decomposer treats the empty
    /// fallback as "row carries its own lang prefix".
    /// </summary>
    private static string ExtractLanguageCodeFromOmwFileName(string fileName)
    {
        const string Prefix = "wn-data-";
        const string Suffix = ".tab";
        if (!fileName.StartsWith(Prefix, System.StringComparison.Ordinal)) { return string.Empty; }
        if (!fileName.EndsWith(Suffix, System.StringComparison.Ordinal))   { return string.Empty; }
        return fileName.Substring(Prefix.Length, fileName.Length - Prefix.Length - Suffix.Length);
    }
}
