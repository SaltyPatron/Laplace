using System.Runtime.CompilerServices;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// Leaders read the bounded leaderboard and the immutable band-name catalog in one
/// set-wise command (<c>leaders.native_named</c>); the live <c>converse.relation_bands()</c>
/// census is never on the request path.
/// </summary>
public sealed class LeaderBandCatalogGateTests
{
    [Fact]
    public void Leaders_UsesOneNamedSetWiseReadWithoutLiveCountAggregate()
    {
        var text = Read("app/Laplace.Endpoints.OpenAICompat/SubstrateClient.Matchup.cs");
        var method = ExtractMethod(text, "public async Task<IReadOnlyList<BandLeaders>> LeadersAsync");

        Assert.Contains("NpgsqlSubstrateReads.ClaimLeadersNamedAsync(", method);
        Assert.DoesNotContain("NpgsqlSubstrateReads.BandLeadersAsync(", method);
        Assert.DoesNotContain("RelationBandsAsync(", method);
        Assert.DoesNotContain("Task.WhenAll", method);
    }

    [Fact]
    public void NamedLeaderRead_UsesNativeCatalogAndNeverLiveCensus()
    {
        var text = Read("app/Laplace.Substrate/Crud/Npgsql/NpgsqlSubstrateReads.BandLeaders.cs");
        var catalog = Read("engine/core/src/sql_catalog.def");
        Assert.Contains("SqlCatalog.Get(\"leaders.native_named\")", text);
        Assert.DoesNotContain("SELECT ", text);
        Assert.Contains("SQL_QUERY(\"leaders.native_named\"", catalog);
        int start = catalog.IndexOf("SQL_QUERY(\"leaders.native_named\"", StringComparison.Ordinal);
        int end = catalog.IndexOf("\nSQL_", start + 1, StringComparison.Ordinal);
        string named = end < 0 ? catalog[start..] : catalog[start..end];
        Assert.Contains("FROM ops.claim_leaders($1,$2) WITH ORDINALITY", named);
        Assert.Contains("FROM converse.relation_band_catalog() b", named);
        Assert.Contains("LEFT JOIN leaders n ON n.band=b.band", named);
        Assert.DoesNotContain("BandLeadersNamedAsync(", Read("app/Laplace.Endpoints.OpenAICompat/SubstrateClient.Matchup.cs"));
        Assert.DoesNotContain("FROM converse.relation_bands()", named);
        Assert.DoesNotContain("JOIN converse.relation_bands()", named);
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
