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
        Assert.Equal(41, claims.Length);
        Assert.Equal(14, claims.Count(claim => claim.Contains("\tidentifier vocabulary\tili", StringComparison.Ordinal)));
        Assert.Equal(3, claims.Count(claim => claim.Contains("\tdenotes\t", StringComparison.Ordinal)));
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
        var frames = new (string Id, string Text)[]
        {
            ("1", "Something ----s"), ("2", "Somebody ----s"), ("8", "Somebody ----s something"),
            ("10", "Something ----s somebody"), ("13", "Somebody ----s on something"),
        };
        Assert.Equal(frames.SelectMany(frame => new[]
        {
            $"claim\t0\t1\t{frame.Id}\tgeneric verb sentence frame\t{frame.Text}",
            $"claim\t0\t1\t{frame.Id}\tidentifier vocabulary\twnframe",
            $"claim\t0\t1\t[wnframe, {frame.Id}]\tidentifies\t{frame.Id}",
            $"claim\t0\t1\t[wnframe, {frame.Text}]\tidentifies\t{frame.Id}",
        }), claims);
    }

    private static string Fixture(string name)
    {
        Assert.True(LaplaceInstall.TryRepoRoot(out string repository));
        return Path.Combine(repository, "app", "Laplace.Decomposers.Tests", "Fixtures", "NativeRecipes", "wordnet", name);
    }
}
