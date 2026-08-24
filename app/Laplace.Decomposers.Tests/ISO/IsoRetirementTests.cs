using Xunit;

namespace Laplace.Decomposers.ISO.Tests;

/// <summary>
/// ISO 639-3 ships 386 retirement rows. The phase required a 3-character Change_To, which
/// silently dropped 174 of them: 72 reason=N (the standard saying the code names NO real
/// language -- the largest block of negative evidence ISO states) and 102 reason=S (split,
/// where the successors live in Ret_Remedy rather than Change_To).
/// </summary>
public sealed class IsoRetirementTests
{
    private static string[] Parse(string remedy)
        => IsoRetirementRemedy.SuccessorsFromRemedy(remedy);

    [Fact]
    public void Split_Into_Two_Yields_Both_Codes()
    {
        Assert.Equal(
            new[] { "sfb", "vgt" },
            Parse("Split into Langue des signes de Belgique Francophone [sfb], and Vlaamse Gebarentaal [vgt]"));
    }

    [Fact]
    public void Split_Into_Five_Yields_All_Five()
    {
        Assert.Equal(
            new[] { "zhn", "zyg", "zyn", "zzj", "zhd" },
            Parse("Split into five languages: Nong Zhuang [zhn];  Yang Zhuang [zyg]; Yongnan Zhuang [zyn]; Zuojiang Zhuang [zzj]; Dai Zhuang [zhd]."));
    }

    // "Chittagonian (new identifier [ctg])" -- a bracketed code inside a parenthetical is
    // still the successor the standard names.
    [Fact]
    public void Parenthesised_New_Identifier_Is_Found()
    {
        Assert.Equal(
            new[] { "rhg", "ctg" },
            Parse("Split into Rohingya [rhg], and Chittagonian (new identifier [ctg])"));
    }

    [Fact]
    public void Empty_And_Unbracketed_Yield_Nothing()
    {
        Assert.Empty(Parse(""));
        Assert.Empty(Parse("Merged into Bogus"));
        Assert.Empty(Parse("[ABC] [toolong] [ab]"));   // uppercase / wrong length are not codes
    }
}
