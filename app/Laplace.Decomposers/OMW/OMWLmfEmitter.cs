using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.OMW;

internal static class OMWLmfEmitter
{
    private readonly record struct RelationBinding(OmwRelation Relation, bool Flip = false);

    private static readonly IReadOnlyDictionary<string, RelationBinding> RelationTypes =
        new Dictionary<string, RelationBinding>(StringComparer.Ordinal)
        {
            ["antonym"] = new(OmwRelation.IsAntonymOf),
            ["hypernym"] = new(OmwRelation.HasHypernym),
            ["instance_hypernym"] = new(OmwRelation.IsInstanceOf),
            ["hyponym"] = new(OmwRelation.HasHyponym),
            ["instance_hyponym"] = new(OmwRelation.HasInstance),
            ["holo_member"] = new(OmwRelation.IsMemberOf),
            ["holo_substance"] = new(OmwRelation.IsSubstanceOf),
            ["holo_part"] = new(OmwRelation.IsPartOf),
            ["mero_member"] = new(OmwRelation.HasMember),
            ["mero_substance"] = new(OmwRelation.HasSubstance),
            ["mero_part"] = new(OmwRelation.HasPart),
            ["attribute"] = new(OmwRelation.HasAttribute),
            ["derivation"] = new(OmwRelation.DerivationallyRelated),
            ["domain_topic"] = new(OmwRelation.HasDomainTopic),
            ["has_domain_topic"] = new(OmwRelation.IsDomainTopicMember),
            ["domain_region"] = new(OmwRelation.HasDomainRegion),
            ["has_domain_region"] = new(OmwRelation.IsDomainRegionMember),
            ["entails"] = new(OmwRelation.Entails),
            ["causes"] = new(OmwRelation.Causes),
            ["also"] = new(OmwRelation.AlsoSee),
            ["similar"] = new(OmwRelation.IsSimilarTo),
            ["participle"] = new(OmwRelation.IsParticipleOf),
            ["pertainym"] = new(OmwRelation.PertainsTo),
            // WN-LMF usage-domain relations correspond to Princeton -u / ;u.
            // Textual examples are emitted separately from Example elements.
            ["exemplifies"] = new(OmwRelation.IsDomainUsageMember),
            ["is_exemplified_by"] = new(OmwRelation.HasDomainUsage),
        };

    internal static bool SupportsRelation(string relationType) =>
        RelationTypes.ContainsKey(relationType);

    internal static void Emit(SubstrateChangeBuilder b, OmwLmfRecord record)
    {
        switch (record)
        {
            case OmwLmfLexicon lexicon:
                EmitLexicon(b, lexicon);
                break;
            case OmwLmfRequires requires:
                EmitRequires(b, requires);
                break;
            case OmwLmfLexicalEntry entry:
                EmitEntry(b, entry);
                break;
            case OmwLmfSynset synset:
                EmitSynset(b, synset);
                break;
            case OmwLmfSyntacticBehaviour behaviour:
                EmitBehaviour(b, behaviour);
                break;
            case OmwLmfSidecar sidecar:
                EmitSidecar(b, sidecar);
                break;
        }
    }

    internal static Hash128 Identity(string kind, string lexicon, string rawId) =>
        SubstrateCanonicalIds.OfVersioned("omw-lmf", kind, lexicon, rawId);

    private static Hash128 Declare(
        SubstrateChangeBuilder b, string kind, string lexicon, string rawId, Hash128 typeId)
    {
        Hash128 id = Identity(kind, lexicon, rawId);
        b.AddEntity(id, EntityTier.Word, typeId, OMWDecomposer.Source);
        CategoryAnchor.AttestCategory(b, id, typeId, OMWDecomposer.Source, TC.AcademicCurated);
        return id;
    }

    private static Hash128 Lexicon(SubstrateChangeBuilder b, string lexicon) =>
        Declare(b, "lexicon", lexicon, lexicon, OMWSource.LexiconTypeId);

    private static Hash128 Entry(SubstrateChangeBuilder b, string lexicon, string id) =>
        Declare(b, "entry", lexicon, id, OMWSource.LexicalEntryTypeId);

    private static Hash128 Sense(SubstrateChangeBuilder b, string lexicon, string id) =>
        Declare(b, "sense", lexicon, id, EntityTypeRegistry.WordNetSense);

    private static Hash128 Synset(SubstrateChangeBuilder b, string lexicon, string id) =>
        Declare(b, "synset", lexicon, id, EntityTypeRegistry.WordNetSynset);

    private static Hash128 Behaviour(SubstrateChangeBuilder b, string lexicon, string id) =>
        Declare(b, "syntactic-behaviour", lexicon, id, OMWSource.SyntacticBehaviourTypeId);

    private static Hash128 Package(SubstrateChangeBuilder b) =>
        Declare(b, "package", "omw", "2.0", OMWSource.PackageTypeId);

    private static void EmitLexicon(SubstrateChangeBuilder b, OmwLmfLexicon lexicon)
    {
        Hash128 id = Lexicon(b, lexicon.Id);
        AttestContent(b, id, OmwRelation.HasNameAlias, lexicon.Label);
        AttestContent(b, id, OmwRelation.HasVersion, lexicon.Version);
        AttestContent(b, id, OmwRelation.HasLicense, lexicon.License);
        AttestContent(b, id, OmwRelation.HasSourceUrl, lexicon.Url);
        AttestContent(b, id, OmwRelation.HasCitation, lexicon.Citation);
        AttestContent(b, id, OmwRelation.HasAttribution, lexicon.Email);
        AttestLanguage(b, id, lexicon.LanguageCode);
        Attest(b, Package(b), OmwRelation.Contains, id);
    }

    private static void EmitRequires(SubstrateChangeBuilder b, OmwLmfRequires requires)
    {
        if (string.IsNullOrWhiteSpace(requires.Reference)) return;
        Hash128 source = Lexicon(b, requires.Lexicon);
        Hash128 target = Lexicon(b, requires.Reference);
        Attest(b, source, OmwRelation.Requires, target);
        AttestContent(b, target, OmwRelation.HasVersion, requires.Version);
    }

    private static void EmitEntry(SubstrateChangeBuilder b, OmwLmfLexicalEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id)) return;
        Hash128 entryId = Entry(b, entry.Lexicon, entry.Id);
        Attest(b, Lexicon(b, entry.Lexicon), OmwRelation.Contains, entryId);

        Hash128? lemma = EmitContent(b, entry.Lemma);
        if (lemma is { } lemmaId)
        {
            Attest(b, entryId, OmwRelation.HasNameAlias, lemmaId, Language(entry.LanguageCode));
            AttestLanguage(b, lemmaId, entry.LanguageCode);
            if (!string.IsNullOrWhiteSpace(entry.PartOfSpeech))
                PosReference.Attest(
                    b, lemmaId, entry.PartOfSpeech, PosReference.PosTagset.WordNet,
                    OMWDecomposer.Source, Language(entry.LanguageCode), TC.AcademicCurated);
        }
        AttestContent(b, entryId, OmwRelation.HasNameAlias, entry.Index);
        AttestContent(b, entryId, OmwRelation.HasFeature, entry.LemmaType);
        AttestLanguage(b, entryId, entry.LanguageCode);
        if (!string.IsNullOrWhiteSpace(entry.PartOfSpeech))
            PosReference.Attest(
                b, entryId, entry.PartOfSpeech, PosReference.PosTagset.WordNet,
                OMWDecomposer.Source, Language(entry.LanguageCode), TC.AcademicCurated);

        foreach (OmwLmfForm form in entry.Forms)
        {
            if (EmitContent(b, form.WrittenForm) is not { } formId) continue;
            Attest(b, entryId, OmwRelation.Contains, formId, Language(entry.LanguageCode));
            if (lemma is { } baseId)
                Attest(b, formId, OmwRelation.FormOf, baseId, Language(entry.LanguageCode));
            AttestLanguage(b, formId, entry.LanguageCode);
            foreach (OmwLmfTag tag in form.Tags)
                AttestContent(b, formId, OmwRelation.HasFeature,
                    $"{tag.Category}={tag.Value}", entry.LanguageCode);
        }

        foreach (OmwLmfSense sense in entry.Senses)
        {
            if (string.IsNullOrWhiteSpace(sense.Id) || string.IsNullOrWhiteSpace(sense.Synset))
                continue;
            Hash128 senseId = Sense(b, entry.Lexicon, sense.Id);
            Hash128 synsetId = Synset(b, entry.Lexicon, sense.Synset);
            if (double.TryParse(
                    sense.Count, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double count)
                && count >= 0)
                Attest(b, entryId, OmwRelation.HasSense, senseId,
                    Language(entry.LanguageCode), count);
            else
                Attest(b, entryId, OmwRelation.HasSense, senseId, Language(entry.LanguageCode));
            Attest(b, senseId, OmwRelation.IsSenseOf, synsetId, Language(entry.LanguageCode));
            if (lemma is { } nameId)
                Attest(b, senseId, OmwRelation.HasNameAlias, nameId, Language(entry.LanguageCode));
            AttestLanguage(b, senseId, entry.LanguageCode);
            if (!string.IsNullOrWhiteSpace(entry.PartOfSpeech))
                PosReference.Attest(
                    b, senseId, entry.PartOfSpeech, PosReference.PosTagset.WordNet,
                    OMWDecomposer.Source, Language(entry.LanguageCode), TC.AcademicCurated);
            AttestContent(b, senseId, OmwRelation.HasProperty, sense.Number);
            AttestContent(b, senseId, OmwRelation.HasSenseFrequency, sense.Count);
            AttestContent(b, senseId, OmwRelation.HasFeature, sense.AdjectivePosition);
            AttestContent(b, senseId, OmwRelation.HasFeature, sense.Lexicalized);

            if (SenseAnchor.EmitExact(
                    b, sense.Identifier, OMWDecomposer.Source, TC.AcademicCurated) is { } exactId)
                Attest(b, senseId, OmwRelation.CorrespondsTo, exactId);

            foreach (string behaviourId in sense.Subcategorization.Split(
                         ' ', StringSplitOptions.RemoveEmptyEntries))
                Attest(b, senseId, OmwRelation.HasVerbFrame,
                    Behaviour(b, entry.Lexicon, behaviourId));

            EmitRelations(b, senseId, entry.Lexicon, sense.Relations, senseTargets: true);
        }
    }

    private static void EmitSynset(SubstrateChangeBuilder b, OmwLmfSynset synset)
    {
        if (string.IsNullOrWhiteSpace(synset.Id)) return;
        Hash128 synsetId = Synset(b, synset.Lexicon, synset.Id);
        AttestLanguage(b, synsetId, synset.LanguageCode);
        if (!string.IsNullOrWhiteSpace(synset.PartOfSpeech))
            PosReference.Attest(
                b, synsetId, synset.PartOfSpeech, PosReference.PosTagset.WordNet,
                OMWDecomposer.Source, Language(synset.LanguageCode), TC.AcademicCurated);
        AttestContent(b, synsetId, OmwRelation.HasLexCategory, synset.Lexfile);
        AttestContent(b, synsetId, OmwRelation.HasNameAlias, synset.Identifier);
        AttestContent(b, synsetId, OmwRelation.HasFeature, synset.Lexicalized);

        if (!string.IsNullOrWhiteSpace(synset.Ili))
        {
            Hash128 iliId = ReferenceAnchor.Id(ReferenceIdentityKind.CiliIli, synset.Ili)!.Value;
            b.AddEntity(iliId, EntityTier.Word, EntityTypeRegistry.WordNetSynset, OMWDecomposer.Source);
            Attest(b, synsetId, OmwRelation.CorrespondsTo, iliId);
        }

        foreach (string member in synset.Members)
            Attest(b, synsetId, OmwRelation.HasMember, Sense(b, synset.Lexicon, member),
                Language(synset.LanguageCode));
        foreach (string definition in synset.Definitions)
            AttestContent(b, synsetId, OmwRelation.HasDefinition, definition, synset.LanguageCode);
        foreach (string example in synset.Examples)
            AttestContent(b, synsetId, OmwRelation.HasExample, example, synset.LanguageCode);
        EmitRelations(b, synsetId, synset.Lexicon, synset.Relations, senseTargets: false);
    }

    private static void EmitBehaviour(SubstrateChangeBuilder b, OmwLmfSyntacticBehaviour behaviour)
    {
        if (string.IsNullOrWhiteSpace(behaviour.Id)) return;
        Hash128 id = Behaviour(b, behaviour.Lexicon, behaviour.Id);
        AttestContent(b, id, OmwRelation.HasNameAlias, behaviour.Frame, behaviour.LanguageCode);
    }

    private static void EmitSidecar(SubstrateChangeBuilder b, OmwLmfSidecar sidecar)
    {
        Hash128 subject = sidecar.Kind == OmwLmfSidecarKind.ReleaseIndex
            ? Package(b) : Lexicon(b, sidecar.Lexicon);
        OmwRelation relation = sidecar.Kind switch
        {
            OmwLmfSidecarKind.License => OmwRelation.HasLicense,
            OmwLmfSidecarKind.Citation => OmwRelation.HasCitation,
            _ => OmwRelation.HasProperty,
        };
        AttestContent(b, subject, relation, sidecar.Content,
            sidecar.Kind == OmwLmfSidecarKind.ReleaseIndex ? null : sidecar.LanguageCode);
    }

    private static void EmitRelations(
        SubstrateChangeBuilder b,
        Hash128 subject,
        string lexicon,
        IReadOnlyList<OmwLmfRelation> relations,
        bool senseTargets)
    {
        foreach (OmwLmfRelation relation in relations)
        {
            if (!RelationTypes.TryGetValue(relation.Type, out RelationBinding binding)
                || string.IsNullOrWhiteSpace(relation.Target))
                continue;
            Hash128 target = senseTargets
                ? Sense(b, lexicon, relation.Target)
                : Synset(b, lexicon, relation.Target);
            Attest(b,
                binding.Flip ? target : subject,
                binding.Relation,
                binding.Flip ? subject : target,
                magnitude: relation.Confidence ?? 1.0);
        }
    }

    private static void AttestLanguage(SubstrateChangeBuilder b, Hash128 subject, string language)
    {
        Hash128? languageId = Language(language);
        if (languageId is null) return;
        b.AddEntity(languageId.Value, EntityTier.Word, EntityTypeRegistry.Language, OMWDecomposer.Source);
        Attest(b, subject, OmwRelation.HasLanguage, languageId.Value);
    }

    private static Hash128? Language(string language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        OMWDecomposer.TrackLanguage(language);
        return LanguageReference.Resolve(language);
    }

    private static Hash128? EmitContent(SubstrateChangeBuilder b, string value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : ContentEmitter.Emit(b, value, OMWDecomposer.Source);

    private static void AttestContent(
        SubstrateChangeBuilder b,
        Hash128 subject,
        OmwRelation relation,
        string value,
        string? language = null)
    {
        if (EmitContent(b, value) is not { } contentId) return;
        Attest(b, subject, relation, contentId,
            language is null ? null : Language(language));
    }

    private static void Attest(
        SubstrateChangeBuilder b,
        Hash128 subject,
        OmwRelation relation,
        Hash128 obj,
        Hash128? context = null,
        double? magnitude = null)
    {
        var resolved = OMWSource.Resolve(relation);
        Hash128 typeId = resolved.Id;
        if (resolved.Flip) (subject, obj) = (obj, subject);
        b.AddAttestation(magnitude is { } value
            ? NativeAttestation.ResolvedScored(
                subject, typeId, obj, OMWDecomposer.Source, context,
                TC.AcademicCurated, value, arenaScale: 1.0)
            : NativeAttestation.CategoricalResolved(
                subject, typeId, obj, OMWDecomposer.Source, context, TC.AcademicCurated));
    }
}
