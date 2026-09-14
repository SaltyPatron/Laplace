using System.Runtime.CompilerServices;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessMoveCommentaryConcurrencyTests
{
    [Fact]
    public void IndependentHistoryAndBookReads_StartBeforeEitherIsAwaited()
    {
        var text = File.ReadAllText(RepoPath("app/Laplace.Chess/Service/ChessMoveCommentary.cs"));
        var start = text.IndexOf("public static async Task<string> BuildAsync", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = text.IndexOf("private static readonly Hash128 ExplainsRelation", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var method = text[start..end];

        Assert.Contains("var historyTask =", method);
        Assert.Contains("var bookTask =", method);
        Assert.Contains("await Task.WhenAll(historyTask, bookTask)", method);
        Assert.DoesNotContain("&& await HistoricalPositionLineAsync", method);
        Assert.DoesNotContain("&& await BookLineAsync", method);
    }

    private static string RepoPath(string repoRelative) =>
        Path.Combine(RepoRoot(), repoRelative.Replace('/', Path.DirectorySeparatorChar));

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        while (dir is not null &&
               !(Directory.Exists(Path.Combine(dir.FullName, "docs"))
                 && Directory.Exists(Path.Combine(dir.FullName, "app"))))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("repo root not found above test source");
    }
}
