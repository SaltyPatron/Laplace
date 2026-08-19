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
        // GH #520: hard-fail with the rest of the ILI mesh; warn-and-drop left
        // ConceptNet synset anchors silently unmeshed.
        SourceEntityIdConventions.EnsureCiliMapForIngest(context.Logger, SourceName);
        return Task.CompletedTask;
    }

    // Extraction only. assertions.csv is already
    // `assertion-uri <TAB> /r/Relation <TAB> /c/lang/start <TAB> /c/lang/end <TAB> {json}`
    // — no container to unpack, so no tree-sitter. Stream UTF-8 lines, tab-split managed,
    // parse the concept URIs, apply the language filter, yield a record carrying the
    // assertion weight. Content-address, dedup, bulk COPY, fold are the shared pipeline.
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
        if (!ConceptNetRelations.TryResolveType(rel, out var typeName, out bool flipEdge)) return false;
        // Capture the POS ConceptNet encodes in the concept URI (/c/en/dog/n). Previously
        // discarded (out _); now folded onto the unified POS hub via HAS_POS. The /wn/ synset
        // suffix routes to the WordNet/CILI hub via CORRESPONDS_TO. See docs/specs/16 §4.
        if (!ConceptNetUri.TryParseConceptUri(startUri, out var startLang, out var startTerm, out var startPos, out var startWn)) return false;
        if (!ConceptNetUri.TryParseConceptUri(endUri, out var endLang, out var endTerm, out var endPos, out var endWn)) return false;
        if (langs?.MatchesAllUtf8(startLang, endLang) == false) return false;
        if (startTerm.IsEmpty || endTerm.IsEmpty) return false;

        // Language scope. The URI's /c/<lang>/ segment was parsed and then DISCARDED,
        // which left every edge language-free — most damagingly Synonym, which is
        // cross-lingual by design and maps into the HAS_SENSE family, so unscoped
        // translations competed as lexical.senses(the same defect OMWGrammarWitness fixed
        // for GH #867: an English copula electing "ice" from Danish witnesses).
        // The handler already emits HAS_LANGUAGE from these ids; the edge's context
        // is the SUBJECT's language — the claim is made about the subject surface,
        // exactly as OMW scopes lemma->synset by the file's language.
        Hash128? startLangId = LangId(startLang);
        Hash128? endLangId = LangId(endLang);

        // The generic handler uses the resolved synset ids as the semantic edge endpoints
        // where present, while retaining these surfaces as lexical routes into those hubs.
        // A sense-bearing /c/en/bank/n/wn/... assertion must not flatten back onto the shared
        // text root for "bank" and then rely on side metadata to recover its meaning.
        //
        // dbpedia's subject order is not the manifest's: it says (France, capital, Paris)
        // while AT_LOCATION reads "subject is located at object". Flipping HERE, at record
        // construction, keeps every downstream stage order-agnostic -- the alternative is a
        // flip flag riding through the pipeline for one lane's benefit.
        if (flipEdge)
        {
            record = new RelationTripleRecord(
                UnderscoredUtf8Canonicalize.ToSpaces(endTerm), typeName, UnderscoredUtf8Canonicalize.ToSpaces(startTerm),
                ContextId: endLangId, Magnitude: ConceptNetUri.ParseWeight(meta),
                SubjectPos: endPos, ObjectPos: startPos,
                SubjectSynsetId: ConceptNetUri.ResolveSynsetFromWnSuffix(endWn, endPos),
                ObjectSynsetId: ConceptNetUri.ResolveSynsetFromWnSuffix(startWn, startPos),
                SubjectLangId: endLangId, ObjectLangId: startLangId);
            return true;
        }

        record = new RelationTripleRecord(
            UnderscoredUtf8Canonicalize.ToSpaces(startTerm), typeName, UnderscoredUtf8Canonicalize.ToSpaces(endTerm),
            ContextId: startLangId, Magnitude: ConceptNetUri.ParseWeight(meta),
            SubjectPos: startPos, ObjectPos: endPos,
            SubjectSynsetId: ConceptNetUri.ResolveSynsetFromWnSuffix(startWn, startPos),
            ObjectSynsetId: ConceptNetUri.ResolveSynsetFromWnSuffix(endWn, endPos),
            SubjectLangId: startLangId, ObjectLangId: endLangId);
        return true;
    }

    // Per-row language resolution with NO per-row allocation on the hot path: codes
    // of <= 8 bytes (every ConceptNet code that matters — "en", "fr", "zh") pack into
    // one ulong key, so the row cost is a span pack + one lock-free dictionary hit.
    // Longer codes ("zh-classical") take the string-keyed memo. Either way the
    // canonicalization walk (Trim/ToLower/alias in LanguageReference.ResolveCode)
    // runs once per DISTINCT code (~300 in the corpus), not once per row, and the
    // factory feeds the readback roster (fix for LanguageNames being declared and
    // never populated). Unresolved codes map to "und" inside LanguageReference —
    // never default — so null here means only an empty span.
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
