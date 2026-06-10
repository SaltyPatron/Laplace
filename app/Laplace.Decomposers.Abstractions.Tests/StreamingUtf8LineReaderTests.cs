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
    public void Implementation_UsesArrayPoolNotPerLineByteArrays()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var src = Path.Combine(repoRoot, "app", "Laplace.Decomposers.Abstractions", "StreamingUtf8LineReader.cs");
        var text = File.ReadAllText(src);
        Assert.Contains("ArrayPool<byte>.Shared.Rent", text, StringComparison.Ordinal);
        Assert.DoesNotContain("new byte[", text, StringComparison.Ordinal);
    }
}
