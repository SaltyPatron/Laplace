using System.IO;
using System.Linq;
using Xunit;

namespace Laplace.Decomposers.Unicode.Tests;

/// <summary>
/// DerivedNormalizationProps.txt states normalization quick-check verdicts: No (a
/// refutation) and Maybe (a draw) per codepoint and form. Yes is carried by absence.
/// </summary>
public sealed class UcdNormalizationQcTests
{
    private const string UcdDir = "/vault/Data/UCD/Public/UCD/latest/ucd";

    private static bool Available => File.Exists(Path.Combine(UcdDir, "DerivedNormalizationProps.txt"));

    [SkippableFact]
    public void Quick_Check_Verdicts_Are_Read_From_The_Real_Corpus()
    {
        Skip.IfNot(Available, $"UCD not present at {UcdDir}");
        var ucd = UcdProperties.Load(UcdDir);

        // Expected counts, with ranges expanded to codepoints ("0340..0341 ; NFC_QC; N" is
        // two verdicts):
        //   NFD_QC=N 13,253   NFKD_QC=N 17,086   NFKC_QC=N 4,965   NFC_QC=N 1,120  -> 36,424
        //   NFC_QC=M    132   NFKC_QC=M    132                                     ->    264
        int maybes = ucd.NormalizationQc.Sum(kv => kv.Value.Count(v => v.Maybe));
        int nos = ucd.NormalizationQcCount - maybes;

        Assert.Equal(36424, nos);
        Assert.Equal(264, maybes);
    }

    [SkippableFact]
    public void Only_The_Four_Stated_Forms_Appear_And_Yes_Is_Never_Synthesised()
    {
        Skip.IfNot(Available, $"UCD not present at {UcdDir}");
        var ucd = UcdProperties.Load(UcdDir);

        var forms = ucd.NormalizationQc.SelectMany(kv => kv.Value).Select(v => v.Form).Distinct().OrderBy(f => f);
        Assert.Equal(new[] { "NFC", "NFD", "NFKC", "NFKD" }, forms);

        // Yes is carried by absence and is never synthesized: absence is not evidence, so
        // only stated verdicts are read and the set stays sparse.
        Assert.True(ucd.NormalizationQcCount < 50_000,
            $"quick-check should be sparse; got {ucd.NormalizationQcCount}");
    }
}
