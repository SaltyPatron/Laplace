using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Tests.Structured;

public sealed class NativeAlignedLayerTests
{
    [Fact]
    public async Task FrameNetLexemeFlagsStayTogetherInTheirLexicalUnit()
    {
        using var fixture = new NativeCorpus();
        Assert.True(LaplaceInstall.TryRepoRoot(out string repository));
        string path = Path.Combine(repository, "app", "Laplace.Substrate.Tests",
            "Fixtures", "NativeRecipes", "xml", "Abandonment.xml");
        string[] bundle = Assert.Single((await fixture.Claims("framenet", path)).Claims()
            .Select(line => line.Split('\t')),
            parts => parts.Length==8 && parts[3]=="[Abandonment, abandon.v]");
        Assert.Equal(new[] { "abandon", "V", "false", "false" }, bundle.Skip(4));
    }

    [Fact]
    public async Task UdSentenceEmitsOneBundleForItsAlignedLayers()
    {
        using var fixture = new NativeCorpus();
        string path = fixture.Write("UD_English-EWT/en_ewt-ud-test.conllu", """
            # sent_id = native-layer-bundle
            # text = I run.
            1	I	I	PRON	PRP	Case=Nom|Person=1	2	nsubj	2:nsubj	_
            2	run	run	VERB	VBP	_	0	root	0:root	SpaceAfter=No
            3	.	.	PUNCT	.	_	2	punct	2:punct	_

            """);
        var sentenceClaims=(await fixture.Claims("universal-dependencies",path)).Claims()
            .Select(line => line.Split('\t')).Where(parts => parts[3]=="I run.").ToArray();
        string[] bundle=Assert.Single(sentenceClaims);
        Assert.True(bundle.Length>=12,string.Join('\t',bundle));
    }
}
