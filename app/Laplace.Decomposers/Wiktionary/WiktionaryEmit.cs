using System.Runtime.InteropServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Wiktionary;

/// <summary>
/// THE single Wiktionary attestation emitter. Given a natively-parsed
/// <see cref="WiktionaryEntry"/> it stages content through the shared
/// <see cref="ContentTierSpine"/> (identical content ids to the former
/// grammar-witness path — <c>ResolveRoot</c> and <c>TryStageIntoBuilder</c>
/// return the same Merkle root) and emits the same typed, provenanced edges.
/// Both the bulk compose lane (<see cref="WiktionaryDecomposer"/>) and the
/// grammar-witness adapter route through here — one implementation per fact.
/// </summary>
internal static class WiktionaryEmit
{
    private static readonly double Trust = TC.AcademicCuratedUserInput;
    private static readonly Hash128 LanguageTypeId = EntityTypeRegistry.Language;

    /// <summary>
    /// Floor recorded on a composed tag set. Tags are words (tier 0-2 — tier is a floor, and
    /// single-grapheme tags exist), so a composition of them sits at 3. It is not an input to
    /// the id: <c>hash128_merkle</c> discards its tier argument by law (hash128.c:28).
    /// </summary>
    /// <summary>
    /// Every surface <see cref="Emit"/> would stage — collected so the deferred unit can
    /// <see cref="WiktionarySurfaceTrees.TryBuild"/> them on the compose fan before serial drain.
    /// </summary>
    public static void CollectSurfaces(
        WiktionaryEntry e, HashSet<string> into, HashSet<string> reusable)
    {
        Add(into, e.Word);
        if (e.Senses is { } senses)
            foreach (var s in senses)
            {
                AddAll(into, s.Glosses);
                AddAll(into, s.Examples);
                AddAll(into, s.SenseIds);
                CollectRelations(into, in s.Relations);
                bool hasSourceSenseId = s.SenseIds is { Count: > 0 }
                    && s.SenseIds.Any(static id => !string.IsNullOrWhiteSpace(id));
                if (!hasSourceSenseId)
                {
                    AddAll(into, s.Tags);
                    AddAll(into, s.LinkTargets);
                    AddAll(into, s.WikidataIds);
                }
                AddAll(into, s.Tags);
                AddAll(reusable, s.Tags);
            }

        if (e.Sounds is { } sounds)
            foreach (var snd in sounds)
            {
                Add(into, snd.Ipa);
                AddAll(into, snd.Tags);
                AddAll(reusable, snd.Tags);
            }

        CollectRelations(into, in e.Top);
        if (e.IncludeTranslations)
            AddAll(into, e.Translations);
        if (e.Forms is { } forms)
            foreach (var form in forms)
            {
                Add(into, form.FormText);
                AddAll(into, form.Tags);
                AddAll(reusable, form.Tags);
            }

        Add(into, e.EtymologyText);
        if (e.EtymologyTemplates is not { } templates) return;
        foreach (var t in templates)
        {
            if (t.Name is null || t.Args is not { } args) continue;
            if (!TryEtymologyRule(t.Name, out _, out string[] termArgs)) continue;
            foreach (var arg in termArgs)
            {
                if (!args.TryGetValue(arg, out var term)) continue;
                if (string.IsNullOrEmpty(term) || term == "-") continue;
                Add(into, term);
            }
        }
    }

    private static void CollectRelations(HashSet<string> into, in WiktionaryEntry.RelationBlock r)
    {
        AddAll(into, r.Synonyms);
        AddAll(into, r.Antonyms);
        AddAll(into, r.Hyponyms);
        AddAll(into, r.Meronyms);
        AddAll(into, r.Holonyms);
        AddAll(into, r.Related);
        AddAll(into, r.Hypernyms);
        AddAll(into, r.Coordinate);
        AddAll(into, r.Derived);
    }

    private static void Add(HashSet<string> into, string? s)
    {
        if (!string.IsNullOrEmpty(s)) into.Add(s);
    }

    private static void AddAll(HashSet<string> into, List<string>? list)
    {
        if (list is null) return;
        foreach (var s in list)
            Add(into, s);
    }

    /// <summary>Staging needs the surfaces; the _dis1 weight rides along to EmitWords.</summary>
    private static void AddAll(HashSet<string> into, List<WiktionaryMember>? list)
    {
        if (list is null) return;
        foreach (var m in list)
            Add(into, m.Word);
    }

    public static void Emit(WiktionaryEntry e, SubstrateChangeBuilder b) =>
        Emit(e, b, roots: null, coords: null);

    /// <summary>
    /// When <paramref name="roots"/> is non-null, every surface was already emitted into the
    /// builder and this walk only looks up root ids + writes attestations/entities.
    /// </summary>
    public static void Emit(
        WiktionaryEntry e, SubstrateChangeBuilder b,
        IReadOnlyDictionary<string, Hash128>? roots,
        IReadOnlyDictionary<string, WiktionarySurfaceTrees.RootCoord>? coords)
    {
        if (!Stage(b, e.Word, roots, out Hash128 wordId)) return;
        byte wordTier = CompositionLaw.TryFace(e.Word, out CompositionLaw.Face wordFace) ? wordFace.Tier : (byte)0;

        // langCtx scopes every word->word relation edge and the synset memberships
        // below. The language was already resolved here and then went NOWHERE except
        // HAS_LANGUAGE on the word — the edges stayed language-free, which is the
        // exact defect OMWGrammarWitness documents (GH #867): with no scope on the
        // edge, cross-language IS_SYNONYM_OF candidates compete as senses and a
        // reader cannot tell which language attested the claim.
        Hash128? langCtx = null;
        byte languageTier = 0;
        if (e.LangCode is { Length: > 0 } lc)
        {
            Hash128 langEntity = LanguageReference.Resolve(lc);
            languageTier = CompositionLaw.TryFace(lc.Trim(), out CompositionLaw.Face langFace)
                ? langFace.Tier
                : (byte)0;
            VocabularyNames.TrackLanguage(WiktionaryDecomposer.VocabularyNames, lc);
            b.AddEntity(new EntityRow(langEntity, languageTier, LanguageTypeId));
            langCtx = langEntity;
        }

        Hash128? posId = null;
        byte posTier = 0;
        bool isVerb = false;
        if (e.Pos is { Length: > 0 } pos)
        {
            isVerb = pos.Equals("verb", StringComparison.OrdinalIgnoreCase);
            posId = PosReference.Emit(
                b, pos, PosReference.PosTagset.Wiktionary,
                WiktionaryDecomposer.Source, Trust,
                WiktionaryDecomposer.VocabularyNames);
            posTier = PosReference.ResolveFace(pos, PosReference.PosTagset.Wiktionary).Tier;
        }

        if (e.Senses is { } senses)
            foreach (var s in senses)
            {
                if (WiktionarySenseAnchor.Declare(
                        b, e.Word, wordId, wordTier, langCtx, languageTier, posId, posTier,
                        s, WiktionaryDecomposer.Source, roots, coords, e.Pos) is not { } senseId)
                    continue;

                AttestResolved(b, wordId, WiktionarySource.SensesTypeId, senseId, langCtx);
                WalkSense(b, senseId, s, isVerb, langCtx, roots, coords);
                RouteSynsetLinks(b, senseId, s, langCtx);
                RouteWikidataLinks(b, senseId, s, langCtx);
            }

        WalkSounds(b, wordId, e.Sounds, roots, coords);
        WalkRelations(b, wordId, in e.Top, isVerb, context: langCtx, roots);
        if (e.IncludeTranslations && e.Translations is { } tr)
            foreach (var t in tr)
            {
                if (!Stage(b, t.Word, roots, out var trId)) continue;
                if (t.Dis1 > 0.0)
                    b.AddAttestation(NativeAttestation.Categorical(
                        wordId, TranslationRelation, trId, WiktionaryDecomposer.Source, Trust,
                        magnitude: t.Dis1, arenaScale: 1.0, contextId: null));
                else
                    Attest(b, wordId, TranslationRelation, trId, null);
            }
        WalkForms(b, wordId, e.Forms, roots, coords);
        WalkEtymology(b, wordId, e, roots);
    }

    private static void WalkSense(
        SubstrateChangeBuilder b, Hash128 senseId, WiktionaryEntry.Sense s, bool isVerb,
        Hash128? langCtx, IReadOnlyDictionary<string, Hash128>? roots,
        IReadOnlyDictionary<string, WiktionarySurfaceTrees.RootCoord>? coords)
    {
        if (s.Glosses is { } gl)
            foreach (var g in gl)
                if (Stage(b, g, roots, out var gId)) Attest(b, senseId, "glosses", gId, langCtx);

        if (s.Examples is { } ex)
            foreach (var x in ex)
                if (Stage(b, x, roots, out var xId)) Attest(b, senseId, "examples", xId, langCtx);

        WalkRelations(b, senseId, in s.Relations, isVerb, langCtx, roots);

        EmitMembers(b, senseId, "tags", s.Tags, langCtx, roots);
    }

    private static void WalkRelations(
        SubstrateChangeBuilder b, Hash128 wordId, in WiktionaryEntry.RelationBlock r, bool isVerb,
        Hash128? context, IReadOnlyDictionary<string, Hash128>? roots)
    {
        _ = isVerb;
        EmitWords(b, wordId, "synonyms", r.Synonyms, context, roots);
        EmitWords(b, wordId, "antonyms", r.Antonyms, context, roots);
        EmitWords(b, wordId, "hyponyms", r.Hyponyms, context, roots);
        EmitWords(b, wordId, "meronyms", r.Meronyms, context, roots);
        EmitWords(b, wordId, "holonyms", r.Holonyms, context, roots);
        EmitWords(b, wordId, "related", r.Related, context, roots);
        EmitWords(b, wordId, "hypernyms", r.Hypernyms, context, roots);
        EmitWords(b, wordId, "coordinate_terms", r.Coordinate, context, roots);
        EmitWords(b, wordId, "derived", r.Derived, context, roots);
    }

    private static void EmitWords(
        SubstrateChangeBuilder b, Hash128 wordId, string type, List<WiktionaryMember>? words,
        Hash128? context, IReadOnlyDictionary<string, Hash128>? roots)
    {
        if (words is null) return;
        foreach (var w in words)
        {
            if (!Stage(b, w.Word, roots, out var id)) continue;
            // wiktextract's own association weight. Zero means the source computed none,
            // which is not the same as 1.0 and must not be promoted to it -- those enter
            // unscored, exactly as before.
            if (w.Dis1 > 0.0)
                b.AddAttestation(NativeAttestation.Categorical(
                    wordId, type, id, WiktionaryDecomposer.Source, Trust,
                    magnitude: w.Dis1, arenaScale: 1.0, contextId: context));
            else
                Attest(b, wordId, type, id, context);
        }
    }

    private static void WalkSounds(
        SubstrateChangeBuilder b, Hash128 wordId, List<WiktionaryEntry.Sound>? sounds,
        IReadOnlyDictionary<string, Hash128>? roots,
        IReadOnlyDictionary<string, WiktionarySurfaceTrees.RootCoord>? coords)
    {
        if (sounds is null) return;
        foreach (var snd in sounds)
        {
            if (!Stage(b, snd.Ipa, roots, out var ipaId)) continue;
            Attest(b, wordId, "ipa", ipaId, null);
            EmitMembers(b, ipaId, "tags", snd.Tags, null, roots);
        }
    }

    private static void WalkForms(
        SubstrateChangeBuilder b, Hash128 wordId, List<WiktionaryEntry.Form>? forms,
        IReadOnlyDictionary<string, Hash128>? roots,
        IReadOnlyDictionary<string, WiktionarySurfaceTrees.RootCoord>? coords)
    {
        if (forms is null) return;
        foreach (var form in forms)
        {
            if (!Stage(b, form.FormText, roots, out var formId)) continue;

            // A FORM TABLE LISTS ITS OWN LEMMA. Wiktionary's form list includes the headword
            // itself (the singular in a noun's table, the infinitive in a verb's), so
            // formId == wordId and this emitted `cat FORM_OF cat` — a claim with no losing
            // condition. Nothing could rate, corroborate or refute it, because there is no
            // world in which a word is not a form of itself.
            //
            // MEASURED 2026-08-16 on laplace.consensus_r_form_of_h0: 143,612 self-edges in
            // 8,945,068 rows (1.6%), one partition of eight — ~1.15M rows substrate-wide.
            // They also cost every reader: consensus.edges_raw('both') mirrored each one,
            // and at p_limit=12 cat/green/house/run each spent 2 of 12 beam slots on the
            // pair, displacing real strands.
            //
            // Skipped HERE and not in SubstrateChangeBuilder.AddAttestation: a self-edge is
            // vacuous for FORM_OF specifically, not universally. Surface ids are
            // content-addressed and language-independent, so two languages spelling a word
            // identically produce a legitimate IS_TRANSLATION_OF X->X, and a blanket guard
            // at the chokepoint would silently delete it.
            //
            // HAS_FEATURE below is still emitted: when the form IS the lemma its tags are
            // the lemma's own morphology, which is testimony about the headword.
            if (formId != wordId)
                Attest(b, wordId, "forms", formId, null);
            EmitMembers(b, formId, "tags", form.Tags, null, roots);
        }
    }

    private static void WalkEtymology(
        SubstrateChangeBuilder b, Hash128 wordId, WiktionaryEntry e,
        IReadOnlyDictionary<string, Hash128>? roots)
    {
        if (Stage(b, e.EtymologyText, roots, out var etyId))
            Attest(b, wordId, "etymology_text", etyId, null);

        if (e.EtymologyTemplates is not { } templates) return;
        foreach (var t in templates)
        {
            if (t.Name is null || t.Args is not { } args) continue;
            if (!TryEtymologyRule(t.Name, out string etymType, out string[] termArgs)) continue;
            foreach (var arg in termArgs)
            {
                if (!args.TryGetValue(arg, out var term)) continue;
                if (string.IsNullOrEmpty(term) || term == "-") continue;
                if (!Stage(b, term, roots, out var termId)) continue;
                Attest(b, wordId, etymType, termId, null);
            }
        }
    }

    // The template name is the predicate. The argument index is where that template puts the etymon.
    private static bool TryEtymologyRule(
        string name, out string etymType, out string[] termArgs)
    {
        etymType = name;
        termArgs = name switch
        {
            "bor" or "borrowed" or "inh" or "inherited" or "der" or "derived" => ["3"],
            "cog" or "cognate" or "doublet" or "dbt" or "suffix" or "suf"
                or "back-form" or "back-formation" or "bf" => ["2"],
            "prefix" or "pre" => ["3"],
            "af" or "affix" or "com" or "compound" or "blend" => ["2", "3", "4"],
            _ => [],
        };
        return termArgs.Length > 0;
    }

    private static void RouteSynsetLinks(
        SubstrateChangeBuilder b, Hash128 senseId, WiktionaryEntry.Sense s, Hash128? langCtx)
    {
        if (s.LinkTargets is not { } links) return;
        var seen = new HashSet<Hash128>();
        foreach (var key in links)
            if (SourceEntityIdConventions.ResolveSynsetAnchor(key) is { } syn
                && syn != default && seen.Add(syn))
                AttestResolved(b, senseId, WiktionarySource.LinksTypeId, syn, langCtx);
    }

    private static void RouteWikidataLinks(
        SubstrateChangeBuilder b, Hash128 senseId, WiktionaryEntry.Sense s, Hash128? langCtx)
    {
        if (s.WikidataIds is not { } items) return;
        var seen = new HashSet<Hash128>();
        foreach (string raw in items)
        {
            string qid = raw.Trim().ToUpperInvariant();
            if (qid.Length < 2 || qid[0] != 'Q'
                || !long.TryParse(qid.AsSpan(1), out long ordinal) || ordinal <= 0)
                continue;
            if (ReferenceAnchor.Declare(
                    b, ReferenceIdentityKind.WikidataItem, qid,
                    EntityTypeRegistry.WikidataItem, WiktionaryDecomposer.Source) is not { } itemId
                || !seen.Add(itemId))
                continue;
            AttestResolved(b, senseId, WiktionarySource.WikidataTypeId, itemId, langCtx);
        }
    }

    private static bool Stage(
        SubstrateChangeBuilder b, string? surface, IReadOnlyDictionary<string, Hash128>? roots,
        out Hash128 id)
    {
        id = default;
        if (string.IsNullOrEmpty(surface)) return false;
        if (roots is not null)
            return roots.TryGetValue(surface, out id);
        return WiktionarySurfaceTrees.TryStage(b, surface, WiktionaryDecomposer.Source, out id);
    }

    // Spelled once: the scored and unscored arms must not drift onto different relations.
    private const string TranslationRelation = "translations";

    private static void EmitMembers(
        SubstrateChangeBuilder b, Hash128 subject, string predicate, List<string>? members,
        Hash128? context, IReadOnlyDictionary<string, Hash128>? roots)
    {
        if (members is null) return;
        foreach (string member in members)
            if (Stage(b, member, roots, out Hash128 id))
                Attest(b, subject, predicate, id, context);
    }

    private static void Attest(
        SubstrateChangeBuilder b, Hash128 subject, string typeName, Hash128 objectId, Hash128? context) =>
        b.AddAttestation(NativeAttestation.Categorical(
            subject, typeName, objectId, WiktionaryDecomposer.Source, Trust, contextId: context));

    private static void AttestResolved(
        SubstrateChangeBuilder b, Hash128 subject, Hash128 typeId, Hash128 objectId, Hash128? context) =>
        b.AddAttestation(NativeAttestation.CategoricalResolved(
            subject, typeId, objectId, WiktionaryDecomposer.Source, context, Trust));
}
