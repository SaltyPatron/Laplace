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
}
