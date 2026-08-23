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
    private const double Trust = TC.AcademicCuratedUserInput;
    private static readonly Hash128 LanguageTypeId = EntityTypeRegistry.Language;

    /// <summary>
    /// Floor recorded on a composed tag set. Tags are words (tier 0-2 — tier is a floor, and
    /// single-grapheme tags exist), so a composition of them sits at 3. It is not an input to
    /// the id: <c>hash128_merkle</c> discards its tier argument by law (hash128.c:28).
    /// </summary>
    private const byte CollectionTier = 3;

    private static readonly HashSet<string> RegisterTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "archaic", "obsolete", "dated", "slang", "colloquial", "informal", "formal",
        "vulgar", "offensive", "derogatory", "humorous", "euphemistic", "dialectal",
        "regional", "literary", "poetic", "technical", "rare", "nonstandard",
        "historical", "figurative",
    };

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
                CollectRelations(into, in s.Relations);
                if (s.Tags is { } tags)
                    foreach (var tag in tags)
                        if (RegisterTags.Contains(tag))
                        {
                            Add(into, tag);
                            Add(reusable, tag);
                        }
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

        // langCtx scopes every word->word relation edge and the synset memberships
        // below. The language was already resolved here and then went NOWHERE except
        // HAS_LANGUAGE on the word — the edges stayed language-free, which is the
        // exact defect OMWGrammarWitness documents (GH #867): with no scope on the
        // edge, cross-language IS_SYNONYM_OF candidates compete as senses and a
        // reader cannot tell which language attested the claim.
        Hash128? langCtx = null;
        if (e.LangCode is { Length: > 0 } lc)
        {
            Hash128 langEntity = LanguageReference.Resolve(lc);
            VocabularyNames.TrackLanguage(WiktionaryDecomposer.VocabularyNames, lc);
            b.AddEntity(new EntityRow(langEntity, EntityTier.Word, LanguageTypeId, WiktionaryDecomposer.Source));
            // HAS_LANGUAGE keeps a null context: the object IS the language.
            b.AddAttestation(NativeAttestation.Categorical(
                wordId, "HAS_LANGUAGE", langEntity, WiktionaryDecomposer.Source, Trust));
            langCtx = langEntity;
        }

        Hash128? posId = null;
        bool isVerb = false;
        if (e.Pos is { Length: > 0 } pos)
        {
            isVerb = pos.Equals("verb", StringComparison.OrdinalIgnoreCase);
            posId = PosReference.Attest(
                b, wordId, pos, PosReference.PosTagset.Wiktionary,
                WiktionaryDecomposer.Source, langCtx, Trust,
                WiktionaryDecomposer.VocabularyNames);
        }

        if (e.Senses is { } senses)
            foreach (var s in senses)
            {
                if (WiktionarySenseAnchor.Declare(
                        b, wordId, langCtx, posId, s, WiktionaryDecomposer.Source) is not { } senseId)
                    continue;

                AttestResolved(b, wordId, WiktionarySource.HasSenseTypeId, senseId, langCtx);
                AttestResolved(b, senseId, WiktionarySource.HasNameAliasTypeId, wordId, langCtx);
                if (langCtx is { } langId)
                    AttestResolved(b, senseId, WiktionarySource.HasLanguageTypeId, langId, null);
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
                if (Stage(b, g, roots, out var gId)) Attest(b, senseId, "HAS_DEFINITION", gId, langCtx);

        if (s.Examples is { } ex)
            foreach (var x in ex)
                if (Stage(b, x, roots, out var xId)) Attest(b, senseId, "HAS_EXAMPLE", xId, langCtx);

        WalkRelations(b, senseId, in s.Relations, isVerb, langCtx, roots);

        // A sense's register is one reading -- "archaic AND humorous" -- not two independent
        // claims. Same shape as HAS_FEATURE and the same fix: one composition, one edge, one
        // thing a second witness can corroborate or refute as a whole.
        if (s.Tags is { } tags)
        {
            List<string>? register = null;
            foreach (var tag in tags)
                if (RegisterTags.Contains(tag)) (register ??= []).Add(tag);
            if (TryStageSet(b, register, roots, coords, out var registerId))
                Attest(b, senseId, "HAS_USAGE_REGISTER", registerId, langCtx);
        }
    }

    private static void WalkRelations(
        SubstrateChangeBuilder b, Hash128 wordId, in WiktionaryEntry.RelationBlock r, bool isVerb,
        Hash128? context, IReadOnlyDictionary<string, Hash128>? roots)
    {
        EmitWords(b, wordId, "IS_SYNONYM_OF", r.Synonyms, context, roots);
        EmitWords(b, wordId, "IS_ANTONYM_OF", r.Antonyms, context, roots);
        EmitWords(b, wordId, "HAS_HYPONYM", r.Hyponyms, context, roots);
        EmitWords(b, wordId, "HAS_PART", r.Meronyms, context, roots);
        EmitWords(b, wordId, "IS_PART_OF", r.Holonyms, context, roots);
        EmitWords(b, wordId, "RELATED_TO", r.Related, context, roots);
        EmitWords(b, wordId, isVerb ? "MANNER_OF" : "HAS_HYPERNYM", r.Hypernyms, context, roots);
        EmitWords(b, wordId, "IS_COORDINATE_TERM_WITH", r.Coordinate, context, roots);

        // Derived reverses direction: derived-word DERIVED_FROM this word.
        if (r.Derived is { } derived)
            foreach (var d in derived)
            {
                if (!Stage(b, d.Word, roots, out var dId)) continue;
                if (d.Dis1 > 0.0)
                    b.AddAttestation(NativeAttestation.Categorical(
                        dId, DerivedRelation, wordId, WiktionaryDecomposer.Source, Trust,
                        magnitude: d.Dis1, arenaScale: 1.0, contextId: context));
                else
                    Attest(b, dId, DerivedRelation, wordId, context);
            }
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
            // attestations.context_id is ONE bytea, so the previous shape took the first
            // stageable dialect tag and dropped the rest — data loss across 5,192,208
            // TRANSCRIBES_AS rows, forced by the schema rather than chosen. A set composition
            // fits in the slot, so every tag survives.
            Hash128? dialectCtx = TryStageSet(b, snd.Tags, roots, coords, out var dialectSetId)
                ? dialectSetId : null;
            Attest(b, wordId, "TRANSCRIBES_AS", ipaId, dialectCtx);
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
                Attest(b, formId, "FORM_OF", wordId, null);
            // A form's tags are ONE morphological analysis, not N independent claims. Emitting
            // them as N edges gives the analysis no id, so nothing can rate, corroborate or
            // refute it as a whole. One composition, one edge, one adjudicable claim.
            if (TryStageSet(b, form.Tags, roots, coords, out var featureSetId))
                Attest(b, formId, "HAS_FEATURE", featureSetId, null);
        }
    }

    /// <summary>
    /// Compose a tag list into one set entity and return its id. False when nothing stageable
    /// remains — the caller then emits no edge, which is the honest answer for an empty analysis.
    /// </summary>
    /// <remarks>
    /// A ONE-tag list returns that tag's own id by the tier-floor collapse law, so the degenerate
    /// case stays a direct edge to the tag and no wrapper entity is minted.
    ///
    /// Member coordinates come from each tag's own tier tree rather than from this builder,
    /// because a tag emitted in an earlier batch is suppressed by the existence bitmap and has no
    /// staged physicality here. A tag whose geometry is unavailable is dropped from the set
    /// rather than substituted with the origin: forging a member coordinate would move the
    /// centroid of every set that contains it.
    /// </remarks>
    private static bool TryStageSet(
        SubstrateChangeBuilder b, List<string>? tags,
        IReadOnlyDictionary<string, Hash128>? roots,
        IReadOnlyDictionary<string, WiktionarySurfaceTrees.RootCoord>? rootCoords,
        out Hash128 setId)
    {
        setId = default;
        if (tags is null || tags.Count == 0) return false;

        var ids = new List<Hash128>(tags.Count);
        var coords = new List<double>(tags.Count * 4);
        Span<double> c = stackalloc double[4];
        foreach (var tag in tags)
        {
            // A member that cannot be staged or placed FAILS THE WHOLE SET. Dropping it and
            // composing the remainder is worse than emitting nothing: a partial set is a
            // DIFFERENT set with a different merkle id, so the same analysis would land under
            // two ids depending on cache state, and neither would be wrong on its face. No
            // edge is recoverable -- the substrate can prove an absence (INVENTION §8) and
            // cannot prove a silently truncated set.
            Hash128 tagId;
            WiktionarySurfaceTrees.RootCoord coord;
            if (roots is not null)
            {
                if (!roots.TryGetValue(tag, out tagId)
                    || rootCoords is null
                    || !rootCoords.TryGetValue(tag, out coord)) return false;
            }
            else if (!WiktionarySurfaceTrees.TryStageWithCoord(
                         b, tag, WiktionaryDecomposer.Source, retainForReuse: true,
                         out tagId, out coord)) return false;
            coord.CopyTo(c);
            ids.Add(tagId);
            for (int i = 0; i < 4; i++) coords.Add(c[i]);
        }
        if (ids.Count == 0) return false;

        setId = b.StageCollection(
            CollectionsMarshal.AsSpan(ids), CollectionsMarshal.AsSpan(coords),
            CollectionTier, EntityTypeRegistry.Collection, WiktionaryDecomposer.Source);
        return true;
    }

    private static void WalkEtymology(
        SubstrateChangeBuilder b, Hash128 wordId, WiktionaryEntry e,
        IReadOnlyDictionary<string, Hash128>? roots)
    {
        if (Stage(b, e.EtymologyText, roots, out var etyId))
            Attest(b, wordId, "HAS_ETYMOLOGY", etyId, null);

        if (e.EtymologyTemplates is not { } templates) return;
        foreach (var t in templates)
        {
            if (t.Name is null || t.Args is not { } args) continue;
            if (!TryEtymologyRule(t.Name, out string etymType, out string[] termArgs)) continue;
            foreach (var arg in termArgs)
            {
                if (!args.TryGetValue(arg, out var term)) continue;
                if (string.IsNullOrEmpty(term) || term == "-") continue;
                if (Stage(b, term, roots, out var termId)) Attest(b, wordId, etymType, termId, null);
            }
        }
    }

    private static bool TryEtymologyRule(string name, out string etymType, out string[] termArgs)
    {
        switch (name)
        {
            case "bor": case "borrowed": etymType = "BORROWED_FROM"; termArgs = new[] { "3" }; return true;
            case "inh": case "inherited": etymType = "INHERITED_FROM"; termArgs = new[] { "3" }; return true;
            case "der": case "derived": etymType = "ETYMOLOGICALLY_DERIVED_FROM"; termArgs = new[] { "3" }; return true;
            case "cog": case "cognate": etymType = "ETYMOLOGICALLY_RELATED_TO"; termArgs = new[] { "2" }; return true;
            case "suffix": case "suf": etymType = "ETYMOLOGICALLY_DERIVED_FROM"; termArgs = new[] { "2" }; return true;
            case "prefix": case "pre": etymType = "ETYMOLOGICALLY_DERIVED_FROM"; termArgs = new[] { "3" }; return true;
            case "af":
            case "affix":
            case "com":
            case "compound":
            case "blend":
                etymType = "ETYMOLOGICALLY_DERIVED_FROM"; termArgs = new[] { "2", "3", "4" }; return true;
            case "doublet": case "dbt": etymType = "ETYMOLOGICALLY_RELATED_TO"; termArgs = new[] { "2" }; return true;
            case "back-form":
            case "back-formation":
            case "bf":
                etymType = "ETYMOLOGICALLY_DERIVED_FROM"; termArgs = new[] { "2" }; return true;
            default:
                etymType = string.Empty; termArgs = Array.Empty<string>(); return false;
        }
    }

    private static void RouteSynsetLinks(
        SubstrateChangeBuilder b, Hash128 senseId, WiktionaryEntry.Sense s, Hash128? langCtx)
    {
        if (s.LinkTargets is not { } links) return;
        var seen = new HashSet<Hash128>();
        foreach (var key in links)
            if (SourceEntityIdConventions.ResolveSynsetAnchor(key) is { } syn
                && syn != default && seen.Add(syn))
                AttestResolved(b, senseId, WiktionarySource.IsSenseOfTypeId, syn, langCtx);
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
            AttestResolved(b, senseId, WiktionarySource.CorrespondsToTypeId, itemId, langCtx);
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
    private const string TranslationRelation = "IS_TRANSLATION_OF";
    private const string DerivedRelation = "DERIVED_FROM";

    private static void Attest(
        SubstrateChangeBuilder b, Hash128 subject, string typeName, Hash128 objectId, Hash128? context) =>
        b.AddAttestation(NativeAttestation.Categorical(
            subject, typeName, objectId, WiktionaryDecomposer.Source, Trust, contextId: context));

    private static void AttestResolved(
        SubstrateChangeBuilder b, Hash128 subject, Hash128 typeId, Hash128 objectId, Hash128? context) =>
        b.AddAttestation(NativeAttestation.CategoricalResolved(
            subject, typeId, objectId, WiktionaryDecomposer.Source, context, Trust));
}
