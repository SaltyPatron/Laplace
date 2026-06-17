using System.Text;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public class StreamingUtf8LineReaderTests
{
    [Fact]
    public async Task ReadLinesAsync_YieldsAllLines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"laplace-lines-{Guid.NewGuid():N}.tsv");
        try
        {
            const int lines = 128;
            await File.WriteAllTextAsync(path,
                string.Join('\n', Enumerable.Range(0, lines).Select(i => $"a{i}\tb{i}")));
            int count = 0;
            await foreach (var line in StreamingUtf8LineReader.ReadLinesAsync(path))
            {
                Assert.True(line.Length > 0);
                count++;
            }
            Assert.Equal(lines, count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadLinesAsync_YieldsBlankLines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"laplace-blank-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(path, "a\n\nb\n");
            var lines = new List<string>();
            await foreach (var line in StreamingUtf8LineReader.ReadLinesAsync(path))
                lines.Add(Encoding.UTF8.GetString(line.Span));
            Assert.Equal(["a", "", "b"], lines);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void CountConlluSentences_CountsBlankLineDelimitedSentences()
    {
        var path = Path.Combine(Path.GetTempPath(), $"laplace-conllu-{Guid.NewGuid():N}.conllu");
        try
        {
            File.WriteAllText(path,
                """
                # sent 1
                1	Hello	hello	INTJ	_	_	0	root	_	_

                # sent 2
                1	World	world	NOUN	_	_	0	root	_	_

                """);
            Assert.Equal(2, EtlInventory.CountConlluSentences(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Implementation_UsesArrayPoolNotPerLineByteArrays()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var src = Path.Combine(repoRoot, "app", "Laplace.Decomposers.Abstractions", "StreamingUtf8LineReader.cs");
        var text = File.ReadAllText(src);
        Assert.Contains("ArrayPool<byte>.Shared.Rent", text, StringComparison.Ordinal);
        Assert.DoesNotContain("new byte[", text, StringComparison.Ordinal);
    }
}
