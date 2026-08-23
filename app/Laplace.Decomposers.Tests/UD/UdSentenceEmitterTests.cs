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
        EmitAndDecode(UdSentence sentence, string langCode = "en")
    {
        Hash128 langId = LanguageReference.Resolve(langCode);
        var builder = new SubstrateChangeBuilder(
            UdSource, "ud/emitter-test", null,
            entityCapacity: 256, physicalityCapacity: 256, attestationCapacity: 256);
        UdSentenceEmitContext context = BuildEmitContext(sentence);
        UdSentenceEmitContext.EmitWitness(
            builder, sentence, langId, langCode, "ud/test.conllu",
            new HashSet<Hash128>(), new ConcurrentIdSet(),
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
