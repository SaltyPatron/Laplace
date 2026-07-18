using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Extractors;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.ConceptNet;

public sealed class ConceptNetDecomposer : RelationTripleDecomposerBase<ConceptNetSource, FullScope>, IIngestInventoryProvider
{
    public static readonly Hash128 Source = ConceptNetSource.SourceId;
    public static readonly Hash128 TrustClass = ConceptNetSource.TrustClass;

    private static readonly Hash128 LanguageTypeId = EntityTypeRegistry.Language;

    internal static Dictionary<string, string> RelMap => ConceptNetSource.RelMap;

    public override int LayerOrder => 2;
    protected override double SourceTrust => TC.UserCuratedResource;
    internal static readonly ConcurrentDictionary<string, byte> LanguageNames = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> CanonicalNamesForReadback => LanguageNames.Keys.ToArray();

    protected override ConcurrentDictionary<string, byte>? VocabularyReadback => LanguageNames;

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        string file = Path.Combine(context.EcosystemPath, "assertions.csv");
        return Task.FromResult(IngestInventory.SingleFile(
            "assertions", file, options.MaxInputUnits, ct));
    }

    public override async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {


        var inv = await DescribeInputAsync(context, DecomposerOptions.ForWitness(SourceName), ct);
        return inv?.TotalInputUnits;
    }

    // Extraction only. assertions.csv is already
    // `assertion-uri <TAB> /r/Relation <TAB> /c/lang/start <TAB> /c/lang/end <TAB> {json}`
    // — no container to unpack, so no tree-sitter. Stream UTF-8 lines, tab-split managed,
    // parse the concept URIs, apply the language filter, yield a record carrying the
    // assertion weight. Content-address, dedup, bulk COPY, fold are the shared pipeline.
    protected override async IAsyncEnumerable<RelationTripleRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        SourceEntityIdConventions.WarnIfCiliMapMissing(null, SourceName);
        string file = Path.Combine(ecosystemPath, "assertions.csv");
        if (!File.Exists(file)) yield break;

        var langs = options.Languages;
        long cap = options.MaxInputUnits;
        long consumed = 0;

        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(file, ct))
        {
            if (lineMem.Length == 0) continue;
            if (TryExtract(lineMem.Span, langs, out var record))
            {
                yield return record;
                if (cap > 0 && ++consumed >= cap) yield break;
            }
        }
    }

    // Mirrors the former ConceptNetGrammarWitness.WalkRow field logic; all span work stays
    // in this synchronous helper so no ref-struct span is alive across the iterator's yield.
    private static bool TryExtract(
        ReadOnlySpan<byte> line, LanguageFilter? langs, out RelationTripleRecord record)
    {
        record = default;
        if (langs is { IsActive: true } lf && !ConceptNetRowFilter.MatchesLanguageFilter(line, lf))
            return false;
        if (!TrySplitAssertion(line, out var rel, out var startUri, out var endUri, out var meta))
            return false;
        if (ConceptNetUri.IsExternalUrlRelation(rel)) return false;
        if (!ConceptNetRelations.TryResolveType(rel, out var typeName)) return false;
        // Capture the POS ConceptNet encodes in the concept URI (/c/en/dog/n). Previously
        // discarded (out _); now folded onto the unified POS hub via HAS_POS. The /wn/ synset
        // suffix routes to the WordNet/CILI hub via CORRESPONDS_TO. See docs/specs/16 §4.
        if (!ConceptNetUri.TryParseConceptUri(startUri, out var startLang, out var startTerm, out var startPos, out var startWn)) return false;
        if (!ConceptNetUri.TryParseConceptUri(endUri, out var endLang, out var endTerm, out var endPos, out var endWn)) return false;
        if (langs?.MatchesAllUtf8(startLang, endLang) == false) return false;
        if (startTerm.IsEmpty || endTerm.IsEmpty) return false;

        record = new RelationTripleRecord(
            UnderscoredUtf8Canonicalize.ToSpaces(startTerm), typeName, UnderscoredUtf8Canonicalize.ToSpaces(endTerm),
            ContextId: null, Magnitude: ConceptNetUri.ParseWeight(meta),
            SubjectPos: startPos, ObjectPos: endPos,
            SubjectSynsetId: ConceptNetUri.ResolveSynsetFromWnSuffix(startWn, startPos),
            ObjectSynsetId: ConceptNetUri.ResolveSynsetFromWnSuffix(endWn, endPos));
        return true;
    }

    // assertion-uri \t relation \t start-concept \t end-concept \t {metadata-json}
    private static bool TrySplitAssertion(
        ReadOnlySpan<byte> line,
        out ReadOnlySpan<byte> rel, out ReadOnlySpan<byte> startUri,
        out ReadOnlySpan<byte> endUri, out ReadOnlySpan<byte> meta)
    {
        rel = startUri = endUri = meta = default;
        int f0 = line.IndexOf((byte)'\t');
        if (f0 < 0) return false;
        var r1 = line[(f0 + 1)..];
        int f1 = r1.IndexOf((byte)'\t');
        if (f1 < 0) return false;
        rel = r1[..f1];
        var r2 = r1[(f1 + 1)..];
        int f2 = r2.IndexOf((byte)'\t');
        if (f2 < 0) return false;
        startUri = r2[..f2];
        var r3 = r2[(f2 + 1)..];
        int f3 = r3.IndexOf((byte)'\t');
        if (f3 < 0) { endUri = r3; }
        else { endUri = r3[..f3]; meta = r3[(f3 + 1)..]; }
        return !rel.IsEmpty && !startUri.IsEmpty && !endUri.IsEmpty;
    }
}
