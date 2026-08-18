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
    /// id -> content root, populated as a FREE side effect of phase 1 (the sentence lane
    /// already composes every root) and read by phase 2. Discarded with the run: a Tatoeba
    /// row number is ingest scaffolding, not knowledge, so it gets no entity, no geometry
    /// and no trajectory.
    /// </summary>
    private readonly TatoebaIdMap _ids = new();

    internal static readonly ConcurrentDictionary<string, byte> LanguageNames = new(StringComparer.Ordinal);
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => LanguageNames.Keys.ToArray();

    public override int LayerOrder => 2;

    private ConcurrentDictionary<long, byte>? _allowedSentenceIds;

    protected override ConcurrentDictionary<string, byte>? VocabularyReadback => LanguageNames;

    /// <summary>
    /// sentences.csv is the ENTITY file; links.csv is the ATTESTATION file. They run as two
    /// PHASES because the second needs what the first resolved — not as parallel files with a
    /// prelude that resolves everything twice (see TatoebaPhase for the measurement that
    /// killed that shape). Each phase is single-file, so MonolithSegmenter still gives it
    /// intra-file parallelism.
    /// </summary>
    protected override async IAsyncEnumerable<SubstrateChange> RunIngestAsync(
        IDecomposerContext context, DecomposerOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        _allowedSentenceIds = options.Languages?.IsActive == true
            ? new ConcurrentDictionary<long, byte>()
            : null;

        await foreach (var c in RunPhaseAsync(
                           new TatoebaSentencePhase(_ids, _allowedSentenceIds), context, options, ct))
            yield return c;

        // Phase 2 only ever runs after phase 1 has been fully composed, so the map is
        // complete by construction. If it is empty the corpus would silently lose every
        // translation — the failure class IngestRunner already refuses to call success.
        if (_ids.Count == 0)
            throw new InvalidOperationException(
                "Tatoeba link phase reached with an empty id map: sentences.csv was missing or "
                + "yielded no resolvable rows. Every IS_TRANSLATION_OF would be dropped.");

        await foreach (var c in RunPhaseAsync(
                           new TatoebaLinkPhase(_ids, _allowedSentenceIds), context, options, ct))
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
