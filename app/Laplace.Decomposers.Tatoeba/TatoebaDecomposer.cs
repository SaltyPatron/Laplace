using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Tatoeba;

public sealed class TatoebaDecomposer : IDecomposer, IIngestInventoryProvider, IIngestCommitPolicy
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/TatoebaDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    public IngestCommitParallelism CommitParallelism => IngestCommitParallelism.Unordered;

    internal static readonly Hash128 SentenceRefTypeId = EntityTypeRegistry.TatoebaSentence;
    internal static readonly Hash128 LanguageTypeId   = EntityTypeRegistry.Language;

    internal static readonly ConcurrentDictionary<string, byte> LanguageNames = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> CanonicalNamesForReadback => LanguageNames.Keys.ToArray();

    public Hash128 SourceId     => Source;
    public string  SourceName   => "TatoebaDecomposer";
    public int     LayerOrder   => 2;
    public Hash128 TrustClassId => TrustClass;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("Tatoeba_Sentence");
        boot.AddRelationType("HAS_EXTERNAL_ID");
        boot.AddRelationType("IS_TRANSLATION_OF");
        await context.Writer.ApplyAsync(boot.Build(), ct);
        foreach (var n in boot.CanonicalNames)
            LanguageNames.TryAdd(n, 0);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string sentences = Path.Combine(context.EcosystemPath, "sentences.csv");
        string links     = Path.Combine(context.EcosystemPath, "links.csv");
        int batch = options.BatchSize > 1 ? options.BatchSize : 65536;

        var allowedSentenceIds = options.Languages?.IsActive == true ? new HashSet<long>() : null;

        if (File.Exists(sentences))
        {
            var witness = new TatoebaGrammarWitness(TatoebaRowKind.Sentence, allowedSentenceIds);
            Func<ReadOnlySpan<byte>, bool>? acceptSent = options.Languages is { IsActive: true } langs
                ? line => TatoebaRowFilter.MatchesSentenceLanguageFilter(line, langs)
                : null;

            await foreach (var change in StructuredGrammarIngest.IngestFileAsync(
                sentences, "tsv", Source, witness, batch, 1.0, "tatoeba/sent",
                reportUnits: null, contextId: null, commitEpoch: 0,
                acceptRow: acceptSent, ct: ct))
            {
                if (!options.DryRun) yield return change;
            }
        }

        if (File.Exists(links))
        {
            var witness = new TatoebaGrammarWitness(TatoebaRowKind.Link, allowedSentenceIds);
            Func<ReadOnlySpan<byte>, bool>? acceptLink = allowedSentenceIds is not null
                ? line => TatoebaRowFilter.MatchesLinkFilter(line, allowedSentenceIds)
                : null;

            await foreach (var change in StructuredGrammarIngest.IngestFileAsync(
                links, "tsv", Source, witness, batch, 1.0, "tatoeba/link",
                reportUnits: null, contextId: null, commitEpoch: 1,
                acceptRow: acceptLink, ct: ct))
            {
                if (!options.DryRun) yield return change;
            }
        }
    }

    public async Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        if (options.Languages?.IsActive == true)
            return null;
        return await EtlInventory.TatoebaAsync(context.EcosystemPath, options.Languages, ct);
    }

    public async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(context, DecomposerOptions.ForWitness(SourceName), ct);
        return inv?.TotalInputUnits;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
