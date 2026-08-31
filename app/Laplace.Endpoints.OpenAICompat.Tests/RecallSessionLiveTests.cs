using Laplace.Engine.Core;
using Npgsql;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// Live product smoke for the seeded conversation surface. This is deliberately
/// Tier=live, not Tier=db: database health does not imply that lexical/knowledge
/// seeds are resident or that the conversational forward path is ready.
/// </summary>
[Trait("Tier", "live")]
public sealed class RecallSessionLiveTests
{
    [SkippableFact]
    public async Task RecallSession_LiveSubstrate_ReturnsRows()
    {
        Skip.IfNot(CanReachSeededSubstrate(), "seeded Postgres substrate not reachable");

        await using var client = new SubstrateClient();
        var session = Laplace.Decomposers.Abstractions.ConversationContent
            .SessionId("live-smoke", "live-smoke-session").ToBytes();
        var rows = await client.ConverseAsync("what does dog mean?", session, CancellationToken.None);
        Assert.NotNull(rows);
    }

    private static bool CanReachSeededSubstrate()
    {
        try
        {
            using var conn = new NpgsqlConnection(LaplaceInstall.PostgresConnectionString());
            conn.Open();
            using var cmd = new NpgsqlCommand(
                "SELECT 1 FROM laplace.entities WHERE type_id = laplace.entity_type_id('Codepoint') LIMIT 1", conn);
            return cmd.ExecuteScalar() is not null;
        }
        catch
        {
            return false;
        }
    }
}
