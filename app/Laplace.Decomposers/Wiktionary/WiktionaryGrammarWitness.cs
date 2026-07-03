using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Wiktionary;





internal sealed class WiktionaryGrammarWitness : IGrammarWitness
{
    private static readonly Hash128 LanguageTypeId = EntityTypeRegistry.Language;

    private static readonly Dictionary<string, string> RelMap = new(StringComparer.Ordinal)
    {
        ["synonyms"] = "IS_SYNONYM_OF",
        ["antonyms"] = "IS_ANTONYM_OF",

        ["hyponyms"] = "HAS_HYPONYM",
        ["meronyms"] = "HAS_PART",
        ["holonyms"] = "IS_PART_OF",

        ["related"] = "RELATED_TO",
    };

    private static readonly HashSet<string> RegisterTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "archaic", "obsolete", "dated", "slang", "colloquial", "informal", "formal",
        "vulgar", "offensive", "derogatory", "humorous", "euphemistic", "dialectal",
        "regional", "literary", "poetic", "technical", "rare", "nonstandard",
        "historical", "figurative",
    };

    private readonly DecomposerOptions _options;

    public WiktionaryGrammarWitness(DecomposerOptions options) => _options = options;

    public string ModalityId => "json";

    public void WalkRow(
        in GrammarComposeContext composed,
        in RowContext ctx,
        SubstrateChangeBuilder builder)
    {
        if (!JsonGrammarHelper.TryComposedProperty(composed, "word", out var wordId)) return;

        if (JsonGrammarHelper.TryPropertyUtf8(composed, "lang_code", out var langSpan)
            || JsonGrammarHelper.TryPropertyUtf8(composed, "lang", out langSpan))
        {
            string langCode = JsonGrammarHelper.Utf8ToString(langSpan);
            if (_options.Languages?.MatchesRaw(langCode) == false) return;

            Hash128 langEntity = LanguageReference.Resolve(langCode);
            VocabularyNames.TrackLanguage(WiktionaryDecomposer.VocabularyNames, langCode);
            builder.AddEntity(new EntityRow(langEntity, EntityTier.Word, LanguageTypeId, WiktionaryDecomposer.Source));
            builder.AddAttestation(NativeAttestation.Categorical(
                wordId, "HAS_LANGUAGE", langEntity, WiktionaryDecomposer.Source, TC.AcademicCuratedUserInput));
        }
        else if (_options.Languages?.IsActive == true)
            return;

        Hash128? posCtx = null;
        bool isVerb = false;
        if (JsonGrammarHelper.TryPropertyUtf8(composed, "pos", out var posSpan))
        {
            string pos = JsonGrammarHelper.Utf8ToString(posSpan);
            isVerb = pos.Equals("verb", StringComparison.OrdinalIgnoreCase);
            posCtx = PosReference.Attest(builder, wordId,
                pos, PosReference.PosTagset.Wiktionary,
                WiktionaryDecomposer.Source, null, TC.AcademicCuratedUserInput,
                WiktionaryDecomposer.VocabularyNames);
        }

        foreach (int senseObj in JsonGrammarHelper.ObjectNodesInArrayProperty(composed, "senses"))
            WalkSense(composed, builder, wordId, senseObj, posCtx, isVerb);

        WalkSounds(composed, builder, wordId);
        WalkRootRelations(composed, builder, wordId, isVerb);
        WalkForms(composed, builder, wordId);
        WalkEtymology(composed, builder, wordId);
    }

    private void WalkSense(
        in GrammarComposeContext composed,
        SubstrateChangeBuilder b,
        Hash128 wordId,
        int senseObj,
        Hash128? posCtx,
        bool isVerb)
    {
        foreach (int glossNode in JsonGrammarHelper.StringNodesInArrayOnObject(composed, senseObj, "glosses"))
            if (JsonGrammarHelper.TryComposedNode(composed, glossNode, out var glossId))
                Attest(b, wordId, "HAS_DEFINITION", glossId, posCtx);

        foreach (int exNode in JsonGrammarHelper.ChildNodesInObjectArray(composed, senseObj, "examples"))
        {
            if (composed.Ast.NodeTypeName(composed.Ast.GetNode(exNode).NodeTypeId) == "string")
            {
                if (JsonGrammarHelper.TryComposedNode(composed, exNode, out var exId))
                    Attest(b, wordId, "HAS_EXAMPLE", exId, null);
            }
            else if (JsonGrammarHelper.TryComposedPropertyOnObject(composed, exNode, "text", out var exId))
                Attest(b, wordId, "HAS_EXAMPLE", exId, null);
        }

        foreach (var (prop, typeName) in RelMap)
            foreach (int relObj in JsonGrammarHelper.ObjectNodesInArrayOnObject(composed, senseObj, prop))
                if (JsonGrammarHelper.TryComposedPropertyOnObject(composed, relObj, "word", out var relId))
                    Attest(b, wordId, typeName, relId, posCtx);



        string hyperType = isVerb ? "MANNER_OF" : "HAS_HYPERNYM";
        foreach (int relObj in JsonGrammarHelper.ObjectNodesInArrayOnObject(composed, senseObj, "hypernyms"))
            if (JsonGrammarHelper.TryComposedPropertyOnObject(composed, relObj, "word", out var relId))
                Attest(b, wordId, hyperType, relId, posCtx);


        foreach (int relObj in JsonGrammarHelper.ObjectNodesInArrayOnObject(composed, senseObj, "derived"))
            if (JsonGrammarHelper.TryComposedPropertyOnObject(composed, relObj, "word", out var relId))
                Attest(b, relId, "DERIVED_FROM", wordId, posCtx);

        foreach (int coordObj in JsonGrammarHelper.ObjectNodesInArrayOnObject(composed, senseObj, "coordinate_terms"))
            if (JsonGrammarHelper.TryComposedPropertyOnObject(composed, coordObj, "word", out var coordId))
                Attest(b, wordId, "IS_COORDINATE_TERM_WITH", coordId, posCtx);




        foreach (int tagNode in JsonGrammarHelper.StringNodesInArrayOnObject(composed, senseObj, "tags"))
        {
            if (!JsonGrammarHelper.TryComposedNode(composed, tagNode, out var tagId)) continue;
            var nd = composed.Ast.GetNode(tagNode);
            string tag = JsonGrammarHelper.Utf8ToString(
                composed.Utf8.AsSpan((int)nd.StartByte, (int)(nd.EndByte - nd.StartByte)));
            if (RegisterTags.Contains(tag))
                Attest(b, wordId, "HAS_USAGE_REGISTER", tagId, posCtx);
        }
    }

    private static void WalkSounds(in GrammarComposeContext composed, SubstrateChangeBuilder b, Hash128 wordId)
    {
        foreach (int sndObj in JsonGrammarHelper.ObjectNodesInArrayProperty(composed, "sounds"))
        {
            if (!JsonGrammarHelper.TryComposedPropertyOnObject(composed, sndObj, "ipa", out var ipaId)) continue;
            Hash128? dialectCtx = null;
            foreach (int tagNode in JsonGrammarHelper.StringNodesInArrayOnObject(composed, sndObj, "tags"))
            {
                if (!JsonGrammarHelper.TryComposedNode(composed, tagNode, out var dialectId)) continue;
                dialectCtx = dialectId;
                break;
            }
            Attest(b, wordId, "TRANSCRIBES_AS", ipaId, dialectCtx);
        }
    }

    private void WalkRootRelations(in GrammarComposeContext composed, SubstrateChangeBuilder b, Hash128 wordId, bool isVerb)
    {
        if (_options.EmitCrossLanguageLinks)
        {
            foreach (int trObj in JsonGrammarHelper.ObjectNodesInArrayProperty(composed, "translations"))
                if (JsonGrammarHelper.TryComposedPropertyOnObject(composed, trObj, "word", out var trId))
                    Attest(b, wordId, "IS_TRANSLATION_OF", trId, null);
        }

        foreach (var (prop, typeName) in RelMap)
            foreach (int relObj in JsonGrammarHelper.ObjectNodesInArrayProperty(composed, prop))
                if (JsonGrammarHelper.TryComposedPropertyOnObject(composed, relObj, "word", out var relId))
                    Attest(b, wordId, typeName, relId, null);

        string hyperType = isVerb ? "MANNER_OF" : "HAS_HYPERNYM";
        foreach (int relObj in JsonGrammarHelper.ObjectNodesInArrayProperty(composed, "hypernyms"))
            if (JsonGrammarHelper.TryComposedPropertyOnObject(composed, relObj, "word", out var relId))
                Attest(b, wordId, hyperType, relId, null);

        foreach (int relObj in JsonGrammarHelper.ObjectNodesInArrayProperty(composed, "derived"))
            if (JsonGrammarHelper.TryComposedPropertyOnObject(composed, relObj, "word", out var relId))
                Attest(b, relId, "DERIVED_FROM", wordId, null);

        foreach (int coordObj in JsonGrammarHelper.ObjectNodesInArrayProperty(composed, "coordinate_terms"))
            if (JsonGrammarHelper.TryComposedPropertyOnObject(composed, coordObj, "word", out var coordId))
                Attest(b, wordId, "IS_COORDINATE_TERM_WITH", coordId, null);
    }

    private static void WalkForms(in GrammarComposeContext composed, SubstrateChangeBuilder b, Hash128 wordId)
    {


        foreach (int formObj in JsonGrammarHelper.ObjectNodesInArrayProperty(composed, "forms"))
        {
            if (!JsonGrammarHelper.TryComposedPropertyOnObject(composed, formObj, "form", out var formId)) continue;
            Attest(b, formId, "FORM_OF", wordId, null);
            foreach (int tagNode in JsonGrammarHelper.StringNodesInArrayOnObject(composed, formObj, "tags"))
                if (JsonGrammarHelper.TryComposedNode(composed, tagNode, out var tagId))
                    Attest(b, formId, "HAS_FEATURE", tagId, null);
        }
    }

    private static void WalkEtymology(in GrammarComposeContext composed, SubstrateChangeBuilder b, Hash128 wordId)
    {
        if (JsonGrammarHelper.TryComposedProperty(composed, "etymology_text", out var etyId))
            Attest(b, wordId, "HAS_ETYMOLOGY", etyId, null);

        foreach (int etObj in JsonGrammarHelper.ObjectNodesInArrayProperty(composed, "etymology_templates"))
        {
            if (!JsonGrammarHelper.TryPropertyUtf8OnObject(composed, etObj, "name", out var nameSpan)) continue;
            string name = JsonGrammarHelper.Utf8ToString(nameSpan);









            string etymType;
            string[] termArgs;
            switch (name)
            {
                case "bor": case "borrowed": etymType = "BORROWED_FROM"; termArgs = new[] { "3" }; break;
                case "inh": case "inherited": etymType = "INHERITED_FROM"; termArgs = new[] { "3" }; break;
                case "der": case "derived": etymType = "ETYMOLOGICALLY_DERIVED_FROM"; termArgs = new[] { "3" }; break;
                case "cog": case "cognate": etymType = "ETYMOLOGICALLY_RELATED_TO"; termArgs = new[] { "2" }; break;
                case "suffix": case "suf": etymType = "ETYMOLOGICALLY_DERIVED_FROM"; termArgs = new[] { "2" }; break;
                case "prefix": case "pre": etymType = "ETYMOLOGICALLY_DERIVED_FROM"; termArgs = new[] { "3" }; break;
                case "af":
                case "affix":
                case "com":
                case "compound":
                case "blend":
                    etymType = "ETYMOLOGICALLY_DERIVED_FROM"; termArgs = new[] { "2", "3", "4" }; break;
                case "doublet": case "dbt": etymType = "ETYMOLOGICALLY_RELATED_TO"; termArgs = new[] { "2" }; break;
                case "back-form":
                case "back-formation":
                case "bf":
                    etymType = "ETYMOLOGICALLY_DERIVED_FROM"; termArgs = new[] { "2" }; break;
                default: continue;
            }

            int argsObj = JsonGrammarHelper.FindNestedObject(composed, etObj, "args");
            if (argsObj < 0) continue;
            foreach (var termArg in termArgs)
            {
                if (!JsonGrammarHelper.TryComposedPropertyOnObject(composed, argsObj, termArg, out var termId)) continue;
                if (JsonGrammarHelper.IsEmptyOrDashPlaceholder(composed, argsObj, termArg)) continue;
                Attest(b, wordId, etymType, termId, null);
            }
        }
    }

    private static void Attest(
        SubstrateChangeBuilder b, Hash128 subject, string typeName, Hash128 objectId, Hash128? context) =>
        b.AddAttestation(NativeAttestation.Categorical(
            subject, typeName, objectId, WiktionaryDecomposer.Source, TC.AcademicCuratedUserInput,
            contextId: context));
}
