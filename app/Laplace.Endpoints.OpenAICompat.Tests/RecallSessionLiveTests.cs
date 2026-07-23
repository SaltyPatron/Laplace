using Laplace.Engine.Core;
using Npgsql;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>Live recall_session smoke — skipped when Postgres substrate is unreachable.</summary>
[Trait("Tier", "db")]
public sealed class RecallSessionLiveTests
{
    [SkippableFact]
    public async Task RecallSession_LiveSubstrate_ReturnsRows()
    {
        Skip.IfNot(CanReachSubstrate(), "Postgres substrate not reachable");

        await using var client = new SubstrateClient();
        // The canonical session mint (spec 34) — recall_session treats it as an
        // opaque carry key, so the 16-byte id passes unchanged.
        var session = Laplace.Decomposers.Abstractions.ConversationContent
            .SessionId("live-smoke", "live-smoke-session").ToBytes();
        var rows = await client.ConverseAsync("what does dog mean?", session, CancellationToken.None);
        Assert.NotNull(rows);
    }

    private static bool CanReachSubstrate()
    {
        try
        {
            using var conn = new NpgsqlConnection(LaplaceInstall.PostgresConnectionString());
            conn.Open();
            using var cmd = new NpgsqlCommand("SELECT laplace.word_id('the');", conn);
            cmd.ExecuteScalar();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
