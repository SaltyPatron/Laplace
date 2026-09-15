using System.Runtime.CompilerServices;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// Independent matchup operands must not be serialized through two successive
/// datasource reads. Chess players additionally require their chess-owned comparator;
/// generic consensus standing must never be rendered as source Elo.
/// </summary>
public sealed class MatchupReadConcurrencyGateTests
{
    [Theory]
    [InlineData("public async Task<MatchupResponse?> MatchupAsync")]
    [InlineData("public async Task<MatchupVerdictResponse?> MatchupVerdictAsync")]
    public void IndependentTopicResolution_FansOutBeforeEitherResultIsConsumed(string signature)
    {
        var text = Read("app/Laplace.Endpoints.OpenAICompat/SubstrateClient.Matchup.cs");
        var method = ExtractMethod(text, signature);

        Assert.Contains("var xTask = ResolveTopicAsync(xRef, ct);", method);
        Assert.Contains("var yTask = ResolveTopicAsync(yRef, ct);", method);
        Assert.Contains("await Task.WhenAll(xTask, yTask)", method);
        Assert.DoesNotContain("var x = await ResolveTopicAsync", method);
        Assert.DoesNotContain("var y = await ResolveTopicAsync", method);
    }

    [Fact]
    public void ChessPlayers_SelectChessComparator_AndKeepEloSeparateFromStanding()
    {
        var client = Read("app/Laplace.Endpoints.OpenAICompat/SubstrateClient.Matchup.cs");
        var contract = Read("app/Laplace.Endpoints.OpenAICompat/Contracts/Matchup.cs");
        var web = Read("web/src/explore/matchup/MatchupView.tsx");
        var overview = Read("web/src/explore/entity/tabs/OverviewTab.tsx");

        Assert.Contains("IsChessPlayerAsync", client);
        Assert.Contains("ChessTapeAsync", client);
        Assert.Contains("ChessPlayerRatingsAsync", client);
        Assert.Contains("ChessPlayerRecordAsync", client);
        Assert.Contains("ChessMeetingsAsync", client);
        Assert.Contains("\"played against\", \"chess pairing\"", client);
        Assert.Contains("ChessMatchupSide", contract);
        Assert.Contains("source_rating_peak", contract);

        Assert.Contains("peak source Elo", web);
        Assert.Contains("chess.wins", web);
        Assert.Contains("played against", web);
        Assert.Contains("played against", overview);
        Assert.Contains("top standing", web);
        Assert.DoesNotContain(">top rating ·", web, StringComparison.Ordinal);
    }

    private static string ExtractMethod(string text, string signaturePrefix)
    {
        var start = text.IndexOf(signaturePrefix, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signaturePrefix}' not found");
        var nextMethod = text.IndexOf("\n    /// <summary>", start + signaturePrefix.Length, StringComparison.Ordinal);
        var nextPrivate = text.IndexOf("\n    private ", start + signaturePrefix.Length, StringComparison.Ordinal);
        var ends = new[] { nextMethod, nextPrivate }.Where(i => i > start).ToArray();
        var end = ends.Length == 0 ? text.Length : ends.Min();
        return text[start..end];
    }

    private static string Read(string repoRelative) => File.ReadAllText(RepoPath(repoRelative));

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
