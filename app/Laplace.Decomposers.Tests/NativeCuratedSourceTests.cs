using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Tests;

public sealed class NativeCuratedSourceTests
{
    public static TheoryData<string, string[]> Products => new()
    {
        { "semlink", [
            "claim\t0\t1\tabandon.01\tvnclass:leave-51.2",
            "claim\t0\t1\t[abandon.01, vnclass:leave-51.2]\tARG0\ttheme",
            "claim\t0\t1\tabandon.02\tvnclass:leave-51.2",
            "claim\t0\t1\t[abandon.02, vnclass:leave-51.2]\tARG0\ttheme",
            "claim\t0\t1\tvnclass:knead-26.5\tMoving_in_place",
            "claim\t0\t1\tvnclass:knead-26.5\tBody_movement",
            "claim\t0\t1\tvnclass:knead-26.5\tCause_to_move_in_place",
            "claim\t0\t1\tvnclass:reflexive_appearance-48.1.2\tEvidence",
        ] },
        { "predicate-matrix", [
            "claim\t0\t1\tcat\tv\tabaixar.1.default\targ0\tvnclass:roll-51.3.1\tdrop\tAgent\t[drop, eng, ili:i31613]\tili:i31613\tBody_movement\t[Body_movement, drop.v]\t[Body_movement, Agent]\tdrop.01\t0\t0\tfactotum\tMotion\tDynamic;Location\tmotion\tili:i30898\t21\tMotion",
            "claim\t0\t1\t[drop, eng, ili:i31613]\tWordNet synset type\t2",
            "claim\t0\t1\t[drop, eng, ili:i31613]\tlexfile:verb.motion",
            "claim\t0\t1\t[drop, eng, ili:i31613]\tWordNet lexical identifier\t00",
        ] },
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
