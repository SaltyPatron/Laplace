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
            ("Wiktionary", ["GrammarIngestDecomposer", "WiktionaryGrammarWitness", "IGrammarWitness"]),
            ("SemLink", ["GrammarIngestHandler", "SemLinkGrammarWitness", "IGrammarWitness"]),
            ("Tatoeba", ["DecomposerMultiFile<GrammarIngestRecord", "GrammarIngestHandler",
                "TatoebaGrammarWitness", "IngestPipelineDefaults.StructuredGrammar"]),
            // ConceptNet + Atomic2020 are triple sources on the shared
            // RelationTripleDecomposerBase path (extraction only), NOT the grammar spine.
            ("ConceptNet", ["RelationTripleRecord", "ExtractRecordsAsync", "RelationTripleDecomposerBase"]),
            ("OMW", ["DecomposerMultiFile<GrammarIngestRecord", "GrammarIngestHandler",
                "OMWGrammarWitness", "OMWRowParser"]),
            ("Atomic2020", ["RelationTripleRecord", "ExtractRecordsAsync", "RelationTripleDecomposerBase"]),
            ("UD", ["DecomposerMultiFile<UdIngestRecord", "UdIngestHandler", "UdConlluParser"]),
        };

        foreach (var (project, needles) in grammarSpine)
        {
            var dir = Path.Combine(repoRoot, "app", "Laplace.Decomposers", project);
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
    public void OpenSubtitles_UsesIngestPipeline_NotHandRolledBuilderLoop()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var dir = Path.Combine(repoRoot, "app", "Laplace.Decomposers", "OpenSubtitles");
        var text = string.Join('\n', Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains("bin") && !p.Contains("obj"))
            .Select(File.ReadAllText));
        Assert.Contains("OpenSubtitlesZipIngest", text, StringComparison.Ordinal);
        Assert.Contains("RelationTripleDecomposerBase", text, StringComparison.Ordinal);
        Assert.Contains("ExtractRecordsAsync", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenSubtitlesIngestHandler", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenSubtitlesFastIngest", text, StringComparison.Ordinal);
    }
}
