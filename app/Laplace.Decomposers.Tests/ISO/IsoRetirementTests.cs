using System.Linq;
using System.IO;
using System.Collections.Generic;
using Xunit;

namespace Laplace.Decomposers.ISO.Tests;

/// <summary>
/// ISO 639-3 retirement rows: reason=N states the code names no language (a refutation);
/// reason=S (split) names its successors only in Ret_Remedy, which
/// <c>IsoRetirementRemedy</c> parses alongside Change_To.
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

    // "Chittagonian (new identifier [ctg])": a bracketed code inside a parenthetical is
    // the successor the standard names.
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

    // The tests below run the per-row rule (IsoRetirementRemedy.Classify) over the admitted
    // retirement table, not just the remedy parser.
    private const string Retirements = "/vault/Data/ISO639/iso-639-3_Retirements.tab";

    private static IEnumerable<(string Id, string Reason, string[] Succ, bool Keep)> Rows()
    {
        bool header = false;
        foreach (string line in File.ReadLines(Retirements))
        {
            if (!header) { header = true; continue; }
            var c = line.Split('\t');
            if (c.Length < 4) continue;
            string id = c[0].Trim();
            if (id.Length != 3) continue;
            var (reason, succ, keep) = IsoRetirementRemedy.Classify(
                c[2].Trim(), c[3].Trim(), c.Length > 4 ? c[4].Trim() : "");
            yield return (id, reason, succ, keep);
        }
    }

    [Fact]
    public void EveryRetirementRowIsAccountedFor()
    {
        if (!File.Exists(Retirements)) return;
        var rows = Rows().ToList();
        Assert.Equal(386, rows.Count);

        // Every row is kept: it either states non-existence or names at least one successor.
        Assert.Empty(rows.Where(r => !r.Keep));

        int refutes = rows.Count(r => r.Reason == "N");
        int edges = rows.Sum(r => r.Succ.Length);
        Assert.Equal(72, refutes);     // object-null refutations
        Assert.Equal(481, edges);      // SUPERSEDED_BY successors
    }

    [Fact]
    public void SplitRowsResolveThroughRemedy_NotChangeTo()
    {
        if (!File.Exists(Retirements)) return;
        // reason=S has an empty Change_To, so every successor comes from Ret_Remedy.
        var splits = Rows().Where(r => r.Reason == "S").ToList();
        Assert.Equal(102, splits.Count);
        Assert.All(splits, r => Assert.NotEmpty(r.Succ));
        Assert.True(splits.Sum(r => r.Succ.Length) > splits.Count,
            "split retirements must yield more successors than rows");
    }
}
