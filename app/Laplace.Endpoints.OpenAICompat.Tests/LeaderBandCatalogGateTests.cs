using System.Runtime.CompilerServices;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// The leaders page needs immutable band names, not the live relation-band census.
/// Keep the bounded leaderboard and naming catalog in one set-wise database command
/// and reject any regression that puts converse.relation_bands() back on the request path.
/// </summary>
public sealed class LeaderBandCatalogGateTests
{
    [Fact]
    public void Leaders_UsesOneNamedSetWiseReadWithoutLiveCountAggregate()
    {
        var text = Read("app/Laplace.Endpoints.OpenAICompat/SubstrateClient.Matchup.cs");
        var method = ExtractMethod(text, "public async Task<IReadOnlyList<BandLeaders>> LeadersAsync");

        Assert.Contains("NpgsqlSubstrateReads.BandLeadersNamedAsync(", method);
        Assert.DoesNotContain("NpgsqlSubstrateReads.BandLeadersAsync(", method);
        Assert.DoesNotContain("RelationBandsAsync(", method);
        Assert.DoesNotContain("Task.WhenAll", method);
    }

    [Fact]
    public void NamedLeaderRead_JoinsImmutableCatalogAndNeverLiveCensus()
    {
        var text = Read("app/Laplace.Substrate/Crud/Npgsql/NpgsqlSubstrateReads.BandLeaders.cs");
        Assert.Contains("FROM ops.band_leaders(@bands, @per) AS l", text);
        Assert.Contains("JOIN converse.relation_band_catalog() AS b", text);
        Assert.DoesNotContain("FROM converse.relation_bands()", text);
        Assert.DoesNotContain("JOIN converse.relation_bands()", text);
    }

    private static string ExtractMethod(string text, string signaturePrefix)
    {
        var start = text.IndexOf(signaturePrefix, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signaturePrefix}' not found");
        var nextMethod = text.IndexOf("\n    /// <summary>", start + signaturePrefix.Length, StringComparison.Ordinal);
        return nextMethod > start ? text[start..nextMethod] : text[start..];
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
