using System.Collections.Concurrent;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Tatoeba;

public sealed class TatoebaDecomposer : DecomposerMultiFile<GrammarIngestRecord>, IIngestInventoryProvider
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/TatoebaDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    internal static readonly Hash128 SentenceRefTypeId = EntityTypeRegistry.TatoebaSentence;
    internal static readonly Hash128 LanguageTypeId = EntityTypeRegistry.Language;

    internal static readonly ConcurrentDictionary<string, byte> LanguageNames = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> CanonicalNamesForReadback => LanguageNames.Keys.ToArray();

    public override Hash128 SourceId => Source;
    public override string SourceName => "TatoebaDecomposer";
    public override int LayerOrder => 2;
    public override Hash128 TrustClassId => TrustClass;
    protected override double SourceTrust => TC.StructuredCorpus;

    // Shared across sentence + link epochs: maps Tatoeba numeric id → content root.
    internal readonly ConcurrentDictionary<long, Hash128> IdToRoot = new();
    private HashSet<long>? _allowedSentenceIds;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        await SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            typeNodeNames: ["Tatoeba_Sentence"],
            relationNodeNames: ["HAS_EXTERNAL_ID", "IS_TRANSLATION_OF", "HAS_LANGUAGE"],
            readbackNames: LanguageNames, ct: ct);

    protected override IMultiFileRecordStream<GrammarIngestRecord> CreateMultiFileStream(
        string ecosystemPath, DecomposerOptions options)
    {
        IdToRoot.Clear();
        _allowedSentenceIds = options.Languages?.IsActive == true ? new HashSet<long>() : null;

        string sentences = Path.Combine(ecosystemPath, "sentences.csv");
        string links = Path.Combine(ecosystemPath, "links.csv");
        var files = new List<(string Path, string Label, Func<ReadOnlySpan<byte>, bool>? AcceptRow)>();

        if (File.Exists(sentences))
        {
            Func<ReadOnlySpan<byte>, bool>? acceptSent = options.Languages is { IsActive: true } langs
                ? line => TatoebaRowFilter.MatchesSentenceLanguageFilter(line, langs)
                : null;
            files.Add((sentences, "tatoeba/sent", acceptSent));
        }

        if (File.Exists(links))
        {
            Func<ReadOnlySpan<byte>, bool>? acceptLink = _allowedSentenceIds is not null
                ? line => TatoebaRowFilter.MatchesLinkFilter(line, _allowedSentenceIds)
                : null;
            files.Add((links, "tatoeba/link", acceptLink));
        }

        return new TatoebaMultiFileStream(files);
    }

    protected override IIngestRecordHandler<GrammarIngestRecord> CreateHandlerForFile(string fileLabel)
    {
        var kind = fileLabel.EndsWith("/link", StringComparison.Ordinal)
            ? TatoebaRowKind.Link
            : TatoebaRowKind.Sentence;
        return new GrammarIngestHandler(
            Source, "tsv",
            new TatoebaGrammarWitness(kind, _allowedSentenceIds, IdToRoot),
            contextId: null);
    }

    protected override IngestBatchConfig ConfigForFile(
        string fileLabel, ISubstrateReader? reader, DecomposerOptions options)
    {
        int batch = options.BatchSize > 1
            ? options.BatchSize
            : IngestSizing.ResolveForSource(IngestSourceProfile.Wiktionary).RecordBatchSize;
        int commitEpoch = fileLabel.EndsWith("/link", StringComparison.Ordinal) ? 1 : 0;
        return IngestPipelineDefaults.ApplyMaxInputUnits(
            IngestPipelineDefaults.StructuredGrammar(
                Source, fileLabel, batch, options, reader, witnessWeight: 1.0, commitEpoch: commitEpoch),
            options);
    }

    public async Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        if (options.MaxInputUnits > 0)
        {
            var paths = new List<string>();
            string sentences = Path.Combine(context.EcosystemPath, "sentences.csv");
            string links = Path.Combine(context.EcosystemPath, "links.csv");
            if (File.Exists(sentences)) paths.Add(sentences);
            if (File.Exists(links)) paths.Add(links);
            return IngestInventory.FromFiles("records", paths, options.MaxInputUnits, ct);
        }
        return await EtlInventory.TatoebaAsync(context.EcosystemPath, options.Languages, ct);
    }

    public override async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(context, DecomposerOptions.ForWitness(SourceName), ct);
        return inv?.TotalInputUnits;
    }
}
