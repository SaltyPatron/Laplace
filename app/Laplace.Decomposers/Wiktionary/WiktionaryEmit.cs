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
    public static void CollectSurfaces(WiktionaryEntry e, HashSet<string> into)
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
                            Add(into, tag);
            }

        if (e.Sounds is { } sounds)
            foreach (var snd in sounds)
            {
                Add(into, snd.Ipa);
                AddAll(into, snd.Tags);
            }

        CollectRelations(into, in e.Top);
        if (e.IncludeTranslations)
            AddAll(into, e.Translations);
        if (e.Forms is { } forms)
            foreach (var form in forms)
            {
                Add(into, form.FormText);
                AddAll(into, form.Tags);
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

    public static void Emit(WiktionaryEntry e, SubstrateChangeBuilder b) =>
        Emit(e, b, roots: null);

    /// <summary>
    /// When <paramref name="roots"/> is non-null, every surface was already emitted into the
    /// builder and this walk only looks up root ids + writes attestations/entities.
    /// </summary>
    public static void Emit(
        WiktionaryEntry e, SubstrateChangeBuilder b, IReadOnlyDictionary<string, Hash128>? roots)
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

        Hash128? posCtx = null;
        bool isVerb = false;
        if (e.Pos is { Length: > 0 } pos)
        {
            isVerb = pos.Equals("verb", StringComparison.OrdinalIgnoreCase);
            posCtx = PosReference.Attest(b, wordId, pos, PosReference.PosTagset.Wiktionary,
                WiktionaryDecomposer.Source, null, Trust, WiktionaryDecomposer.VocabularyNames);
        }

        if (e.Senses is { } senses)
            foreach (var s in senses)
            {
                WalkSense(b, wordId, s, posCtx, isVerb, langCtx, roots);
                RouteSynsetLinks(b, wordId, s, langCtx);
            }

        WalkSounds(b, wordId, e.Sounds, roots);
        WalkRelations(b, wordId, in e.Top, isVerb, context: langCtx, roots);
        if (e.IncludeTranslations && e.Translations is { } tr)
            foreach (var t in tr)
                if (Stage(b, t, roots, out var trId))
                    Attest(b, wordId, "IS_TRANSLATION_OF", trId, null);
        WalkForms(b, wordId, e.Forms, roots);
        WalkEtymology(b, wordId, e, roots);
    }

    private static void WalkSense(
        SubstrateChangeBuilder b, Hash128 wordId, WiktionaryEntry.Sense s, Hash128? posCtx, bool isVerb,
        Hash128? langCtx, IReadOnlyDictionary<string, Hash128>? roots)
    {
        if (s.Glosses is { } gl)
            foreach (var g in gl)
                if (Stage(b, g, roots, out var gId)) Attest(b, wordId, "HAS_DEFINITION", gId, posCtx);

        if (s.Examples is { } ex)
            foreach (var x in ex)
                if (Stage(b, x, roots, out var xId)) Attest(b, wordId, "HAS_EXAMPLE", xId, null);

        // Relation edges carry the LANGUAGE, not the POS: IS_SYNONYM_OF lives in the
        // HAS_SENSE family, and an unscoped edge there is a translation competing as
        // a sense (GH #867). POS remains attested on the word itself via HAS_POS.
        WalkRelations(b, wordId, in s.Relations, isVerb, langCtx, roots);

        if (s.Tags is { } tags)
            foreach (var tag in tags)
                if (RegisterTags.Contains(tag) && Stage(b, tag, roots, out var tagId))
                    Attest(b, wordId, "HAS_USAGE_REGISTER", tagId, posCtx);
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
                if (Stage(b, d, roots, out var dId)) Attest(b, dId, "DERIVED_FROM", wordId, context);
    }

    private static void EmitWords(
        SubstrateChangeBuilder b, Hash128 wordId, string type, List<string>? words, Hash128? context,
        IReadOnlyDictionary<string, Hash128>? roots)
    {
        if (words is null) return;
        foreach (var w in words)
            if (Stage(b, w, roots, out var id)) Attest(b, wordId, type, id, context);
    }

    private static void WalkSounds(
        SubstrateChangeBuilder b, Hash128 wordId, List<WiktionaryEntry.Sound>? sounds,
        IReadOnlyDictionary<string, Hash128>? roots)
    {
        if (sounds is null) return;
        foreach (var snd in sounds)
        {
            if (!Stage(b, snd.Ipa, roots, out var ipaId)) continue;
            Hash128? dialectCtx = null;
            if (snd.Tags is { } tags)
                foreach (var tag in tags)
                    if (Stage(b, tag, roots, out var dialectId)) { dialectCtx = dialectId; break; }
            Attest(b, wordId, "TRANSCRIBES_AS", ipaId, dialectCtx);
        }
    }

    private static void WalkForms(
        SubstrateChangeBuilder b, Hash128 wordId, List<WiktionaryEntry.Form>? forms,
        IReadOnlyDictionary<string, Hash128>? roots)
    {
        if (forms is null) return;
        foreach (var form in forms)
        {
            if (!Stage(b, form.FormText, roots, out var formId)) continue;
            Attest(b, formId, "FORM_OF", wordId, null);
            if (form.Tags is { } tags)
                foreach (var tag in tags)
                    if (Stage(b, tag, roots, out var tagId)) Attest(b, formId, "HAS_FEATURE", tagId, null);
        }
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
        SubstrateChangeBuilder b, Hash128 wordId, WiktionaryEntry.Sense s, Hash128? langCtx)
    {
        if (s.LinkTargets is { } links)
            foreach (var key in links)
            {
                if (SourceEntityIdConventions.ResolveSynsetAnchor(key) is { } syn && syn != default)
                    LinkSynset(b, wordId, syn, langCtx);
            }

        if (s.SynsetKey is { Length: > 0 } sk
            && SourceEntityIdConventions.ResolveSynsetAnchor(sk) is { } synId && synId != default)
            LinkSynset(b, wordId, synId, langCtx);
    }

    // Both edges on purpose. CORRESPONDS_TO is the cross-reference hub the CILI/
    // WordNet routing reads — but it is NOT in the HAS_SENSE family, so before this
    // change Wiktionary's word->synset converse.links(the GOOD sense evidence) were invisible
    // to lexical.senses()/bubble_up while its word->word converse.synonyms(translation-shaped) were
    // the only Wiktionary edges electing senses. Both edges carry language context
    // (OMW post-#867 / Copilot #891); POS stays on the word via HAS_POS.
    private static void LinkSynset(
        SubstrateChangeBuilder b, Hash128 wordId, Hash128 synId, Hash128? langCtx)
    {
        Attest(b, wordId, "CORRESPONDS_TO", synId, langCtx);
        Attest(b, wordId, "IS_SYNONYM_OF", synId, langCtx);
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

    private static void Attest(
        SubstrateChangeBuilder b, Hash128 subject, string typeName, Hash128 objectId, Hash128? context) =>
        b.AddAttestation(NativeAttestation.Categorical(
            subject, typeName, objectId, WiktionaryDecomposer.Source, Trust, contextId: context));
}
