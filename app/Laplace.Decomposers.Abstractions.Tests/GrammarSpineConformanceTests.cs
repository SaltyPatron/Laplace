using System.Text;
using Laplace.Decomposers.OMW;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public class GrammarSpineConformanceTests
{
    [Fact]
    public void OmwRowParser_ParsesLemmaRow()
    {
        ReadOnlySpan<byte> line = "1740-n\teng:lemma\tcat"u8;
        Assert.True(OMWRowParser.TryParseRow(line, "eng", out var row, out var valueUtf8));
        Assert.Equal(OmwType.Lemma, row.Type);
        Assert.Equal("cat", Encoding.UTF8.GetString(valueUtf8));
        Assert.Equal("eng", row.Lang);
    }

    [Fact]
    public void OmwRowParser_SkipsCommentLines()
    {
        Assert.False(OMWRowParser.TryParseRow("# comment"u8, "eng", out _, out _));
    }

    [Fact]
    public void TsvSpan_ParsesConllUFields()
    {
        ReadOnlySpan<byte> line = "1\tform\t_\t_\t_\t_\t0\troot\t_\t_"u8;
        Assert.True(TsvSpan.TryField(line, 0, out var id));
        Assert.True(TsvSpan.TryField(line, 1, out var form));
        Assert.Equal("1", Encoding.UTF8.GetString(id));
        Assert.Equal("form", Encoding.UTF8.GetString(form));
    }

    [Fact]
    public void TabularDecomposers_UseStructuredGrammarIngest()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var grammarSpine = new (string Project, string[] Needles)[]
        {
            ("Laplace.Decomposers.Wiktionary", ["StructuredGrammarIngest", "WiktionaryGrammarWitness", "IGrammarWitness"]),
            ("Laplace.Decomposers.SemLink", ["StructuredGrammarIngest", "SemLinkGrammarWitness", "IGrammarWitness"]),
            ("Laplace.Decomposers.Tatoeba", ["StructuredGrammarIngest", "TatoebaGrammarWitness", "IGrammarWitness", "ContentWitnessBatch"]),
            ("Laplace.Decomposers.ConceptNet", ["StructuredGrammarIngest", "ConceptNetGrammarWitness", "IGrammarWitness", "ContentWitnessBatch"]),
            ("Laplace.Decomposers.OMW", ["StructuredGrammarIngest", "OMWGrammarWitness", "IGrammarWitness", "OMWRowParser"]),
            ("Laplace.Decomposers.Atomic2020", ["StructuredGrammarIngest", "Atomic2020GrammarWitness", "IGrammarWitness", "ContentWitnessBatch"]),
        };

        foreach (var (project, needles) in grammarSpine)
        {
            var dir = Path.Combine(repoRoot, "app", project);
            var text = string.Join('\n', Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .Select(File.ReadAllText));
            foreach (var needle in needles)
                Assert.True(text.Contains(needle, StringComparison.Ordinal),
                    $"{project} must use grammar spine pattern '{needle}'");
        }
    }

    [Fact]
    public void OpenSubtitles_UsesContentWitnessBatch_NotHandRolledTabularParse()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var dir = Path.Combine(repoRoot, "app", "Laplace.Decomposers.OpenSubtitles");
        var text = string.Join('\n', Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains("bin") && !p.Contains("obj"))
            .Select(File.ReadAllText));
        Assert.Contains("OpenSubtitlesZipIngest", text, StringComparison.Ordinal);
        Assert.Contains("ContentWitnessBatch", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenSubtitlesFastIngest", text, StringComparison.Ordinal);
    }
}
