using System.IO;
using System.Linq;
using Xunit;

namespace Laplace.Decomposers.Unicode.Tests;

/// <summary>
/// DerivedNormalizationProps.txt is the only UCD file that states a NEGATIVE, and the
/// decomposer never opened it. Measured on the 2026-08-23 seed, UnicodeDecomposer had
/// 1,631,783 CONFIRM attestations, zero REFUTEs and zero DRAWs -- and it is not alone:
/// thirteen of fifteen seeded sources emit neither, which is why source trust is not
/// learnable from the substrate's own evidence.
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

        // Counted directly from the corpus, with ranges EXPANDED to codepoints -- the file
        // states 1,382 lines but "0340..0341 ; NFC_QC; N" is two verdicts, not one:
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

        // Yes is the default and is carried by ABSENCE from the file. Materialising it would
        // be ~4.4M invented Confirms; spec 05 says an absent row is UNKNOWN, not evidence.
        // Sparse against 1,114,112 codepoints -- 3.3%, and every one of them stated.
        Assert.True(ucd.NormalizationQcCount < 50_000,
            $"quick-check should be sparse; got {ucd.NormalizationQcCount}");
    }
}
