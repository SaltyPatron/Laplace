using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Tatoeba;

public sealed class TatoebaDecomposer : DecomposerMultiPhase<TatoebaSource, FullScope>, IIngestInventoryProvider
{
    public static readonly Hash128 Source = TatoebaSource.SourceId;
    public static readonly Hash128 TrustClass = TatoebaSource.TrustClass;

    internal static readonly Hash128 LanguageTypeId = EntityTypeRegistry.Language;

    /// <summary>Links naming a sentence id absent from sentences.csv — reported, never grounded.</summary>
    internal static long UnresolvedLinks;

    /// <summary>
    /// Row id → content root, recorded while sentences.csv composes each root and read when
    /// links.csv resolves. Discarded with the run: the row number is packaging, so it gets no
    /// entity, geometry or trajectory.
    /// </summary>
    private readonly TatoebaIdMap _ids = new();

    internal static readonly ConcurrentDictionary<string, byte> LanguageNames = new(StringComparer.Ordinal);
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => LanguageNames.Keys.ToArray();

    public override int LayerOrder => 2;

    private ConcurrentDictionary<long, byte>? _allowedSentenceIds;

    protected override ConcurrentDictionary<string, byte>? VocabularyReadback => LanguageNames;

    /// <summary>
    /// sentences.csv composes the sentence roots; links.csv attests translations between
    /// them. The link file runs after the sentence file is composed and persisted because it
    /// resolves through the id map the sentence file fills. Each is a single-file phase, so
    /// MonolithSegmenter still parallelizes within it.
    /// </summary>
    protected override async IAsyncEnumerable<SubstrateChange> RunIngestAsync(
        IDecomposerContext context, DecomposerOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        _allowedSentenceIds = options.Languages?.IsActive == true
            ? new ConcurrentDictionary<long, byte>()
            : null;

        string sentencesPath = Path.Combine(context.EcosystemPath, "sentences.csv");
        await foreach (var c in RunPhaseAsync(
                           new TatoebaSentencePhase(_ids, _allowedSentenceIds), context, options,
                           "tatoeba/sentences", sentencesPath, ct))
            yield return c;

        // The id map is complete once composition finishes; the link attestations also
        // reference the sentence entities, so the sentences must be persisted first. The
        // apply barrier enforces that rather than relying on producer order.
        await foreach (SubstrateChange barrier in ApplyBarrierAsync(
                           "tatoeba/sentences-persisted", ct).ConfigureAwait(false))
            yield return barrier;

        // The map is complete here; an empty map would drop every translation, so it fails
        // the run.
        if (_ids.Count == 0)
            throw new InvalidOperationException(
                "Tatoeba link phase reached with an empty id map: sentences.csv was missing or "
                + "yielded no resolvable rows. Every IS_TRANSLATION_OF would be dropped.");

        string linksPath = Path.Combine(context.EcosystemPath, "links.csv");
        await foreach (var c in RunPhaseAsync(
                   new TatoebaLinkPhase(_ids, _allowedSentenceIds), context, options,
                   "tatoeba/links", linksPath, ct))
            yield return c;
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
