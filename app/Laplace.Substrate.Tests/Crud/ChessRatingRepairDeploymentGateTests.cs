using Xunit;

namespace Laplace.Substrate.Tests.Crud;

public class ChessRatingRepairDeploymentGateTests
{
    private static string Root =>
        Laplace.Decomposers.Abstractions.Tests.TypeIdLawTests.FindRepoRootPublic();

    [Fact]
    public void LegacyDeploymentEntrypointCannotPerformOrScheduleRepair()
    {
        var path = Path.Combine(
            Root, "extension", "laplace_substrate", "sql", "functions", "chess",
            "repair_player_ratings.sql.in");
        var source = File.ReadAllText(path);

        const string signature = "CREATE PROCEDURE chess.repair_player_ratings(";
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "legacy deployment compatibility entrypoint is missing");

        var bodyStart = source.IndexOf("AS $$", start, StringComparison.Ordinal);
        var bodyEnd = source.IndexOf("$$;", bodyStart + 5, StringComparison.Ordinal);
        Assert.True(bodyStart > start && bodyEnd > bodyStart, "legacy deployment procedure body is not inspectable");
        var body = source[(bodyStart + 5)..bodyEnd];

        Assert.DoesNotContain("repair_player_ratings_batch", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("laplace.attestations", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("laplace.consensus", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CALL", body, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("CREATE OR REPLACE PROCEDURE chess.repair_player_ratings_batch(", source);
        Assert.Contains("p_subjects bytea[]", source);
        Assert.Contains("Deployment does not queue", source);
    }
}
