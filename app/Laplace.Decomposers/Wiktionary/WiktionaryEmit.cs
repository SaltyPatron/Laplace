using System.Text;
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

    public static void Emit(WiktionaryEntry e, SubstrateChangeBuilder b)
    {
        if (!Stage(b, e.Word, out Hash128 wordId)) return;

        if (e.LangCode is { Length: > 0 } lc)
        {
            Hash128 langEntity = LanguageReference.Resolve(lc);
            VocabularyNames.TrackLanguage(WiktionaryDecomposer.VocabularyNames, lc);
            b.AddEntity(new EntityRow(langEntity, EntityTier.Word, LanguageTypeId, WiktionaryDecomposer.Source));
            b.AddAttestation(NativeAttestation.Categorical(
                wordId, "HAS_LANGUAGE", langEntity, WiktionaryDecomposer.Source, Trust));
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
                WalkSense(b, wordId, s, posCtx, isVerb);
                RouteSynsetLinks(b, wordId, s, posCtx);
            }

        WalkSounds(b, wordId, e.Sounds);
        WalkRelations(b, wordId, in e.Top, isVerb, context: null);
        if (e.IncludeTranslations && e.Translations is { } tr)
            foreach (var t in tr)
                if (Stage(b, t, out var trId))
                    Attest(b, wordId, "IS_TRANSLATION_OF", trId, null);
        WalkForms(b, wordId, e.Forms);
        WalkEtymology(b, wordId, e);
    }

    private static void WalkSense(
        SubstrateChangeBuilder b, Hash128 wordId, WiktionaryEntry.Sense s, Hash128? posCtx, bool isVerb)
    {
        if (s.Glosses is { } gl)
            foreach (var g in gl)
                if (Stage(b, g, out var gId)) Attest(b, wordId, "HAS_DEFINITION", gId, posCtx);

        if (s.Examples is { } ex)
            foreach (var x in ex)
                if (Stage(b, x, out var xId)) Attest(b, wordId, "HAS_EXAMPLE", xId, null);

        WalkRelations(b, wordId, in s.Relations, isVerb, posCtx);

        if (s.Tags is { } tags)
            foreach (var tag in tags)
                if (RegisterTags.Contains(tag) && Stage(b, tag, out var tagId))
                    Attest(b, wordId, "HAS_USAGE_REGISTER", tagId, posCtx);
    }

    private static void WalkRelations(
        SubstrateChangeBuilder b, Hash128 wordId, in WiktionaryEntry.RelationBlock r, bool isVerb, Hash128? context)
    {
        EmitWords(b, wordId, "IS_SYNONYM_OF", r.Synonyms, context);
        EmitWords(b, wordId, "IS_ANTONYM_OF", r.Antonyms, context);
        EmitWords(b, wordId, "HAS_HYPONYM", r.Hyponyms, context);
        EmitWords(b, wordId, "HAS_PART", r.Meronyms, context);
        EmitWords(b, wordId, "IS_PART_OF", r.Holonyms, context);
        EmitWords(b, wordId, "RELATED_TO", r.Related, context);
        EmitWords(b, wordId, isVerb ? "MANNER_OF" : "HAS_HYPERNYM", r.Hypernyms, context);
        EmitWords(b, wordId, "IS_COORDINATE_TERM_WITH", r.Coordinate, context);

        // Derived reverses direction: derived-word DERIVED_FROM this word.
        if (r.Derived is { } derived)
            foreach (var d in derived)
                if (Stage(b, d, out var dId)) Attest(b, dId, "DERIVED_FROM", wordId, context);
    }

    private static void EmitWords(
        SubstrateChangeBuilder b, Hash128 wordId, string type, List<string>? words, Hash128? context)
    {
        if (words is null) return;
        foreach (var w in words)
            if (Stage(b, w, out var id)) Attest(b, wordId, type, id, context);
    }

    private static void WalkSounds(SubstrateChangeBuilder b, Hash128 wordId, List<WiktionaryEntry.Sound>? sounds)
    {
        if (sounds is null) return;
        foreach (var snd in sounds)
        {
            if (!Stage(b, snd.Ipa, out var ipaId)) continue;
            Hash128? dialectCtx = null;
            if (snd.Tags is { } tags)
                foreach (var tag in tags)
                    if (Stage(b, tag, out var dialectId)) { dialectCtx = dialectId; break; }
            Attest(b, wordId, "TRANSCRIBES_AS", ipaId, dialectCtx);
        }
    }

    private static void WalkForms(SubstrateChangeBuilder b, Hash128 wordId, List<WiktionaryEntry.Form>? forms)
    {
        if (forms is null) return;
        foreach (var form in forms)
        {
            if (!Stage(b, form.FormText, out var formId)) continue;
            Attest(b, formId, "FORM_OF", wordId, null);
            if (form.Tags is { } tags)
                foreach (var tag in tags)
                    if (Stage(b, tag, out var tagId)) Attest(b, formId, "HAS_FEATURE", tagId, null);
        }
    }

    private static void WalkEtymology(SubstrateChangeBuilder b, Hash128 wordId, WiktionaryEntry e)
    {
        if (Stage(b, e.EtymologyText, out var etyId))
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
                if (Stage(b, term, out var termId)) Attest(b, wordId, etymType, termId, null);
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
        SubstrateChangeBuilder b, Hash128 wordId, WiktionaryEntry.Sense s, Hash128? posCtx)
    {
        if (s.LinkTargets is { } links)
            foreach (var key in links)
            {
                if (SourceEntityIdConventions.ResolveSynsetAnchor(key) is { } syn && syn != default)
                    Attest(b, wordId, "CORRESPONDS_TO", syn, posCtx);
            }

        if (s.SynsetKey is { Length: > 0 } sk
            && SourceEntityIdConventions.ResolveSynsetAnchor(sk) is { } synId && synId != default)
            Attest(b, wordId, "CORRESPONDS_TO", synId, posCtx);
    }

    private static bool Stage(SubstrateChangeBuilder b, string? surface, out Hash128 id)
    {
        id = default;
        if (string.IsNullOrEmpty(surface)) return false;
        return ContentTierSpine.TryStageIntoBuilder(
            b, Encoding.UTF8.GetBytes(surface), WiktionaryDecomposer.Source, out id);
    }

    private static void Attest(
        SubstrateChangeBuilder b, Hash128 subject, string typeName, Hash128 objectId, Hash128? context) =>
        b.AddAttestation(NativeAttestation.Categorical(
            subject, typeName, objectId, WiktionaryDecomposer.Source, Trust, contextId: context));
}
