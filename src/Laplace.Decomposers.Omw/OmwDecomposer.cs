namespace Laplace.Decomposers.Omw;

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Decomposers.Text;
using Laplace.Decomposers.WordNet;
using Laplace.Pipeline.Abstractions;

/// <summary>
/// F4 — Open Multilingual WordNet (OMW) seed decomposer.
///
/// OMW provides cross-language lemma + gloss + example attachments to the
/// Princeton WordNet synset structure that <see cref="WordNetDecomposer"/>
/// establishes. Per substrate invariant 4 (knowledge IS edges, no anchor
/// language): Japanese "neko" attaches to the SAME content-addressed synset
/// entity as English "cat" — equivalence emerges from edge density across
/// many languages referencing one Princeton synset, NOT from declaring one
/// language canonical.
///
/// Cross-source dedup mechanism: synset entity_hash = Merkle composition of
/// English lemma hashes (computed by <see cref="WordNetDecomposer"/>). OMW
/// recomputes the same hash by re-parsing the WordNet data file at the same
/// (offset, ss_type) and Merkle-composing the same lemmas, then emits per-
/// language has_sense / has_gloss / has_example edges referencing it. F1
/// TextDecomposer routes every surface lemma + gloss + example through
/// codepoint-anchored decomposition so cross-source dedup with Wiktionary,
/// Tatoeba, etc. is automatic.
///
/// Decomposer flow per OMW row (synset_id, type, value):
///   1. Parse synset_id "NNNNNNNN-T" → (offset, ss_type).
///   2. Resolve synset_hash via the WordNet-derived index.
///   3. Decompose value via F1 → tier-1 content entity hash.
///   4. Emit edge per row type:
///        "lemma" → lemma_entity → has_sense → synset_entity
///        "def"   → synset_entity → has_gloss → gloss_entity
///        "exe"   → synset_entity → has_example → example_entity
///   5. Emit provenance edges referencing the omw_wn_{lang} source entity.
///
/// Phase 4 / Track F4.
/// </summary>
public sealed class OmwDecomposer
{
    private readonly TextDecomposer         _textDecomposer;
    private readonly IIdentityHashing       _hashing;
    private readonly IConceptEntityResolver _conceptResolver;
    private readonly IEdgeEmission          _edgeEmission;
    private readonly IProvenance            _provenance;

    public OmwDecomposer(
        TextDecomposer         textDecomposer,
        IIdentityHashing       hashing,
        IConceptEntityResolver conceptResolver,
        IEdgeEmission          edgeEmission,
        IProvenance            provenance)
    {
        _textDecomposer  = textDecomposer;
        _hashing         = hashing;
        _conceptResolver = conceptResolver;
        _edgeEmission    = edgeEmission;
        _provenance      = provenance;
    }

    /// <summary>
    /// Run OMW decomposition for one language data file. The OMW row's
    /// type column is <c>{lang}:{kind}[:{subkind}]</c> (e.g. <c>cmn:lemma</c>,
    /// <c>arb:lemma:root</c>); per-row language is honored over the
    /// <paramref name="defaultLanguageCode"/> fallback so a single mixed file
    /// (rare but format-valid) decomposes correctly.
    /// </summary>
    /// <param name="omwLanguageDataFile">Path to <c>wn-data-{lang}.tab</c>.</param>
    /// <param name="wordnetDictionaryDirectory">Path to Princeton WordNet's
    /// <c>dict</c> directory containing <c>data.{noun,verb,adj,adv}</c> —
    /// re-parsed to rebuild the (offset, ss_type) → synset_hash index that
    /// makes OMW lemmas attach to the same substrate synset entity hash
    /// WordNetDecomposer emitted.</param>
    /// <param name="defaultLanguageCode">Fallback ISO 639-3 code used when a
    /// row omits the <c>{lang}:</c> prefix; flows into the OMW source entity
    /// canonical name <c>omw_wn_{lang}</c> for per-language Glicko-2 source
    /// rating.</param>
    public async Task DecomposeAsync(
        string             omwLanguageDataFile,
        string             wordnetDictionaryDirectory,
        string             defaultLanguageCode,
        CancellationToken  cancellationToken)
    {
        var synsetIndex = await BuildSynsetIndexAsync(
            wordnetDictionaryDirectory, cancellationToken).ConfigureAwait(false);

        var hasSense    = _conceptResolver.Resolve("has_sense");
        var hasGloss    = _conceptResolver.Resolve("has_gloss");
        var hasExample  = _conceptResolver.Resolve("has_example");
        var subjectRole = _conceptResolver.Resolve("subject");
        var objectRole  = _conceptResolver.Resolve("object");

        // Cache resolved per-language source hashes — files routinely contain
        // tens of thousands of rows for one language.
        var sourceHashByLang = new Dictionary<string, AtomId>();

        async ValueTask<AtomId> ResolveSourceAsync(string lang)
        {
            if (sourceHashByLang.TryGetValue(lang, out var h)) { return h; }
            h = await _provenance.ResolveSourceAsync(
                $"omw_wn_{lang}", cancellationToken).ConfigureAwait(false);
            sourceHashByLang[lang] = h;
            return h;
        }

        foreach (var row in OmwTabParser.Parse(omwLanguageDataFile))
        {
            if (string.IsNullOrEmpty(row.Value)) { continue; }
            if (!TryParseSynsetId(row.SynsetId, out var offset, out var ssType)) { continue; }
            if (!synsetIndex.TryGetValue((offset, ssType), out var synsetHash)) { continue; }

            // Parse "{lang}:{kind}[:{subkind}]" from the typed column.
            // Subkinds (arb:lemma:root, arb:lemma:brokenplural) collapse to
            // their primary kind for substrate emission; the morphological
            // detail is recoverable from the OMW source if needed via a
            // follow-up morphology decomposer.
            var (rowLang, rowKind) = SplitTypeColumn(row.Type, defaultLanguageCode);
            var sourceHash = await ResolveSourceAsync(rowLang).ConfigureAwait(false);

            switch (rowKind)
            {
                case "lemma":
                {
                    // OMW shares WordNet's underscore-as-space convention for
                    // multi-word lemmas; matching restores cross-source dedup
                    // with WordNet's lemma surface forms.
                    var surface   = row.Value.Replace('_', ' ');
                    var lemmaHash = await _textDecomposer.DecomposeAsync(
                        surface, cancellationToken).ConfigureAwait(false);
                    await _provenance.EmitEntityProvenanceAsync(
                        new EntityProvenanceRecord(EntityHash: lemmaHash, SourceHash: sourceHash),
                        cancellationToken).ConfigureAwait(false);
                    await EmitBinaryEdgeAsync(
                        edgeType:    hasSense,
                        subjectRole: subjectRole,
                        subjectHash: lemmaHash,
                        objectRole:  objectRole,
                        objectHash:  synsetHash,
                        sourceHash:  sourceHash,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                }
                case "def":
                {
                    var glossHash = await _textDecomposer.DecomposeAsync(
                        row.Value, cancellationToken).ConfigureAwait(false);
                    await _provenance.EmitEntityProvenanceAsync(
                        new EntityProvenanceRecord(EntityHash: glossHash, SourceHash: sourceHash),
                        cancellationToken).ConfigureAwait(false);
                    await EmitBinaryEdgeAsync(
                        edgeType:    hasGloss,
                        subjectRole: subjectRole,
                        subjectHash: synsetHash,
                        objectRole:  objectRole,
                        objectHash:  glossHash,
                        sourceHash:  sourceHash,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                }
                case "exe":
                {
                    var exampleHash = await _textDecomposer.DecomposeAsync(
                        row.Value, cancellationToken).ConfigureAwait(false);
                    await _provenance.EmitEntityProvenanceAsync(
                        new EntityProvenanceRecord(EntityHash: exampleHash, SourceHash: sourceHash),
                        cancellationToken).ConfigureAwait(false);
                    await EmitBinaryEdgeAsync(
                        edgeType:    hasExample,
                        subjectRole: subjectRole,
                        subjectHash: synsetHash,
                        objectRole:  objectRole,
                        objectHash:  exampleHash,
                        sourceHash:  sourceHash,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Re-derive the (synset_offset, ss_type) → synset_hash map by parsing
    /// the WordNet data files and Merkle-composing English lemma hashes.
    /// Idempotent against substrate state — F1 lemma emissions deduplicate
    /// via content addressing, and the synset_hash by construction equals
    /// the one WordNetDecomposer would emit for the same input.
    /// </summary>
    private async Task<Dictionary<(long Offset, WordNetSynsetType Type), AtomId>> BuildSynsetIndexAsync(
        string             wordnetDictionaryDirectory,
        CancellationToken  cancellationToken)
    {
        var map = new Dictionary<(long, WordNetSynsetType), AtomId>();
        foreach (var (file, defaultType) in new[]
        {
            ("data.noun", WordNetSynsetType.Noun),
            ("data.verb", WordNetSynsetType.Verb),
            ("data.adj",  WordNetSynsetType.Adjective),
            ("data.adv",  WordNetSynsetType.Adverb),
        })
        {
            var path = Path.Combine(wordnetDictionaryDirectory, file);
            if (!File.Exists(path)) { continue; }
            var parser = new WordNetDataParser(defaultType);
            foreach (var synset in parser.Parse(path))
            {
                if (synset.Lemmas.Count == 0) { continue; }
                var lemmaHashes = new AtomId[synset.Lemmas.Count];
                for (int i = 0; i < synset.Lemmas.Count; ++i)
                {
                    lemmaHashes[i] = await _textDecomposer.DecomposeAsync(
                        synset.Lemmas[i].SurfaceForm, cancellationToken).ConfigureAwait(false);
                }
                var counts = new int[lemmaHashes.Length];
                for (int i = 0; i < counts.Length; ++i) { counts[i] = 1; }
                map[(synset.SynsetOffset, synset.Type)] =
                    _hashing.CompositionId(lemmaHashes, counts);
            }
        }
        return map;
    }

    /// <summary>
    /// Emit one binary edge (subject role + object role) with substrate-
    /// canonical edge identity and source provenance. Edge identity matches
    /// <see cref="WordNetDecomposer"/>'s convention so the same logical edge
    /// from WordNet + OMW + future sources collapses to one row.
    /// </summary>
    private async Task EmitBinaryEdgeAsync(
        AtomId            edgeType,
        AtomId            subjectRole,
        AtomId            subjectHash,
        AtomId            objectRole,
        AtomId            objectHash,
        AtomId            sourceHash,
        CancellationToken cancellationToken)
    {
        var children = new[] { edgeType, subjectHash, objectHash };
        var counts   = new[] { 1, 1, 1 };
        var edgeHash = _hashing.CompositionId(children, counts);

        await _edgeEmission.EmitEdgeAsync(
            new EdgeRecord(EdgeTypeHash: edgeType, Hash: edgeHash),
            cancellationToken).ConfigureAwait(false);
        await _edgeEmission.EmitMemberAsync(
            new EdgeMemberRecord(
                EdgeTypeHash:    edgeType,
                EdgeHash:        edgeHash,
                RoleHash:        subjectRole,
                RolePosition:    0,
                ParticipantHash: subjectHash),
            cancellationToken).ConfigureAwait(false);
        await _edgeEmission.EmitMemberAsync(
            new EdgeMemberRecord(
                EdgeTypeHash:    edgeType,
                EdgeHash:        edgeHash,
                RoleHash:        objectRole,
                RolePosition:    0,
                ParticipantHash: objectHash),
            cancellationToken).ConfigureAwait(false);
        await _provenance.EmitEdgeProvenanceAsync(
            new EdgeProvenanceRecord(edgeType, edgeHash, sourceHash),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Split an OMW typed column of the form <c>{lang}:{kind}[:{subkind}]</c>
    /// (e.g. <c>cmn:lemma</c>, <c>arb:lemma:root</c>) into language code and
    /// primary kind. Subkinds are dropped for substrate emission — the
    /// morphological detail is recoverable from the OMW source via a follow-
    /// up morphology decomposer if needed. A column missing the colon
    /// separator is treated as kind-only with the supplied
    /// <paramref name="defaultLanguageCode"/>.
    /// </summary>
    private static (string Language, string Kind) SplitTypeColumn(
        string typeColumn,
        string defaultLanguageCode)
    {
        var firstColon = typeColumn.IndexOf(':');
        if (firstColon < 0) { return (defaultLanguageCode, typeColumn); }
        var language = typeColumn[..firstColon];
        var rest     = typeColumn[(firstColon + 1)..];
        var secondColon = rest.IndexOf(':');
        var kind = secondColon < 0 ? rest : rest[..secondColon];
        return (language, kind);
    }

    /// <summary>
    /// Parse an OMW synset identifier of the form <c>NNNNNNNN-T</c> where
    /// the 8-digit prefix is the Princeton synset offset and T ∈ {n,v,a,s,r}
    /// indicates POS.
    /// </summary>
    private static bool TryParseSynsetId(
        string                id,
        out long              offset,
        out WordNetSynsetType type)
    {
        offset = 0;
        type   = WordNetSynsetType.Noun;
        if (id.Length < 10 || id[8] != '-') { return false; }
        if (!long.TryParse(
                id.AsSpan(0, 8),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out offset))
        {
            return false;
        }
        switch (id[9])
        {
            case 'n': type = WordNetSynsetType.Noun;               return true;
            case 'v': type = WordNetSynsetType.Verb;               return true;
            case 'a': type = WordNetSynsetType.Adjective;          return true;
            case 's': type = WordNetSynsetType.AdjectiveSatellite; return true;
            case 'r': type = WordNetSynsetType.Adverb;             return true;
            default:                                                return false;
        }
    }
}
