using System.Runtime.CompilerServices;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class ChessPlayerReadConcurrencyGateTests
{
    [Fact]
    public void PlayerDetail_ReleasesValidationConnectionThenFansOutIndependentReads()
    {
        var text = Read("app/Laplace.Endpoints.OpenAICompat/SubstrateClient.Chess.cs");
        var method = ExtractMethod(text, "public async Task<ChessPlayerResponse?> ChessPlayerAsync");

        Assert.Contains("var recordTask = NpgsqlSubstrateReads.ChessPlayerRecordAsync", method);
        Assert.Contains("var ratingsTask = NpgsqlSubstrateReads.ChessPlayerRatingsAsync", method);
        Assert.Contains("var opponentsTask = NpgsqlSubstrateReads.ChessHeadToHeadAsync", method);
        Assert.Contains("var displayTask = NpgsqlDisplayLabels.ReadOneAsync", method);
        Assert.Contains("var profileTask = NpgsqlSubstrateReads.ChessPlayerProfileEdgesAsync", method);
        Assert.Contains("Task.WhenAll(recordTask, ratingsTask, opponentsTask, displayTask, profileTask)", method);

        Assert.DoesNotContain("var record = await NpgsqlSubstrateReads.ChessPlayerRecordAsync", method);
        Assert.DoesNotContain("var ratings = await NpgsqlSubstrateReads.ChessPlayerRatingsAsync", method);
        Assert.DoesNotContain("var opponents = await NpgsqlSubstrateReads.ChessHeadToHeadAsync", method);
        Assert.DoesNotContain("var display = await NpgsqlDisplayLabels.ReadOneAsync", method);
        Assert.DoesNotContain("var profileEdges = await NpgsqlSubstrateReads.ChessPlayerProfileEdgesAsync", method);
    }

    private static string ExtractMethod(string text, string signaturePrefix)
    {
        var start = text.IndexOf(signaturePrefix, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signaturePrefix}' not found");
        var nextMethod = text.IndexOf("\n    private static IReadOnlyList<ChessIdentityProfile>", start, StringComparison.Ordinal);
        Assert.True(nextMethod > start);
        return text[start..nextMethod];
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
