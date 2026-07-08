using System.Collections.Concurrent;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Tests;
using Laplace.Decomposers.UD;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.UD.Tests;

// Validates .scratchpad/16 §1a (HAS_LANGUAGE at sentence root) and §5 (XPOS IS_A UPOS + FEAT_*).
public sealed class UdSentenceEmitterTests
{
    private static readonly Hash128 UdSource = UDDecomposer.Source;

    static UdSentenceEmitterTests()
    {
        CodepointPerfcache.Load(TestInstall.ResolvePerfcacheOrThrow());
        LanguageReference.EnsureLoaded(TestIngestPaths.Iso639);
    }

    [Fact]
    public void HasLanguage_AttestsOnce_AtSentenceRoot_NotPerToken()
    {
        byte[] sentenceText = Encoding.UTF8.GetBytes("The cat sat.");
        byte[] cat = Encoding.UTF8.GetBytes("cat");
        byte[] sat = Encoding.UTF8.GetBytes("sat");
        var tokens = new List<UdToken>
        {
            new(1, "1", cat, cat, true, "NOUN", "NN", ["Number=Sing"], 0, "root", "_", "_"),
            new(2, "2", sat, sat, true, "VERB", "VB", [], 1, "nsubj", "_", "_"),
        };
        var sentence = new UdSentence(sentenceText, tokens, [], 2);

        var ctx = BuildEmitContext(sentenceText, cat, sat);
        var change = Emit(sentence, ctx);

        Hash128 hasLang = RelationTypeRegistry.Resolve("HAS_LANGUAGE").Id;
        var langAtts = change.Attestations.Where(a => a.TypeId == hasLang).ToList();

        Assert.Single(langAtts);
        Assert.Equal(ctx.RootFor(sentenceText), langAtts[0].SubjectId);
        Assert.Equal(LanguageReference.Resolve("en"), langAtts[0].ObjectId);

        Hash128? catRoot = ContentTierSpine.ResolveRoot(cat);
        Hash128? satRoot = ContentTierSpine.ResolveRoot(sat);
        Assert.DoesNotContain(langAtts, a => a.SubjectId == catRoot);
        Assert.DoesNotContain(langAtts, a => a.SubjectId == satRoot);
    }

    [Fact]
    public void HasLanguage_PerToken_OnlyWhenMiscLangMarksCodeSwitching()
    {
        byte[] sentenceText = Encoding.UTF8.GetBytes("Bonjour world");
        byte[] bonjour = Encoding.UTF8.GetBytes("Bonjour");
        byte[] world = Encoding.UTF8.GetBytes("world");
        var tokens = new List<UdToken>
        {
            new(1, "1", bonjour, bonjour, true, "INTJ", "_", [], 0, "root", "_", "Lang=fr"),
            new(2, "2", world, world, true, "NOUN", "NN", [], 1, "flat", "_", "_"),
        };
        var sentence = new UdSentence(sentenceText, tokens, [], 2);

        var ctx = BuildEmitContext(sentenceText, bonjour, world);
        var change = Emit(sentence, ctx, langCode: "en");

        Hash128 hasLang = RelationTypeRegistry.Resolve("HAS_LANGUAGE").Id;
        var langAtts = change.Attestations.Where(a => a.TypeId == hasLang).ToList();

        Assert.Equal(2, langAtts.Count);
        Assert.Contains(langAtts, a =>
            a.SubjectId == ctx.RootFor(sentenceText)
            && a.ObjectId == LanguageReference.Resolve("en"));
        Assert.Contains(langAtts, a =>
            a.SubjectId == ctx.RootFor(bonjour)
            && a.ObjectId == LanguageReference.Resolve("fr"));
        Assert.DoesNotContain(langAtts, a => a.SubjectId == ctx.RootFor(world));
    }

    [Fact]
    public void Xpos_LinksToUposViaIsA_AndFeatsEmitOnForm()
    {
        byte[] sentenceText = Encoding.UTF8.GetBytes("Cats run.");
        byte[] cats = Encoding.UTF8.GetBytes("Cats");
        var tokens = new List<UdToken>
        {
            new(1, "1", cats, cats, true, "NOUN", "NNS", ["Number=Plur"], 0, "root", "_", "_"),
        };
        var sentence = new UdSentence(sentenceText, tokens, [], 1);

        var ctx = BuildEmitContext(sentenceText, cats);
        var change = Emit(sentence, ctx);

        Hash128 hasXpos = RelationTypeRegistry.Resolve("HAS_XPOS").Id;
        Hash128 isA = RelationTypeRegistry.Resolve("IS_A").Id;
        Hash128 hasPos = RelationTypeRegistry.Resolve("HAS_POS").Id;
        Hash128 nounUpos = PosReference.CanonicalId("NOUN");

        var formRoot = ctx.RootFor(cats);
        Assert.NotNull(formRoot);

        var xposAtt = Assert.Single(change.Attestations.Where(a =>
            a.TypeId == hasXpos && a.SubjectId == formRoot));
        Assert.NotNull(xposAtt.ObjectId);

        Assert.Contains(change.Attestations, a =>
            a.TypeId == isA && a.SubjectId == xposAtt.ObjectId && a.ObjectId == nounUpos);
        Assert.Contains(change.Attestations, a =>
            a.TypeId == hasPos && a.SubjectId == formRoot && a.ObjectId == nounUpos);

        var featNumber = RelationTypeRegistry.ResolveFeature("Number");
        Assert.Contains(change.Attestations, a =>
            a.TypeId == featNumber.Id && a.SubjectId == formRoot);
    }

    private static UdSentenceEmitContext BuildEmitContext(params byte[][] canonicals)
    {
        var ctx = new UdSentenceEmitContext();
        foreach (var bytes in canonicals)
        {
            var root = ContentTierSpine.ResolveRoot(bytes);
            Assert.NotNull(root);
            ctx.RegisterRoot(bytes, root.Value);
        }
        return ctx;
    }

    private static SubstrateChange Emit(UdSentence sentence, UdSentenceEmitContext ctx, string langCode = "en")
    {
        Hash128 langId = LanguageReference.Resolve(langCode);
        var b = new SubstrateChangeBuilder(
            UdSource, "ud/emitter-test", null,
            entityCapacity: 128, physicalityCapacity: 128, attestationCapacity: 256);
        UdSentenceEmitContext.EmitWitness(
            b, sentence, langId, langCode,
            new HashSet<Hash128>(), new ConcurrentIdSet(),
            new ConcurrentDictionary<string, byte>(), ctx, UdSource);
        return b.Build();
    }
}
