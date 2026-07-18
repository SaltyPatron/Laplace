using System.Collections.Concurrent;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Tatoeba;

public sealed class TatoebaDecomposer : DecomposerMultiFile<GrammarIngestRecord, TatoebaSource, FullScope>, IIngestInventoryProvider
{
    public static readonly Hash128 Source = TatoebaSource.SourceId;
    public static readonly Hash128 TrustClass = TatoebaSource.TrustClass;

    internal static readonly Hash128 SentenceRefTypeId = EntityTypeRegistry.TatoebaSentence;
    internal static readonly Hash128 LanguageTypeId = EntityTypeRegistry.Language;

    internal static readonly ConcurrentDictionary<string, byte> LanguageNames = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> CanonicalNamesForReadback => LanguageNames.Keys.ToArray();

    public override int LayerOrder => 2;
    protected override double SourceTrust => TC.StructuredCorpus;

    private HashSet<long>? _allowedSentenceIds;

    protected override ConcurrentDictionary<string, byte>? VocabularyReadback => LanguageNames;

    // Order-independent: links anchor IS_TRANSLATION_OF on the deterministic TatoebaSentence(id)
    // external-id (computed from the id alone), which bridges to the real content root via
    // HAS_EXTERNAL_ID. No runtime id->root map, so sentences and links ingest fully in parallel
    // like every other content-addressed source — no phase, no barrier.
    protected override IMultiFileRecordStream<GrammarIngestRecord> CreateMultiFileStream(
        string ecosystemPath, DecomposerOptions options)
    {
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
            new TatoebaGrammarWitness(kind, _allowedSentenceIds),
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
