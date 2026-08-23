using System.Text;
using Laplace.Decomposers.OMW;
using Xunit;

namespace Laplace.Decomposers.OMW.Tests;

public sealed class OMWRowParserTests
{
    [Fact]
    public void TryParseRow_WiktLemmaRow_ParsesSynsetAndLang()
    {
        byte[] line = Encoding.UTF8.GetBytes("00002098-a\teng:lemma\tunable");

        Assert.True(OMWRowParser.TryParseRow(line, "eng", out var row, out var value));
        Assert.Equal(2098L, row.Offset);
        Assert.Equal('a', row.SsType);
        Assert.Equal("eng", row.Lang);
        Assert.Equal(OmwType.Lemma, row.Type);
        Assert.Equal("unable", Encoding.UTF8.GetString(value));
    }

    [Theory]
    [InlineData("13983515-n\tarb:lemma:root\tظلم", "arb", "ظلم")]
    [InlineData("03012209-a\tarb:lemma:brokenplural\tأول", "arb", "أول")]
    public void TryParseRow_LemmaWithMorphologySubtype_ParsesAsLemma(
        string rowText, string expectedLang, string expectedValue)
    {
        byte[] line = Encoding.UTF8.GetBytes(rowText);

        Assert.True(OMWRowParser.TryParseRow(line, "arb", out var row, out var value));
        Assert.Equal(OmwType.Lemma, row.Type);
        Assert.Equal(expectedLang, row.Lang);
        Assert.Equal(expectedValue, Encoding.UTF8.GetString(value));
    }

    [Fact]
    public void EnumerateTabFiles_IncludesDataAndWiktGlobs()
    {
        string root = Path.Combine(Path.GetTempPath(), "omw-tab-" + Guid.NewGuid().ToString("N"));
        string wns = Path.Combine(root, "wns");
        Directory.CreateDirectory(Path.Combine(wns, "eng"));
        Directory.CreateDirectory(Path.Combine(wns, "wikt"));
        File.WriteAllText(Path.Combine(wns, "eng", "wn-data-eng.tab"), "# x\n");
        File.WriteAllText(Path.Combine(wns, "wikt", "wn-wikt-eng.tab"), "# x\n");
        try
        {
            var files = OMWTabFiles.EnumerateTabFiles(wns, langs: null).ToList();
            Assert.Equal(2, files.Count);
            Assert.Contains(files, f => f.EndsWith("wn-data-eng.tab", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(files, f => f.EndsWith("wn-wikt-eng.tab", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EnumerateTabFiles_IncludesCldrAndNodia_ExcludesFreqAndChanges()
    {
        string root = Path.Combine(Path.GetTempPath(), "omw-tab-" + Guid.NewGuid().ToString("N"));
        string wns = Path.Combine(root, "wns");
        Directory.CreateDirectory(Path.Combine(wns, "cldr"));
        Directory.CreateDirectory(Path.Combine(wns, "arb"));
        Directory.CreateDirectory(Path.Combine(wns, "msa"));
        File.WriteAllText(Path.Combine(wns, "cldr", "wn-cldr-deu.tab"), "# x\n");
        File.WriteAllText(Path.Combine(wns, "arb", "wn-nodia-arb.tab"), "# x\n");
        File.WriteAllText(Path.Combine(wns, "msa", "wn-freq-ind.tab"), "# x\n");
        File.WriteAllText(Path.Combine(wns, "arb", "arb-changes.tab"), "# x\n");
        try
        {
            var files = OMWTabFiles.EnumerateTabFiles(wns, langs: null).ToList();
            Assert.Contains(files, f => f.EndsWith("wn-cldr-deu.tab", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(files, f => f.EndsWith("wn-nodia-arb.tab", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(files, f => f.EndsWith("wn-freq-ind.tab", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(files, f => f.EndsWith("arb-changes.tab", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // OMW ships its own retractions -- 3,279 REMOVED rows across 26 <lang>-changes.tab
    // files -- and they were never globbed, so membership could only ever accumulate.
    [Fact]
    public void EnumerateChangesFiles_FindsRetractionsTheDataGlobsExclude()
    {
        string root = Path.Combine(Path.GetTempPath(), "omw-chg-" + Guid.NewGuid().ToString("N"));
        string wns = Path.Combine(root, "wns");
        Directory.CreateDirectory(Path.Combine(wns, "swe"));
        File.WriteAllText(Path.Combine(wns, "swe", "wn-data-swe.tab"), "# x\n");
        File.WriteAllText(Path.Combine(wns, "swe", "swe-changes.tab"),
            "2025-02-01\tREMOVED\t07451687-n\tswe:lemma\tbegravning\n");
        try
        {
            Assert.DoesNotContain(
                OMWTabFiles.EnumerateTabFiles(wns, langs: null),
                f => f.EndsWith("swe-changes.tab", StringComparison.OrdinalIgnoreCase));

            var changes = OMWTabFiles.EnumerateChangesFiles(wns).ToList();
            string file = Assert.Single(changes);
            Assert.EndsWith("swe-changes.tab", file, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // The retraction row is a data row with two extra leading fields, so slicing past the
    // second tab must yield exactly what the data tab would have produced -- otherwise the
    // refutation lands on a different triple than the assertion and never contests it.
    [Fact]
    public void ChangesRow_SlicedPastActionFields_ParsesAsTheDataRowItRetracts()
    {
        byte[] changes = Encoding.UTF8.GetBytes(
            "2025-02-01\tREMOVED\t07451687-n\tswe:lemma\tbegravning");
        byte[] data = Encoding.UTF8.GetBytes("07451687-n\tswe:lemma\tbegravning");

        int tabs = 0, cut = -1;
        for (int i = 0; i < changes.Length; i++)
            if (changes[i] == (byte)'\t' && ++tabs == 2) { cut = i; break; }
        Assert.True(cut > 0);

        Assert.True(OMWRowParser.TryParseRow(changes.AsSpan(cut + 1), "und", out var a, out var av));
        Assert.True(OMWRowParser.TryParseRow(data, "und", out var b, out var bv));

        Assert.Equal(b.Offset, a.Offset);
        Assert.Equal(b.SsType, a.SsType);
        Assert.Equal(b.Lang, a.Lang);
        Assert.Equal(b.Type, a.Type);
        Assert.Equal(Encoding.UTF8.GetString(bv), Encoding.UTF8.GetString(av));
    }
}
