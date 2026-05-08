namespace Laplace.Decomposers.Wiktionary;

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Decomposers.Text;
using Laplace.Pipeline.Abstractions;

/// <summary>
/// F4 — Wiktionary (kaikki.org) seed decomposer, lexical + translation
/// slice.
///
/// Wiktionary contributes per-language surface-form detail below the
/// synset granularity that WordNet/OMW provide: synonyms, hypernyms,
/// hyponyms, meronyms, holonyms, coordinate-term lexical relations, plus
/// direct word-to-word translation edges across hundreds of languages.
/// The translation edges are independent attestation alongside OMW's
/// synset-anchored cross-language alignment — both contribute to the edge
/// density that makes cross-language equivalence emerge per substrate
/// invariant 4.
///
/// Decomposer flow per Wiktionary entry (word, lang, pos, relations[],
/// translations[]):
///   1. F1.Decompose(word) → tier-1 word entity hash. Cross-source dedup
///      with WordNet / OMW / Tatoeba / user content is automatic.
///   2. Emit entity provenance for the word entity.
///   3. has_pos edge: word → has_pos → pos_concept (e.g.,
///      "noun" / "verb" / "adjective"). PoS labels are themselves
///      substrate concept entities resolved via IConceptEntityResolver
///      from their canonical names.
///   4. Lexical relation edges: word → {synonym|hypernym|hyponym|meronym|
///      holonym|coordinate_term} → other_word. Each relation array
///      element produces one binary edge.
///   5. Translation edges: word → has_translation → target_word. The
///      target word is F1-decomposed; per-edge provenance carries the OMW
///      pattern (Wiktionary observed the translation, not the languages).
///
/// Sense glosses, etymology, pronunciation (IPA), and inflections are
/// load-bearing but live on the raw Wiktionary entry; their decomposers
/// attach as follow-up units that read the same JSONL.
///
/// Phase 4 / Track F4.
/// </summary>
public sealed class WiktionaryDecomposer
{
    private static readonly int[] BinaryEdgeRleCounts = new[] { 1, 1, 1 };

    private readonly TextDecomposer         _textDecomposer;
    private readonly IIdentityHashing       _hashing;
    private readonly IConceptEntityResolver _conceptResolver;
    private readonly IEdgeEmission          _edgeEmission;
    private readonly IProvenance            _provenance;

    public WiktionaryDecomposer(
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

    public async Task DecomposeAsync(
        string             wiktionaryJsonlFile,
        CancellationToken  cancellationToken)
    {
        if (!File.Exists(wiktionaryJsonlFile)) { return; }

        var sourceHash       = await _provenance.ResolveSourceAsync(
            "wiktionary_kaikki", cancellationToken).ConfigureAwait(false);
        var hasLanguage      = _conceptResolver.Resolve("has_language");
        var hasPosEdge       = _conceptResolver.Resolve("has_pos");
        var hasSenseEdge     = _conceptResolver.Resolve("has_sense");
        var hasTranslation   = _conceptResolver.Resolve("has_translation");
        var hasEtymology     = _conceptResolver.Resolve("has_etymology");
        var hasPronunciation = _conceptResolver.Resolve("has_pronunciation");
        var hasForm          = _conceptResolver.Resolve("has_form");
        var subjectRole      = _conceptResolver.Resolve("subject");
        var objectRole       = _conceptResolver.Resolve("object");

        // Cache resolved concepts: PoS labels (~10-20 across languages),
        // lexical-relation edge types (6 fixed), translation target words
        // resolved per-row via F1.
        var posCache         = new Dictionary<string, AtomId>();
        var lexicalEdgeTypes = new LexicalEdgeTypes(_conceptResolver);

        foreach (var entry in WiktionaryJsonlParser.Parse(wiktionaryJsonlFile))
        {
            if (string.IsNullOrEmpty(entry.Word)) { continue; }

            var wordHash = await _textDecomposer.DecomposeAsync(
                entry.Word, cancellationToken).ConfigureAwait(false);
            await _provenance.EmitEntityProvenanceAsync(
                new EntityProvenanceRecord(EntityHash: wordHash, SourceHash: sourceHash),
                cancellationToken).ConfigureAwait(false);

            // has_language edge — word → has_language → language_entity.
            // Language entities exist via Iso639Decomposer; cross-source
            // dedup is automatic via shared codepoint compositions of the
            // ISO 639-3 code (e.g. "en", "jpn", "cmn"). Tatoeba's seed
            // decomposer follows the same pattern at sentence granularity.
            if (entry.LanguageCode.Length > 0)
            {
                var langHash = await _textDecomposer.DecomposeAsync(
                    entry.LanguageCode, cancellationToken).ConfigureAwait(false);
                await EmitBinaryEdgeAsync(
                    edgeType:    hasLanguage,
                    subjectRole: subjectRole,
                    subjectHash: wordHash,
                    objectRole:  objectRole,
                    objectHash:  langHash,
                    sourceHash:  sourceHash,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            // has_pos edge — only when PoS is non-empty.
            if (entry.Pos.Length > 0)
            {
                if (!posCache.TryGetValue(entry.Pos, out var posHash))
                {
                    posHash = _conceptResolver.Resolve(entry.Pos);
                    posCache[entry.Pos] = posHash;
                }
                await EmitBinaryEdgeAsync(
                    edgeType:    hasPosEdge,
                    subjectRole: subjectRole,
                    subjectHash: wordHash,
                    objectRole:  objectRole,
                    objectHash:  posHash,
                    sourceHash:  sourceHash,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            // has_etymology edge: word → has_etymology → etymology_text_entity.
            // The etymology block can be many lines (proto-IE roots, derivation
            // chains, semantic shift notes); F1 hashes the full multi-line
            // block as one tier-1 composition. Cross-source dedup with any
            // future etymological source is automatic via content addressing.
            if (entry.EtymologyText.Length > 0)
            {
                var etymologyHash = await _textDecomposer.DecomposeAsync(
                    entry.EtymologyText, cancellationToken).ConfigureAwait(false);
                await _provenance.EmitEntityProvenanceAsync(
                    new EntityProvenanceRecord(EntityHash: etymologyHash, SourceHash: sourceHash),
                    cancellationToken).ConfigureAwait(false);
                await EmitBinaryEdgeAsync(
                    edgeType:    hasEtymology,
                    subjectRole: subjectRole,
                    subjectHash: wordHash,
                    objectRole:  objectRole,
                    objectHash:  etymologyHash,
                    sourceHash:  sourceHash,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            // has_pronunciation edges: word → has_pronunciation → ipa_entity.
            // Multiple IPA strings per word (per-dialect / per-register
            // variants) all attach via individual edges. The /…/ slashes are
            // part of the IPA convention and stay in the content; consumers
            // looking for the bare phoneme sequence use a downstream
            // PronunciationDecomposer slice.
            foreach (var ipa in entry.Pronunciations)
            {
                if (string.IsNullOrEmpty(ipa)) { continue; }
                var ipaHash = await _textDecomposer.DecomposeAsync(
                    ipa, cancellationToken).ConfigureAwait(false);
                await _provenance.EmitEntityProvenanceAsync(
                    new EntityProvenanceRecord(EntityHash: ipaHash, SourceHash: sourceHash),
                    cancellationToken).ConfigureAwait(false);
                await EmitBinaryEdgeAsync(
                    edgeType:    hasPronunciation,
                    subjectRole: subjectRole,
                    subjectHash: wordHash,
                    objectRole:  objectRole,
                    objectHash:  ipaHash,
                    sourceHash:  sourceHash,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            // has_form edges: word → has_form → form_entity. Inflections
            // (plural, comparative, conjugations) and alternative spellings
            // share this edge type; tag distinctions (plural vs alternative)
            // collapse this slice — recoverable via downstream MorphologyDecomposer
            // that reads the same forms[] array with tags preserved.
            foreach (var form in entry.Forms)
            {
                if (string.IsNullOrEmpty(form)) { continue; }
                var formHash = await _textDecomposer.DecomposeAsync(
                    form, cancellationToken).ConfigureAwait(false);
                await _provenance.EmitEntityProvenanceAsync(
                    new EntityProvenanceRecord(EntityHash: formHash, SourceHash: sourceHash),
                    cancellationToken).ConfigureAwait(false);
                await EmitBinaryEdgeAsync(
                    edgeType:    hasForm,
                    subjectRole: subjectRole,
                    subjectHash: wordHash,
                    objectRole:  objectRole,
                    objectHash:  formHash,
                    sourceHash:  sourceHash,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            // has_sense edges: word → has_sense → gloss_entity. Wiktionary's
            // primary gloss per sense is what we emit; alternates collapse
            // when their bytes match across sources via F1 content addressing.
            // Same has_sense edge_type as WordNet's lemma→synset attachment,
            // so cross-source attestation of sense identity accumulates on
            // the has_sense edge type concept entity.
            foreach (var gloss in entry.Glosses)
            {
                if (string.IsNullOrEmpty(gloss)) { continue; }
                var glossHash = await _textDecomposer.DecomposeAsync(
                    gloss, cancellationToken).ConfigureAwait(false);
                await _provenance.EmitEntityProvenanceAsync(
                    new EntityProvenanceRecord(EntityHash: glossHash, SourceHash: sourceHash),
                    cancellationToken).ConfigureAwait(false);
                await EmitBinaryEdgeAsync(
                    edgeType:    hasSenseEdge,
                    subjectRole: subjectRole,
                    subjectHash: wordHash,
                    objectRole:  objectRole,
                    objectHash:  glossHash,
                    sourceHash:  sourceHash,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            // Lexical relation edges.
            await EmitLexicalRelationsAsync(
                wordHash, entry.Synonyms,        lexicalEdgeTypes.Synonym,
                subjectRole, objectRole, sourceHash, cancellationToken).ConfigureAwait(false);
            await EmitLexicalRelationsAsync(
                wordHash, entry.Hypernyms,       lexicalEdgeTypes.Hypernym,
                subjectRole, objectRole, sourceHash, cancellationToken).ConfigureAwait(false);
            await EmitLexicalRelationsAsync(
                wordHash, entry.Hyponyms,        lexicalEdgeTypes.Hyponym,
                subjectRole, objectRole, sourceHash, cancellationToken).ConfigureAwait(false);
            await EmitLexicalRelationsAsync(
                wordHash, entry.Meronyms,        lexicalEdgeTypes.Meronym,
                subjectRole, objectRole, sourceHash, cancellationToken).ConfigureAwait(false);
            await EmitLexicalRelationsAsync(
                wordHash, entry.Holonyms,        lexicalEdgeTypes.Holonym,
                subjectRole, objectRole, sourceHash, cancellationToken).ConfigureAwait(false);
            await EmitLexicalRelationsAsync(
                wordHash, entry.CoordinateTerms, lexicalEdgeTypes.CoordinateTerm,
                subjectRole, objectRole, sourceHash, cancellationToken).ConfigureAwait(false);

            // Translation edges (word ↔ target_word, language-agnostic edge
            // type; the target's language is recoverable from its own
            // Wiktionary entry's lang_code edge once that decomposer slice
            // lands. No anchor language on the edge itself.)
            foreach (var t in entry.Translations)
            {
                if (string.IsNullOrEmpty(t.TargetWord)) { continue; }
                var targetHash = await _textDecomposer.DecomposeAsync(
                    t.TargetWord, cancellationToken).ConfigureAwait(false);
                await _provenance.EmitEntityProvenanceAsync(
                    new EntityProvenanceRecord(EntityHash: targetHash, SourceHash: sourceHash),
                    cancellationToken).ConfigureAwait(false);
                await EmitBinaryEdgeAsync(
                    edgeType:    hasTranslation,
                    subjectRole: subjectRole,
                    subjectHash: wordHash,
                    objectRole:  objectRole,
                    objectHash:  targetHash,
                    sourceHash:  sourceHash,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task EmitLexicalRelationsAsync(
        AtomId                            sourceWord,
        IReadOnlyList<WiktionaryRelation> relations,
        AtomId                            edgeType,
        AtomId                            subjectRole,
        AtomId                            objectRole,
        AtomId                            sourceHash,
        CancellationToken                 cancellationToken)
    {
        foreach (var rel in relations)
        {
            if (string.IsNullOrEmpty(rel.Word)) { continue; }
            var relWordHash = await _textDecomposer.DecomposeAsync(
                rel.Word, cancellationToken).ConfigureAwait(false);
            await _provenance.EmitEntityProvenanceAsync(
                new EntityProvenanceRecord(EntityHash: relWordHash, SourceHash: sourceHash),
                cancellationToken).ConfigureAwait(false);
            await EmitBinaryEdgeAsync(
                edgeType:    edgeType,
                subjectRole: subjectRole,
                subjectHash: sourceWord,
                objectRole:  objectRole,
                objectHash:  relWordHash,
                sourceHash:  sourceHash,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EmitBinaryEdgeAsync(
        AtomId            edgeType,
        AtomId            subjectRole,
        AtomId            subjectHash,
        AtomId            objectRole,
        AtomId            objectHash,
        AtomId            sourceHash,
        CancellationToken cancellationToken)
    {
        var edgeHash = _hashing.CompositionId(
            new[] { edgeType, subjectHash, objectHash }, BinaryEdgeRleCounts);

        await _edgeEmission.EmitEdgeAsync(
            new EdgeRecord(EdgeTypeHash: edgeType, Hash: edgeHash),
            cancellationToken).ConfigureAwait(false);
        await _edgeEmission.EmitMemberAsync(
            new EdgeMemberRecord(edgeType, edgeHash, subjectRole, 0, subjectHash),
            cancellationToken).ConfigureAwait(false);
        await _edgeEmission.EmitMemberAsync(
            new EdgeMemberRecord(edgeType, edgeHash, objectRole, 0, objectHash),
            cancellationToken).ConfigureAwait(false);
        await _provenance.EmitEdgeProvenanceAsync(
            new EdgeProvenanceRecord(edgeType, edgeHash, sourceHash),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cached resolution of the six lexical-relation edge type concepts.
    /// Names match WordNet pointer concepts (synonym, hypernym, hyponym,
    /// member_meronym, member_holonym, coordinate_term) so cross-source
    /// dedup with WordNetPointerLinker emissions works for the relations
    /// they overlap on. Wiktionary's relation arrays don't distinguish
    /// member/substance/part flavors of meronymy/holonymy — this slice
    /// uses the member_* canonical names; substance/part flavors are
    /// follow-up work if Wiktionary's tag arrays carry the distinction.
    /// </summary>
    private readonly record struct LexicalEdgeTypes(
        AtomId Synonym,
        AtomId Hypernym,
        AtomId Hyponym,
        AtomId Meronym,
        AtomId Holonym,
        AtomId CoordinateTerm)
    {
        public LexicalEdgeTypes(IConceptEntityResolver r) : this(
            Synonym:        r.Resolve("synonym"),
            Hypernym:       r.Resolve("hypernym"),
            Hyponym:        r.Resolve("hyponym"),
            Meronym:        r.Resolve("member_meronym"),
            Holonym:        r.Resolve("member_holonym"),
            CoordinateTerm: r.Resolve("coordinate_term"))
        { }
    }
}
