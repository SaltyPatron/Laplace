using System.Linq;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Tests;
using Laplace.Decomposers.WordNet;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.WordNet.Tests;

[Collection("GrammarPerfcache")]
public sealed class WordNetDecomposerTests
{
    static WordNetDecomposerTests() => CodepointPerfcache.LoadDefault();

    [Fact]
    public void TryParseDataLine_LexicalPointers_CaptureSourceWord()
    {
        
        
        
        
        const string line =
            "00001740 00 a 01 able 0 005 = 05200169 n 0000 = 05616246 n 0000 "
            + "+ 05616246 n 0101 + 05200169 n 0101 ! 00002098 a 0101 "
            + "| (usually followed by `to') having the necessary means or skill";

        Assert.True(WordNetDecomposer.TryParseDataLine(line, out var syn));
        var antonym = Assert.Single(syn.Pointers, p => p.Symbol == "!");
        Assert.Equal(1, antonym.SrcWord);   
        Assert.Equal(1, antonym.TgtWord);
        Assert.All(syn.Pointers.Where(p => p.Symbol == "="), p => Assert.Equal(0, p.SrcWord)); 
        Assert.Contains(syn.Pointers, p => p.Symbol == "+" && p.SrcWord == 1);                 
    }

    [Fact]
    public void TryParseDataLine_VerbSynset_ParsesVerbFrames()
    {
        const string line =
            "00002325 29 v 01 respire 1 005 $ 00001740 v 0000 @ 02108377 v 0000 "
            + "+ 03110322 a 0101 + 00831191 n 0103 + 00830811 n 0101 01 + 02 00 "
            + "| undergo the biomedical and metabolic processes of respiration";

        Assert.True(WordNetDecomposer.TryParseDataLine(line, out var syn));
        Assert.Equal(2325L, syn.Offset);
        Assert.Equal('v', syn.SsType);
        Assert.Single(syn.Frames);
        Assert.Equal((2, 0), syn.Frames[0]);
    }

    [Fact]
    public void TryParseDataLine_VerbWithoutFrameBlock_HasNoFrames()
    {
        const string line =
            "00001740 29 v 04 breathe 0 take_a_breath 0 respire 0 suspire 3 021 "
            + "* 00005041 v 0000 | draw air into, and expel out of, the lungs";

        Assert.True(WordNetDecomposer.TryParseDataLine(line, out var syn));
        Assert.Equal('v', syn.SsType);
        Assert.Empty(syn.Frames);
    }

    [Fact]
    public void ExactSenseKey_PreservesSatelliteHead_WhileCompatibilityKeyDoesNot()
    {
        const string uninhabited = "abandoned%5:00:00:uninhabited:00";
        const string uninhibited = "abandoned%5:00:00:uninhibited:00";

        Assert.Equal(uninhabited,
            SourceEntityIdConventions.NormalizeExactSenseKey(uninhabited));
        Assert.Equal(uninhibited,
            SourceEntityIdConventions.NormalizeExactSenseKey(uninhibited));
        Assert.Null(SourceEntityIdConventions.NormalizeExactSenseKey("abandoned%5:00:00"));

        Assert.NotEqual(SenseAnchor.ExactId(uninhabited), SenseAnchor.ExactId(uninhibited));
        Assert.Equal(SenseAnchor.Id(uninhabited), SenseAnchor.Id(uninhibited));
    }

    [SkippableFact]
    public async Task Decompose_SatelliteCollision_EmitsDistinctSensesBehindCompatibilityAlias()
    {
        Skip.IfNot(TestInstall.HasFullCiliMap(), "full CILI map is not installed");
        const string uninhabited = "abandoned%5:00:00:uninhabited:00";
        const string uninhibited = "abandoned%5:00:00:uninhibited:00";
        string root = Path.Combine(Path.GetTempPath(), "wordnet-sense-" + Guid.NewGuid().ToString("N"));
        string dict = Path.Combine(root, "WordNet-3.0", "dict");
        Directory.CreateDirectory(dict);
        await File.WriteAllTextAsync(Path.Combine(dict, "index.sense"),
            $"{uninhabited} 01313004 1 1\n{uninhibited} 01317231 2 0\n");

        try
        {
            var entities = new Dictionary<Hash128, EntityRow>();
            var attestations = new List<AttestationRow>();
            var context = new FakeContext(root, new NullWriter());
            await foreach (var change in new WordNetDecomposer().DecomposeAsync(
                               context, DecomposerOptions.Default))
            {
                foreach (var entity in change.Entities) entities[entity.Id] = entity;
                attestations.AddRange(change.Attestations);
            }

            Hash128 exactA = SenseAnchor.ExactId(uninhabited)!.Value;
            Hash128 exactB = SenseAnchor.ExactId(uninhibited)!.Value;
            Hash128 compatibility = SenseAnchor.Id(uninhabited)!.Value;
            Hash128 correspondsTo = RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO");
            Hash128 hasSense = RelationTypeRegistry.RelationTypeId("HAS_SENSE");

            Assert.NotEqual(exactA, exactB);
            Assert.Equal(compatibility, SenseAnchor.Id(uninhibited));
            Assert.Equal(EntityTypeRegistry.WordNetSense, entities[exactA].TypeId);
            Assert.Equal(EntityTypeRegistry.WordNetSense, entities[exactB].TypeId);
            Assert.Equal(EntityTypeRegistry.SourceReference, entities[compatibility].TypeId);
            Assert.Contains(attestations, a =>
                a.TypeId == correspondsTo
                && ((a.SubjectId == compatibility && a.ObjectId == exactA)
                    || (a.SubjectId == exactA && a.ObjectId == compatibility)));
            Assert.Contains(attestations, a =>
                a.TypeId == correspondsTo
                && ((a.SubjectId == compatibility && a.ObjectId == exactB)
                    || (a.SubjectId == exactB && a.ObjectId == compatibility)));
            Assert.Contains(attestations, a => a.TypeId == hasSense && a.ObjectId == exactA);
            Assert.Contains(attestations, a => a.TypeId == hasSense && a.ObjectId == exactB);
            Assert.DoesNotContain(attestations, a =>
                a.TypeId == hasSense && a.ObjectId == compatibility);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task Decompose_SynsetMembership_UsesExactSenseChainWithoutShortcutWitness()
    {
        Skip.IfNot(TestInstall.HasFullCiliMap(), "full CILI map is not installed");
        const string senseKey = "able%3:00:00::";
        const string data =
            "00001740 00 a 01 able 0 005 = 05200169 n 0000 = 05616246 n 0000 "
            + "+ 05616246 a 0101 + 05200169 a 0101 ! 00002098 a 0101 "
            + "| having the necessary means or skill\n";
        string root = Path.Combine(Path.GetTempPath(), "wordnet-membership-" + Guid.NewGuid().ToString("N"));
        string dict = Path.Combine(root, "WordNet-3.0", "dict");
        Directory.CreateDirectory(dict);
        await File.WriteAllTextAsync(Path.Combine(dict, "data.adj"), data);
        await File.WriteAllTextAsync(
            Path.Combine(dict, "index.sense"), $"{senseKey} 00001740 1 5\n");

        try
        {
            var attestations = new List<AttestationRow>();
            await foreach (var change in new WordNetDecomposer().DecomposeAsync(
                               new FakeContext(root, new NullWriter()), DecomposerOptions.Default))
                attestations.AddRange(change.Attestations);

            Hash128 lemma = ContentEmitter.RootId("able")!.Value;
            Hash128 sense = SenseAnchor.ExactId(senseKey)!.Value;
            Hash128 synset = ConceptAnchor.SynsetId(1740, 'a')!.Value;
            Hash128 hasSense = RelationTypeRegistry.RelationTypeId("HAS_SENSE");
            Hash128 isSenseOf = RelationTypeRegistry.RelationTypeId("IS_SENSE_OF");
            Hash128 synonym = RelationTypeRegistry.RelationTypeId("IS_SYNONYM_OF");

            Assert.Contains(attestations, a =>
                a.SubjectId == lemma && a.TypeId == hasSense && a.ObjectId == sense);
            Assert.Contains(attestations, a =>
                a.SubjectId == sense && a.TypeId == isSenseOf && a.ObjectId == synset);
            Assert.DoesNotContain(attestations, a =>
                a.TypeId == synonym
                && ((a.SubjectId == lemma && a.ObjectId == synset)
                    || (a.SubjectId == synset && a.ObjectId == lemma)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
