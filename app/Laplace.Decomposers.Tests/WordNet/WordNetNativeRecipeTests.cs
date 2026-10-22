using Laplace.Decomposers.Tests;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.WordNet.Tests;

public sealed class WordNetNativeRecipeTests
{
    [Fact]
    public async Task NativeRecordsRetainCanonicalConceptsLexicalizationsPointersAndWholeGlosses()
    {
        string[] claims = await NativeCorpusAssertions.ClaimsAsync("princeton-wordnet",
            Fixture("data.noun"), Fixture("data.verb"));
        Assert.Equal(24, claims.Length);
        Assert.Contains("claim\t0\t1\tili:i35545\twords in the synset\t[entity, eng, ili:i35545]", claims);
        Assert.Contains("claim\t0\t1\tili:i35546\twords in the synset\t[physical entity, eng, ili:i35546]", claims);
        Assert.Contains("claim\t0\t1\tili:i35546\t@\tili:i35545", claims);
        Assert.Contains("claim\t0\t1\tili:i21790\twords in the synset\t[hold, eng, ili:i21790]", claims);
        Assert.Contains("claim\t0\t1\tili:i21790\ta definition, one or more example sentences, or both\tkeep from exhaling or expelling; \"hold your breath\"", claims);
        Assert.Contains("claim\t0\t1\tili:i21790\tgeneric verb sentence frames for words in the synset\t8", claims);
        Assert.DoesNotContain(claims, claim => claim.Contains("hold%", StringComparison.Ordinal)
            || claim.Contains("physical_entity", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NativeVerbFrameRecordsUseTheirDeclaredNumericIdentifiers()
    {
        string[] claims = await NativeCorpusAssertions.ClaimsAsync("princeton-wordnet", Fixture("frames.vrb"));
        Assert.Equal(new[]
        {
            "claim\t0\t1\t1\tgeneric verb sentence frame\tSomething ----s",
            "claim\t0\t1\t2\tgeneric verb sentence frame\tSomebody ----s",
            "claim\t0\t1\t8\tgeneric verb sentence frame\tSomebody ----s something",
            "claim\t0\t1\t10\tgeneric verb sentence frame\tSomething ----s somebody",
            "claim\t0\t1\t13\tgeneric verb sentence frame\tSomebody ----s on something",
        }, claims);
    }

    private static string Fixture(string name)
    {
        Assert.True(LaplaceInstall.TryRepoRoot(out string repository));
        return Path.Combine(repository, "app", "Laplace.Decomposers.Tests", "Fixtures", "NativeRecipes", "wordnet", name);
    }
}
