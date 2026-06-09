using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.OpenSubtitles;

public sealed class OpenSubtitlesDecomposer : IDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/OpenSubtitlesDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    private static readonly Hash128 LanguageTypeId =
        Hash128.OfCanonical("substrate/type/Language/v1");

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
    public int     LayerOrder   => 2;
    public Hash128 TrustClassId => TrustClass;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
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

        var zips = Directory.EnumerateFiles(context.EcosystemPath, "*.txt.zip")
                            .OrderBy(p => p, StringComparer.Ordinal)
                            .ToList();

        foreach (string zipPath in zips)
        {
            ct.ThrowIfCancellationRequested();

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

            string unitStem = Path.GetFileNameWithoutExtension(zipPath);
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
                if (lineA is null || lineB is null) break;

                if (lineA.Length == 0 || lineB.Length == 0) continue;

                var idA = ContentEmitter.Emit(b, lineA, Source);
                var idB = ContentEmitter.Emit(b, lineB, Source);
                if (idA is null || idB is null) continue;

                b.AddEntity(new EntityRow(langA, EntityTier.Vocabulary, LanguageTypeId, Source));
                b.AddEntity(new EntityRow(langB, EntityTier.Vocabulary, LanguageTypeId, Source));

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
        long total = 0;
        foreach (var (_, pairs) in PairCounts) total += pairs;
        return Task.FromResult<long?>(total);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static bool IsTextEntry(string entryName)
    {
        string leaf = entryName;
        int slash = leaf.LastIndexOf('/');
        if (slash >= 0) leaf = leaf[(slash + 1)..];
        return leaf.StartsWith("OpenSubtitles.", StringComparison.Ordinal);
    }

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
