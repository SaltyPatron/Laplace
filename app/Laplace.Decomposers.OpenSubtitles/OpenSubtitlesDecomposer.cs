using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.OpenSubtitles;

/// <summary>
/// Emits the OpenSubtitles v2024 aligned parallel corpus as content + attestations.
///
/// <para>DATA — Moses format. Each language pair is one <c>&lt;a&gt;-&lt;b&gt;.txt.zip</c>
/// holding TWO parallel, line-aligned plain-text entries
/// (<c>OpenSubtitles.&lt;a&gt;-&lt;b&gt;.&lt;a&gt;</c> and <c>…&lt;b&gt;</c>): line N of one is the
/// translation of line N of the other. 10 pairs, ~601M aligned sentence pairs, ~15 GB
/// compressed — by a wide margin the biggest source in the ladder, so the inner loop is
/// allocation-lean and the files are streamed line-by-line straight out of the zip
/// (never extracted, never buffered whole).</para>
///
/// <para>EMISSION — the SAME translation arena Tatoeba witnesses. Per aligned pair:
/// <list type="bullet">
///   <item>both sentences as content (<see cref="ContentEmitter"/>) — their tier trees
///   give words / graphemes for free, deduped + converging with every other source;</item>
///   <item><c>sentenceA —IS_TRANSLATION_OF→ sentenceB</c> (registry-routed; the kind is
///   SYMMETRIC, so <c>Orient</c> canonicalizes endpoint order onto ONE consensus row);</item>
///   <item>each sentence <c>—HAS_LANGUAGE→</c> its language entity
///   (<see cref="LanguageReference.Resolve"/> — the same omni-glottal value every source
///   resolves into).</item>
/// </list></para>
///
/// <para>PRECEDES — DELIBERATELY NOT EMITTED. The Moses package is flat, alignment-extracted
/// sentence PAIRS: line N is an alignment, not a position in an ordered subtitle transcript.
/// The format preserves no document / film / subtitle-stream boundaries and no within-stream
/// ordering (pairs are extracted and concatenated across the whole collection, with
/// intra-lingual alternate-upload alignments mixed in per the OPUS README). Consecutive lines
/// are therefore NOT guaranteed consecutive conversational turns, so a <c>PRECEDES</c> edge
/// would fabricate structure the data does not carry — skipped, per the "never invent
/// structure" rule. (Turn adjacency would need the standoff / xml alignment packages, not the
/// Moses package fetched here.)</para>
/// </summary>
public sealed class OpenSubtitlesDecomposer : IDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/OpenSubtitlesDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    private static readonly Hash128 LanguageTypeId =
        Hash128.OfCanonical("substrate/type/Language/v1");

    // Sum of the per-pair aligned-pair counts published in PROVENANCE.md (OPUS v2024
    // Moses headers). Used directly by EstimateUnitCountAsync — counting 1.2 billion
    // lines to estimate a 601M-pair stream would cost a full extra pass over 15 GB.
    private static readonly (string Pair, long Pairs)[] PairCounts =
    {
        ("ar-en",     87_893_588L),
        ("de-en",     65_673_701L),
        ("en-es",    105_482_431L),
        ("en-fr",     83_896_581L),
        ("en-it",     72_430_053L),
        ("en-ja",      2_068_294L),
        ("en-ko",     31_052_957L),
        ("en-pt",     68_557_861L),
        ("en-ru",     61_544_952L),
        ("en-zh_CN",  22_394_812L),
    };

    public Hash128 SourceId     => Source;
    public string  SourceName   => "OpenSubtitlesDecomposer";
    public int     LayerOrder   => 2;   // needs only unicode(0)+iso(1) — independent of wordnet/omw
    public Hash128 TrustClassId => TrustClass;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        // Rank/trust live in the REGISTRY at attest time — AddRelationType(name) only.
        boot.AddRelationType("IS_TRANSLATION_OF");
        boot.AddRelationType("HAS_LANGUAGE");
        await context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!Directory.Exists(context.EcosystemPath)) yield break;
        int batch = options.BatchSize > 1 ? options.BatchSize : 8192;

        // One pass over each pair zip; deterministic order so intent ids are stable.
        var zips = Directory.EnumerateFiles(context.EcosystemPath, "*.txt.zip")
                            .OrderBy(p => p, StringComparer.Ordinal)
                            .ToList();

        foreach (string zipPath in zips)
        {
            ct.ThrowIfCancellationRequested();

            // Two parallel line-aligned entries per pair: derive each side's language
            // from the entry's filename SUFFIX (the part after the last '.') — robust to
            // region codes that themselves contain '_' (e.g. zh_CN).
            using var zip = ZipFile.OpenRead(zipPath);
            var textEntries = zip.Entries
                .Where(e => e.Length > 0 && IsTextEntry(e.FullName))
                .OrderBy(e => e.FullName, StringComparer.Ordinal)
                .Take(2)
                .ToList();
            if (textEntries.Count != 2) continue;

            ZipArchiveEntry entA = textEntries[0], entB = textEntries[1];
            Hash128 langA = LanguageReference.Resolve(LangSuffix(entA.FullName));
            Hash128 langB = LanguageReference.Resolve(LangSuffix(entB.FullName));

            string unitStem = Path.GetFileNameWithoutExtension(zipPath); // e.g. "en-es.txt"
            var b = NewBuilder($"opensubtitles/{unitStem}/0", batch);
            int n = 0, bn = 0;

            using var sA = entA.Open();
            using var sB = entB.Open();
            using var rA = new StreamReader(sA, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            using var rB = new StreamReader(sB, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                string? lineA = await rA.ReadLineAsync(ct);
                string? lineB = await rB.ReadLineAsync(ct);
                // Both null = both files ended together (the aligned invariant). If one ends
                // first the files are not parallel — stop this pair rather than misalign.
                if (lineA is null || lineB is null) break;

                if (lineA.Length == 0 || lineB.Length == 0) continue;

                var idA = ContentEmitter.Emit(b, lineA, Source);
                var idB = ContentEmitter.Emit(b, lineB, Source);
                if (idA is null || idB is null) continue;

                // Language entities (idempotent with the ISO layer) so HAS_LANGUAGE FK holds.
                b.AddEntity(new EntityRow(langA, (byte)MetaTier.Meta, LanguageTypeId, Source));
                b.AddEntity(new EntityRow(langB, (byte)MetaTier.Meta, LanguageTypeId, Source));

                // SYMMETRIC: Orient canonicalizes (A,B)/(B,A) onto one consensus row.
                b.AddAttestation(RelationTypeRegistry.Attest(
                    idA.Value, "IS_TRANSLATION_OF", idB.Value, Source, SourceTrust.StructuredCorpus));
                b.AddAttestation(RelationTypeRegistry.Attest(
                    idA.Value, "HAS_LANGUAGE", langA, Source, SourceTrust.StructuredCorpus));
                b.AddAttestation(RelationTypeRegistry.Attest(
                    idB.Value, "HAS_LANGUAGE", langB, Source, SourceTrust.StructuredCorpus));

                if (++n >= batch)
                {
                    if (!options.DryRun) yield return b.Build();
                    b = NewBuilder($"opensubtitles/{unitStem}/{++bn}", batch);
                    n = 0;
                    await Task.Yield();
                }
            }
            if (n > 0 && !options.DryRun) yield return b.Build();
        }
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        // Sum of the published per-pair aligned-pair counts (PROVENANCE.md). Exact, free —
        // line-counting 1.2B lines to estimate would be a wasted pass over the whole corpus.
        long total = 0;
        foreach (var (_, pairs) in PairCounts) total += pairs;
        return Task.FromResult<long?>(total);   // 600,995,230
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // The Moses text entries are "OpenSubtitles.<a>-<b>.<a>" / "…<b>"; README / LICENSE are
    // the only other entries. A text entry is one whose name starts with "OpenSubtitles.".
    private static bool IsTextEntry(string entryName)
    {
        string leaf = entryName;
        int slash = leaf.LastIndexOf('/');
        if (slash >= 0) leaf = leaf[(slash + 1)..];
        return leaf.StartsWith("OpenSubtitles.", StringComparison.Ordinal);
    }

    // Language suffix = the part of the entry filename after the LAST '.'
    // (e.g. "OpenSubtitles.en-zh_CN.zh_CN" -> "zh_CN", "…en-es.es" -> "es").
    private static string LangSuffix(string entryName)
    {
        string leaf = entryName;
        int slash = leaf.LastIndexOf('/');
        if (slash >= 0) leaf = leaf[(slash + 1)..];
        int dot = leaf.LastIndexOf('.');
        return dot >= 0 && dot + 1 < leaf.Length ? leaf[(dot + 1)..] : "und";
    }

    private static SubstrateChangeBuilder NewBuilder(string unit, int batch) =>
        new(Source, unit, null,
            entityCapacity:      batch * 8,
            physicalityCapacity: batch * 8,
            attestationCapacity: batch * 4);
}
