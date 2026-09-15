using System.Collections.Concurrent;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Tests;
using Laplace.Decomposers.UD;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.UD.Tests;

public sealed class UdSentenceEmitterTests
{
    private static readonly Hash128 UdSource = UDDecomposer.Source;

    static UdSentenceEmitterTests()
    {
        if (!CodepointPerfcache.IsLoaded) CodepointPerfcache.Load(TestInstall.ResolvePerfcacheOrThrow());
        LanguageReference.EnsureLoaded(TestIngestPaths.Iso639);
    }

    [Fact]
    public void SentenceEmitsOneParseAndOneLanguageClaim_NotTokenGlobalClaims()
    {
        byte[] text = Utf8("The cat sat.");
        var sentence = Sentence(text,
            Token(1, "The", "the", "DET", "DT", ["PronType=Art"], 2, "det"),
            Token(2, "cat", "cat", "NOUN", "NN", ["Number=Sing"], 3, "nsubj"),
            Token(3, "sat", "sit", "VERB", "VBD", ["Tense=Past"], 0, "root"));

        (SubstrateChange change, Hash128 parseId, UdParseStructure.DecodedParse parse) =
            EmitAndDecode(sentence);

        Hash128 hasParse = RelationTypeRegistry.Resolve("HAS_PARSE").Id;
        Hash128 hasLanguage = RelationTypeRegistry.Resolve("HAS_LANGUAGE").Id;
        AttestationRow parseClaim = Assert.Single(change.Attestations.Where(a => a.TypeId == hasParse));
        AttestationRow languageClaim = Assert.Single(change.Attestations.Where(a => a.TypeId == hasLanguage));

        Assert.Equal(ContentTierSpine.ResolveRoot(text), parseClaim.SubjectId);
        Assert.Equal(parseId, parseClaim.ObjectId);
        Assert.NotNull(parseClaim.ContextId);
        Assert.Equal(ContentTierSpine.ResolveRoot(text), languageClaim.SubjectId);
        Assert.Equal(LanguageReference.Resolve("en"), languageClaim.ObjectId);
        Assert.Equal(3, parse.Tokens.Count);

        string[] removedOccurrenceProjections =
        [
            "HAS_POS", "HAS_XPOS", "IS_LEMMA_OF", "HAS_PART",
            "HAS_DEFINITION", "TRANSCRIBES_AS",
        ];
        foreach (string relation in removedOccurrenceProjections)
        {
            Hash128 relationId = RelationTypeRegistry.Resolve(relation).Id;
            Assert.DoesNotContain(change.Attestations, a => a.TypeId == relationId);
        }
        Assert.DoesNotContain(change.Attestations, a =>
            a.TypeId == RelationTypeRegistry.ResolveDeprel("det").Id
            || a.TypeId == RelationTypeRegistry.ResolveDeprel("nsubj").Id
            || a.TypeId == RelationTypeRegistry.ResolveDeprel("root").Id
            || a.TypeId == RelationTypeRegistry.ResolveFeature("Number").Id);
    }

    [Fact]
    public void RepeatedIdenticalFormsPreserveDistinctOrdinalsAndHeads()
    {
        byte[] had = Utf8("had");
        var sentence = Sentence(Utf8("had had had"),
            Token(1, had, Utf8("have"), "AUX", "VBD", [], 0, "root"),
            Token(2, had, Utf8("have"), "AUX", "VBN", [], 1, "dep"),
            Token(3, had, Utf8("have"), "AUX", "VBN", [], 2, "dep"));

        var parse = EmitAndDecode(sentence).Parse;

        Assert.Equal(3, parse.Tokens.Count);
        Assert.Single(parse.Tokens.Select(t => t.FormId).Distinct());
        Assert.Equal(3, parse.Tokens.Select(t => t.RefId).Distinct().Count());
        Assert.Equal(UdParseStructure.TokenRefId("1"), parse.Tokens[0].RefId);
        Assert.Equal(UdParseStructure.RootId, parse.Tokens[0].HeadRefId);
        Assert.Equal(UdParseStructure.TokenRefId("1"), parse.Tokens[1].HeadRefId);
        Assert.Equal(UdParseStructure.TokenRefId("2"), parse.Tokens[2].HeadRefId);
    }

    [Fact]
    public void MissingHeadIsNotSilentlyRewrittenAsRoot()
    {
        UdToken token = Token(1, "orphan", "orphan", "NOUN", "NN", [], 0, "_", headSpecified: false);
        var parse = EmitAndDecode(Sentence(Utf8("orphan"), token)).Parse;

        Assert.Equal(UdParseStructure.NoneId, Assert.Single(parse.Tokens).HeadRefId);
    }

    [Fact]
    public void FeatureSetIdentityIsIndependentOfInputOrderAndDuplicates()
    {
        byte[] text = Utf8("cats");
        UdToken ordered = Token(1, "cats", "cat", "NOUN", "NNS",
            ["Case=Nom", "Number=Plur", "Gender=Fem"], 0, "root");
        UdToken shuffled = Token(1, "cats", "cat", "NOUN", "NNS",
            ["Gender=Fem", "Number=Plur", "Case=Nom", "Number=Plur"], 0, "root");

        var a = EmitAndDecode(Sentence(text, ordered));
        var b = EmitAndDecode(Sentence(text, shuffled));

        Assert.Equal(a.ParseId, b.ParseId);
        Assert.Equal(3, Assert.Single(a.Parse.Tokens).Features.Count);
        Assert.Equal(
            Assert.Single(a.Parse.Tokens).Features,
            Assert.Single(b.Parse.Tokens).Features);
    }

    [Fact]
    public void EnhancedDependencySubtypeIsPreserved()
    {
        UdToken token = Token(1, "seen", "see", "VERB", "VBN", [], 0, "root",
            deps: "0:root|2:nsubj:pass");
        var parse = EmitAndDecode(Sentence(Utf8("seen"), token)).Parse;
        var enhanced = Assert.Single(parse.Tokens).Enhanced;

        Assert.Contains(enhanced, edge =>
            edge.HeadRefId == UdParseStructure.TokenRefId("2")
            && edge.RelationId == RelationTypeRegistry.ResolveEnhancedDeprel("nsubj:pass").Id);
        Assert.DoesNotContain(enhanced, edge =>
            edge.HeadRefId == UdParseStructure.TokenRefId("2")
            && edge.RelationId == RelationTypeRegistry.ResolveEnhancedDeprel("nsubj").Id);
    }

    [Fact]
    public void XposIdentityIsLanguageBound()
    {
        Hash128 english = UdParseStructure.XposId("en", "NN");
        Hash128 german = UdParseStructure.XposId("de", "NN");

        Assert.NotEqual(english, german);
        Assert.Equal(english, Assert.Single(EmitAndDecode(
            Sentence(Utf8("cat"), Token(1, "cat", "cat", "NOUN", "NN", [], 0, "root"))).Parse.Tokens).XposId);
        Assert.Equal(german, Assert.Single(EmitAndDecode(
            Sentence(Utf8("Katze"), Token(1, "Katze", "Katze", "NOUN", "NN", [], 0, "root")),
            "de").Parse.Tokens).XposId);
    }

    [Fact]
    public void MiscAndMultiwordRangesRemainInsideTheParseStructure()
    {
        var sentence = new UdSentence(
            Utf8("du monde"),
            [
                Token(1, "de", "de", "ADP", "P", [], 2, "case", misc: "Lang=fr|Gloss=of"),
                Token(2, "le", "le", "DET", "D", [], 0, "root", misc: "SpaceAfter=No"),
            ],
            [new UdMwt(1, 2, Utf8("du"), "SpaceAfter=No|Typo")],
            2,
            "fr-fixture-1",
            1);

        (SubstrateChange change, _, UdParseStructure.DecodedParse parse) = EmitAndDecode(sentence);
        var first = parse.Tokens[0];

        Assert.Contains(first.Misc, item =>
            item.KeyId == UdParseStructure.MiscKeyId("Lang")
            && item.ValueId == LanguageReference.Resolve("fr"));
        Assert.Contains(first.Misc, item =>
            item.KeyId == UdParseStructure.MiscKeyId("Gloss")
            && item.ValueId == ContentTierSpine.ResolveRoot(Utf8("of")));
        Assert.Contains(parse.Tokens[1].Misc, item =>
            item.KeyId == UdParseStructure.MiscKeyId("SpaceAfter")
            && item.ValueId == ContentTierSpine.ResolveRoot(Utf8("No")));

        var mwt = Assert.Single(parse.Mwts);
        Assert.Equal(UdParseStructure.TokenRefId("1"), mwt.StartRefId);
        Assert.Equal(UdParseStructure.TokenRefId("2"), mwt.EndRefId);
        Assert.Equal(ContentTierSpine.ResolveRoot(Utf8("du")), mwt.FormId);
        Assert.Contains(mwt.Misc, item =>
            item.KeyId == UdParseStructure.MiscKeyId("SpaceAfter")
            && item.ValueId == ContentTierSpine.ResolveRoot(Utf8("No")));
        Assert.Contains(mwt.Misc, item =>
            item.KeyId == UdParseStructure.MiscKeyId("Typo")
            && item.ValueId == UdParseStructure.PresentId);

        Hash128 hasLanguage = RelationTypeRegistry.Resolve("HAS_LANGUAGE").Id;
        Assert.Single(change.Attestations.Where(a => a.TypeId == hasLanguage));
    }

    [Fact]
    public async Task ParserPreservesSourceIdentityAndMissingHeadState()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path,
                "# sent_id = repeated-1\n# text = had had\n"
                + "1\thad\thave\tAUX\tVBD\t_\t0\troot\t0:root\t_\n"
                + "2\thad\thave\tAUX\tVBN\t_\t_\t_\t_\t_\n\n");

            var sentences = new List<UdSentence>();
            await foreach (UdSentence sentence in UdConlluParser.ParseSentencesAsync(path))
                sentences.Add(sentence);

            UdSentence parsed = Assert.Single(sentences);
            Assert.Equal("repeated-1", parsed.SourceSentenceId);
            Assert.Equal(1, parsed.SourceOrdinal);
            Assert.True(parsed.Tokens[0].HeadSpecified);
            Assert.False(parsed.Tokens[1].HeadSpecified);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FileLabelIncludesTreebankDirectory_NotOnlyTheRepeatedSplitName()
    {
        string a = UdIngestSupport.FileLabel(
            Path.Combine("root", "UD_English-EWT", "en_train.conllu"));
        string b = UdIngestSupport.FileLabel(
            Path.Combine("root", "UD_English-GUM", "en_train.conllu"));

        Assert.Equal("ud/UD_English-EWT/en_train", a);
        Assert.Equal("ud/UD_English-GUM/en_train", b);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ExplicitWitnessChangesProvenanceWithoutChangingParseStructure()
    {
        var sentence = Sentence(Utf8("cats"),
            Token(1, "cats", "cat", "NOUN", "NNS", ["Number=Plur"], 0, "root"));
        Hash128 source = Hash128.OfCanonical("test/authored-ud/source/λ");
        Hash128 otherSource = Hash128.OfCanonical("test/authored-ud/source/棋");
        Hash128 file = Hash128.OfCanonical("test/authored-ud/file/one");
        Hash128 otherFile = Hash128.OfCanonical("test/authored-ud/file/two");
        var contract = new UdWitnessContract(source, SourceTrust.SubstrateMandate, file);
        var original = EmitAndDecode(sentence, witness: contract);
        var changedSource = EmitAndDecode(sentence, witness: contract with { SourceId = otherSource });
        var changedContext = EmitAndDecode(sentence, witness: contract with { SourceFileContext = otherFile });
        var changedTrust = EmitAndDecode(sentence, witness: contract with { Trust = 0.37 });
        var ordinary = EmitAndDecode(sentence);

        PhysicalityRow originalStructure = ParsePhysicality(original.Change, original.ParseId);
        foreach (var emitted in new[] { changedSource, changedContext, changedTrust, ordinary })
        {
            Assert.Equal(original.ParseId, emitted.ParseId);
            PhysicalityRow structure = ParsePhysicality(emitted.Change, emitted.ParseId);
            Assert.Equal(originalStructure.Id, structure.Id);
            Assert.Equal(originalStructure.TrajectoryXyzm, structure.TrajectoryXyzm);
        }

        AttestationRow originalClaim = ParseClaim(original.Change);
        Assert.NotEqual(originalClaim.ContextId, ParseClaim(changedSource.Change).ContextId);
        Assert.NotEqual(originalClaim.ContextId, ParseClaim(changedContext.Change).ContextId);
        AttestationRow changedTrustClaim = ParseClaim(changedTrust.Change);
        Assert.Equal(originalClaim.ContextId, changedTrustClaim.ContextId);
        Assert.Equal(originalClaim.Id, changedTrustClaim.Id);
        Assert.NotEqual(originalClaim.OpponentRdFp1e9, changedTrustClaim.OpponentRdFp1e9);
        AssertWitness(changedTrustClaim, source, changedTrustClaim.ContextId, 0.37);
    }

    [Fact]
    public void AuthoredWitnessSourceTrustAndContextReachEveryParseDeclaration()
    {
        var sentence = Sentence(Utf8("cats"),
            Token(1, "cats", "cat", "NOUN", "NNS", ["Number=Plur"], 0, "root"));
        var witness = new UdWitnessContract(
            Hash128.OfCanonical("test/authored-ud/provenance"), SourceTrust.SubstrateMandate,
            Hash128.OfCanonical("test/authored-ud/contract-file"));
        var emitted = EmitAndDecode(sentence, witness: witness);
        AttestationRow parseClaim = ParseClaim(emitted.Change);
        Assert.NotNull(parseClaim.ContextId);
        Assert.Equal(emitted.ParseId, parseClaim.ObjectId);
        Assert.All(emitted.Change.Entities, entity => Assert.Equal(witness.SourceId, entity.FirstObservedBy));
        Assert.All(emitted.Change.Attestations, row => AssertWitness(
            row, witness.SourceId,
            row.TypeId == parseClaim.TypeId ? parseClaim.ContextId : witness.SourceFileContext,
            witness.Trust));

        AttestationRow language = Assert.Single(emitted.Change.Attestations.Where(a =>
            a.TypeId == RelationTypeRegistry.Resolve("HAS_LANGUAGE").Id));
        Assert.Equal(ContentTierSpine.ResolveRoot(sentence.TextUtf8), language.SubjectId);
        Assert.Equal(LanguageReference.Resolve("en"), language.ObjectId);
        UdParseStructure.DecodedToken token = Assert.Single(emitted.Parse.Tokens);
        Hash128 isA = RelationTypeRegistry.Resolve("IS_A").Id;
        Assert.Contains(emitted.Change.Attestations, row =>
            row.SubjectId == token.XposId && row.TypeId == isA && row.ObjectId == token.UposId);
        var feature = RelationTypeRegistry.ResolveFeature("Number");
        Assert.NotNull(feature.ParentId);
        Assert.Contains(emitted.Change.Attestations, row =>
            row.SubjectId == feature.Id && row.TypeId == isA && row.ObjectId == feature.ParentId);
    }

    [Fact]
    public void FileContextsKeepSeparateOccurrencesAndSourceDeclarations()
    {
        var sentence = Sentence(Utf8("cats"),
            Token(1, "cats", "cat", "NOUN", "NNS", ["Number=Plur"], 0, "root"));
        var first = new UdWitnessContract(
            Hash128.OfCanonical("test/authored-ud/shared-source"), SourceTrust.SubstrateMandate,
            Hash128.OfCanonical("test/authored-ud/file/first"));
        var second = first with { SourceFileContext = Hash128.OfCanonical("test/authored-ud/file/second") };
        var sharedDeclarations = new ConcurrentIdSet();
        var a = EmitAndDecode(sentence, witness: first, seenSourceDeclarations: sharedDeclarations);
        var b = EmitAndDecode(sentence, witness: second, seenSourceDeclarations: sharedDeclarations);
        var replay = EmitAndDecode(sentence, witness: first);
        Assert.Equal(a.ParseId, b.ParseId);
        Assert.NotEqual(ParseClaim(a.Change).ContextId, ParseClaim(b.Change).ContextId);
        Assert.Equal(ParseClaim(a.Change).ContextId, ParseClaim(replay.Change).ContextId);

        Hash128 contains = RelationTypeRegistry.Resolve("CONTAINS").Id;
        Hash128 isA = RelationTypeRegistry.Resolve("IS_A").Id;
        Hash128 feature = RelationTypeRegistry.ResolveFeature("Number").Id;
        foreach (var (emitted, witness) in new[] { (a, first), (b, second) })
        {
            Hash128 occurrence = ParseClaim(emitted.Change).ContextId!.Value;
            Assert.Contains(emitted.Change.Entities, entity =>
                entity.Id == occurrence && entity.TypeId == EntityTypeRegistry.UdParseOccurrence);
            AttestationRow link = Assert.Single(emitted.Change.Attestations.Where(row => row.TypeId == contains));
            Assert.Equal(witness.SourceFileContext, link.SubjectId);
            Assert.Equal(occurrence, link.ObjectId);
            AssertWitness(link, witness.SourceId, witness.SourceFileContext, witness.Trust);
            Assert.Contains(emitted.Change.Attestations, row => row.SubjectId == feature
                && row.TypeId == isA && row.ContextId == witness.SourceFileContext);
            Assert.Contains(emitted.Change.Attestations, row =>
                row.SubjectId == Assert.Single(emitted.Parse.Tokens).XposId
                && row.TypeId == isA && row.ContextId == witness.SourceFileContext);
        }
    }

    [Fact]
    public void DefaultWitnessRetainsOrdinaryUdSourceTrustAndOccurrenceContract()
    {
        var sentence = Sentence(Utf8("cats"),
            Token(1, "cats", "cat", "NOUN", "NNS", ["Number=Plur"], 0, "root"));
        var implicitDefault = EmitAndDecode(sentence);
        var explicitDefault = EmitAndDecode(sentence,
            witness: new UdWitnessContract(UdSource, SourceTrust.AcademicCurated));
        Assert.Equal(implicitDefault.ParseId, explicitDefault.ParseId);
        Assert.Equal(implicitDefault.Change.Attestations, explicitDefault.Change.Attestations);
        AttestationRow parseClaim = ParseClaim(implicitDefault.Change);
        Assert.NotNull(parseClaim.ContextId);
        Assert.All(implicitDefault.Change.Attestations, row => AssertWitness(
            row, UdSource, row.TypeId == parseClaim.TypeId ? parseClaim.ContextId : null,
            SourceTrust.AcademicCurated));
        Assert.DoesNotContain(implicitDefault.Change.Attestations,
            row => row.TypeId == RelationTypeRegistry.Resolve("CONTAINS").Id);
    }

    private static AttestationRow ParseClaim(SubstrateChange change) =>
        Assert.Single(change.Attestations.Where(a => a.TypeId == RelationTypeRegistry.Resolve("HAS_PARSE").Id));

    private static PhysicalityRow ParsePhysicality(SubstrateChange change, Hash128 parseId) =>
        Assert.Single(change.Physicalities.Where(p => p.EntityId == parseId
            && p.Type == PhysicalityType.ParseStructure));

    private static void AssertWitness(AttestationRow actual, Hash128 source, Hash128? context, double trust)
    {
        AttestationRow expected = NativeAttestation.CategoricalResolved(
            actual.SubjectId, actual.TypeId, actual.ObjectId, source, context, trust);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(source, actual.SourceId);
        Assert.Equal(context, actual.ContextId);
        Assert.Equal(expected.Outcome, actual.Outcome);
        Assert.Equal(expected.ObservationCount, actual.ObservationCount);
        Assert.Equal(expected.ScoreFp1e9, actual.ScoreFp1e9);
        Assert.Equal(expected.SumScoreFp1e9, actual.SumScoreFp1e9);
        Assert.Equal(expected.OpponentRdFp1e9, actual.OpponentRdFp1e9);
        Assert.Equal(expected.OpponentRatingFp1e9, actual.OpponentRatingFp1e9);
    }

    private static UdSentence Sentence(byte[] text, params UdToken[] tokens) =>
        new(text, tokens, [], tokens.Where(t => t.Id > 0).Select(t => t.Id).DefaultIfEmpty().Max(),
            "fixture-1", 1);

    private static UdToken Token(
        int id,
        string form,
        string lemma,
        string upos,
        string xpos,
        string[] feats,
        int head,
        string deprel,
        string deps = "_",
        string misc = "_",
        bool headSpecified = true) =>
        Token(id, Utf8(form), Utf8(lemma), upos, xpos, feats, head, deprel, deps, misc, headSpecified);

    private static UdToken Token(
        int id,
        byte[] form,
        byte[] lemma,
        string upos,
        string xpos,
        string[] feats,
        int head,
        string deprel,
        string deps = "_",
        string misc = "_",
        bool headSpecified = true) =>
        new(id, id.ToString(), form, lemma, form.AsSpan().SequenceEqual(lemma),
            upos, xpos, feats, head, deprel, deps, misc, headSpecified);

    private static (SubstrateChange Change, Hash128 ParseId, UdParseStructure.DecodedParse Parse)
        EmitAndDecode(UdSentence sentence, string langCode = "en", UdWitnessContract? witness = null,
            ConcurrentIdSet? seenSourceDeclarations = null)
    {
        Hash128 langId = LanguageReference.Resolve(langCode);
        Hash128 source = witness?.SourceId ?? UdSource;
        var builder = new SubstrateChangeBuilder(
            source, "ud/emitter-test", null,
            entityCapacity: 256, physicalityCapacity: 256, attestationCapacity: 256);
        UdSentenceEmitContext context = BuildEmitContext(sentence);
        if (witness is { } contract)
            UdSentenceEmitContext.EmitWitness(
                builder, sentence, langId, langCode, "ud/test.conllu",
                new HashSet<Hash128>(), seenSourceDeclarations ?? new ConcurrentIdSet(),
                new ConcurrentDictionary<string, byte>(), context, contract.SourceId,
                contract.Trust, contract.SourceFileContext);
        else
            UdSentenceEmitContext.EmitWitness(
                builder, sentence, langId, langCode, "ud/test.conllu",
                new HashSet<Hash128>(), seenSourceDeclarations ?? new ConcurrentIdSet(),
                new ConcurrentDictionary<string, byte>(), context, UdSource);
        SubstrateChange change = builder.Build();

        Hash128 hasParse = RelationTypeRegistry.Resolve("HAS_PARSE").Id;
        Hash128 parseId = Assert.Single(change.Attestations.Where(a => a.TypeId == hasParse)).ObjectId!.Value;
        PhysicalityRow physicality = Assert.Single(change.Physicalities.Where(p =>
            p.EntityId == parseId && p.Type == PhysicalityType.ParseStructure));
        Assert.NotNull(physicality.TrajectoryXyzm);
        Hash128[] constituents = Trajectory.Constituents(physicality.TrajectoryXyzm!);
        Assert.True(UdParseStructure.TryDecode(constituents, out var decoded));
        return (change, parseId, Assert.IsType<UdParseStructure.DecodedParse>(decoded));
    }

    private static UdSentenceEmitContext BuildEmitContext(UdSentence sentence)
    {
        var canonicals = new List<byte[]>();
        UdSentenceEmitContext.CollectCanonicals(sentence, canonicals);
        var context = new UdSentenceEmitContext();
        int n = 0;
        foreach (byte[] canonical in canonicals)
        {
            Hash128? root = ContentTierSpine.ResolveRoot(canonical);
            Assert.NotNull(root);
            double x = 0.05 + (++n % 10) * 0.02;
            context.RegisterRoot(canonical, root.Value, [x, 0.1, 0.2, 0.3]);
        }
        return context;
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
