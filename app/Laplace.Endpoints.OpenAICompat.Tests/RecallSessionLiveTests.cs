using Laplace.Engine.Core;
using Npgsql;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>Live recall_session smoke — skipped when Postgres substrate is unreachable.</summary>
public sealed class RecallSessionLiveTests
{
    [SkippableFact]
    public async Task RecallSession_LiveSubstrate_ReturnsRows()
    {
        Skip.IfNot(CanReachSubstrate(), "Postgres substrate not reachable");

        await using var client = new SubstrateClient();
        var session = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("live-smoke-session"))[..16];
        var rows = await client.ConverseTurnsAsync(["what does dog mean?"], session, CancellationToken.None);
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
