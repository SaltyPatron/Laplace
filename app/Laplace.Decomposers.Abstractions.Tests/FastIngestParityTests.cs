using System.Text;
using Laplace.Decomposers.OMW;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public class FastIngestParityTests
{
    [Fact]
    public void OmwFastIngest_ParsesLemmaRow()
    {
        ReadOnlySpan<byte> line = "1740-n\teng:lemma\tcat"u8;
        Assert.True(OMWFastIngest.TryParseRow(line, "eng", out var row));
        Assert.Equal(OMWFastIngest.OmwType.Lemma, row.Type);
        Assert.Equal("cat", row.Value);
        Assert.Equal("eng", row.Lang);
    }

    [Fact]
    public void OmwFastIngest_SkipsCommentLines()
    {
        Assert.False(OMWFastIngest.TryParseRow("# comment"u8, "eng", out _));
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
    public void FastIngestDecomposers_UseStreamingLineReader()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var required = new (string Project, string[] Needles)[]
        {
            ("Laplace.Decomposers.OpenSubtitles", ["OpenSubtitlesFastIngest", "StreamingUtf8LineReader"]),
            ("Laplace.Decomposers.UD", ["StreamingUtf8LineReader", "TsvSpan"]),
            ("Laplace.Decomposers.OMW", ["OMWFastIngest", "StreamingUtf8LineReader"]),
            ("Laplace.Decomposers.WordNet", ["StreamingUtf8LineReader"]),
            ("Laplace.Decomposers.Wiktionary", ["WiktionaryFastIngest", "Utf8JsonReader"]),
        };

        foreach (var (project, needles) in required)
        {
            var dir = Path.Combine(repoRoot, "app", project);
            var text = string.Join('\n', Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .Select(File.ReadAllText));
            foreach (var needle in needles)
                Assert.True(text.Contains(needle, StringComparison.Ordinal),
                    $"{project} must reference fast-ingest pattern '{needle}'");
        }
    }
}
