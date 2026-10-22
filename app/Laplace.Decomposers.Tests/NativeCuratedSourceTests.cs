using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Tests;

public sealed class NativeCuratedSourceTests
{
    public static TheoryData<string, string[]> Products => new()
    {
        { "conceptnet", [
            "claim\t0\t1\t[агыруа, language:abk, n, NOUN]\tAntonym\t[аҧсуа, language:abk]",
            "claim\t0\t1\t[[агыруа, language:abk, n, NOUN], Antonym, [аҧсуа, language:abk]]\tthe strength with which this edge expresses this assertion\t1.0",
            "claim\t0\t1\t[thəχ kwo, adx, a, ADJ]\tAntonym\t[ʂap wə, adx]",
            "claim\t0\t1\t[[thəχ kwo, adx, a, ADJ], Antonym, [ʂap wə, adx]]\tthe strength with which this edge expresses this assertion\t1.0",
        ] },
        // The controlled link checks transient-key resolution between two actual
        // sentence records; this is a compiler fixture, not an upstream join proof.
        { "tatoeba", [
            "claim\t0\t1\t我們試試看！\tISO 639-3 language code\tcmn",
            "claim\t0\t1\t我该去睡觉了。\tISO 639-3 language code\tcmn",
            "claim\t0\t1\t我该去睡觉了。\tis the translation of\t我們試試看！",
        ] },
        { "opensubtitles", [
            "claim\t0\t1\t[カリガリ博士の小屋, jpn]\ttranslated\t[\"THE CABINET OF DR. CALIGARI\", eng]",
            "claim\t0\t1\t[幕１, jpn]\ttranslated\t[Act 1, eng]",
        ] },
    };

    [Theory]
    [MemberData(nameof(Products))]
    public async Task CanonicalNativeRecipesRetainTheDeclaredProducts(string source, string[] expected)
    {
        Assert.True(LaplaceInstall.TryRepoRoot(out string repository));
        string root = Path.Combine(repository, "app", "Laplace.Decomposers.Tests", "Fixtures", "NativeRecipes", source);
        string[] claims = await NativeCorpusAssertions.ClaimsAsync(source,
            Directory.GetFiles(root).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(expected.Order(StringComparer.Ordinal), claims.Order(StringComparer.Ordinal));
    }
}
