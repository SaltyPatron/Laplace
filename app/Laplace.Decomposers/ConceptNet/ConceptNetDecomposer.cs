using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
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

    internal static Dictionary<string, string> RelMap => ConceptNetSource.RelMap;

    public override int LayerOrder => 2;
    protected override double SourceTrust => TC.UserCuratedResource;
    internal static readonly ConcurrentDictionary<string, byte> LanguageNames = new(StringComparer.Ordinal);
    private readonly ConcurrentIdSet _sourceNodeDeclarations = new();
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => LanguageNames.Keys.ToArray();

    protected override ConcurrentDictionary<string, byte>? VocabularyReadback => LanguageNames;
    protected override ConcurrentIdSet? SourceNodeDeclarations => _sourceNodeDeclarations;

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

    protected override IReadOnlyList<string> ListInputFiles(
        string ecosystemPath, DecomposerOptions options)
    {
        string file = Path.Combine(ecosystemPath, "assertions.csv");
        return File.Exists(file) ? [file] : [];
    }

    protected override Task OnBeforeRegisterAsync(IDecomposerContext context, CancellationToken ct)
    {
        // Synset anchors resolve through the CILI map, so ingest fails without it.
        SourceEntityIdConventions.EnsureCiliMapForIngest(context.Logger, SourceName);
        return Task.CompletedTask;
    }

    // Extraction only. assertions.csv rows are
    // `assertion-uri <TAB> /r/Relation <TAB> /c/lang/start <TAB> /c/lang/end <TAB> {json}`:
    // stream UTF-8 lines, split on tabs, parse the concept URIs, apply the language filter,
    // and yield a record carrying weight and source count. Compose, converge, bulk persist
    // and fold are the shared recipe.
    protected override async IAsyncEnumerable<RelationTripleRecord> ExtractFileAsync(
        string filePath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!File.Exists(filePath)) yield break;

        var langs = options.Languages;

        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(filePath, ct))
        {
            if (lineMem.Length == 0) continue;
            if (TryExtract(lineMem.Span, langs, out var record))
                yield return record;
        }
    }

    // All span work stays in this synchronous helper so no ref-struct span is alive across
    // the iterator's yield.
    private static bool TryExtract(
        ReadOnlySpan<byte> line, LanguageFilter? langs, out RelationTripleRecord record)
    {
        record = default;
        if (langs is { IsActive: true } lf && !ConceptNetRowFilter.MatchesLanguageFilter(line, lf))
            return false;
        if (!TrySplitAssertion(line, out var rel, out var startUri, out var endUri, out var meta))
            return false;
        if (ConceptNetUri.IsExternalUrlRelation(rel)) return false;
        if (!ConceptNetRelations.TryResolveType(rel, out var typeName, out bool flipEdge, out bool negated))
            return false;
        // A Not* row denies the relation it maps to, and the sign of the magnitude is the
        // outcome: laplace_score_fp(v, m) = 0.5*(1 + v/(m+|v|)) scores below 0.5 for v < 0,
        // so it folds as a refutation into the cell the positive form confirms, with
        // ConceptNet's weight as its strength.
        double weight = ConceptNetUri.ParseWeight(meta);
        if (negated) weight = -weight;
        long sourceCount = ConceptNetUri.ParseSourceCount(meta);
        // The concept URI carries a POS (/c/en/dog/n), attested via HAS_POS, and may carry a
        // /wn/ synset suffix, which resolves to the WordNet/CILI synset identity.
        if (!ConceptNetUri.TryParseConceptUri(startUri, out var startLang, out var startTerm, out var startPos, out var startWn)) return false;
        if (!ConceptNetUri.TryParseConceptUri(endUri, out var endLang, out var endTerm, out var endPos, out var endWn)) return false;
        if (langs?.MatchesAllUtf8(startLang, endLang) == false) return false;
        if (startTerm.IsEmpty || endTerm.IsEmpty) return false;

        // Language scope: the attestation's context is the subject's language, since the
        // claim is about the subject surface. Without it, cross-lingual rows such as Synonym
        // (HAS_SENSE family) would compete with same-language senses in one cell.
        Hash128? startLangId = LangId(startLang);
        Hash128? endLangId = LangId(endLang);

        // Where a synset id resolves, it is the attestation endpoint and the surface stays a
        // lexical route to it, so /c/en/bank/n/wn/... does not collapse onto the text "bank".
        //
        // dbpedia relations state (France, capital, Paris) while AT_LOCATION reads "subject is
        // located at object"; the endpoints are swapped here, at record construction, so no
        // later step carries an order flag.
        if (flipEdge)
        {
            record = new RelationTripleRecord(
                UnderscoredUtf8Canonicalize.ToSpaces(endTerm), typeName, UnderscoredUtf8Canonicalize.ToSpaces(startTerm),
                ContextId: endLangId, Magnitude: weight,
                SubjectPos: endPos, ObjectPos: startPos,
                SubjectSynsetId: ConceptNetUri.ResolveSynsetFromWnSuffix(endWn, endPos),
                ObjectSynsetId: ConceptNetUri.ResolveSynsetFromWnSuffix(startWn, startPos),
                SubjectLangCode: Encoding.UTF8.GetString(endLang),
                ObjectLangCode: Encoding.UTF8.GetString(startLang),
                ObservationCount: sourceCount);
            return true;
        }

        record = new RelationTripleRecord(
            UnderscoredUtf8Canonicalize.ToSpaces(startTerm), typeName, UnderscoredUtf8Canonicalize.ToSpaces(endTerm),
            ContextId: startLangId, Magnitude: weight,
            SubjectPos: startPos, ObjectPos: endPos,
            SubjectSynsetId: ConceptNetUri.ResolveSynsetFromWnSuffix(startWn, startPos),
            ObjectSynsetId: ConceptNetUri.ResolveSynsetFromWnSuffix(endWn, endPos),
            SubjectLangCode: Encoding.UTF8.GetString(startLang),
            ObjectLangCode: Encoding.UTF8.GetString(endLang),
            ObservationCount: sourceCount);
        return true;
    }

    // Per-row language resolution without per-row allocation: codes of at most 8 bytes pack
    // into one ulong key; longer codes ("zh-classical") use a string-keyed memo. Resolution
    // (LanguageReference.Resolve) and readback tracking run once per distinct code.
    // Unresolved codes map to "und", so null here means only an empty span.
    private static readonly ConcurrentDictionary<ulong, Hash128> LangIdByPackedCode = new();
    private static readonly ConcurrentDictionary<string, Hash128> LangIdByRawCode =
        new(StringComparer.Ordinal);

    private static Hash128? LangId(ReadOnlySpan<byte> langUtf8)
    {
        if (langUtf8.IsEmpty) return null;
        if (langUtf8.Length <= 8)
        {
            ulong key = 0;
            for (int i = 0; i < langUtf8.Length; i++)
                key = (key << 8) | langUtf8[i];
            if (LangIdByPackedCode.TryGetValue(key, out var hit)) return hit;
            var resolved = ResolveAndTrack(Encoding.UTF8.GetString(langUtf8));
            LangIdByPackedCode.TryAdd(key, resolved);
            return resolved;
        }
        string raw = Encoding.UTF8.GetString(langUtf8);
        return LangIdByRawCode.GetOrAdd(raw, static code => ResolveAndTrack(code));
    }

    private static Hash128 ResolveAndTrack(string code)
    {
        VocabularyNames.TrackLanguage(LanguageNames, code);
        return LanguageReference.Resolve(code);
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
