using Laplace.Engine.Core;
using Npgsql;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// Live proof for the default conversational forward path. The generation eval used
/// to call converse.infer(), which is a ranked predictor and is not the default
/// generation.forward_text() path used by converse.chat(). A green infer probe could
/// therefore coexist with a broken or disconnected dynamic forward pass.
///
/// This test executes the native trajectory/consensus forward program directly AND
/// the public SubstrateClient conversation path against the same witnessed prompt.
/// Tier=live is intentional: correctness depends on the standing seeded substrate.
/// </summary>
[Trait("Tier", "live")]
public sealed class ForwardPassLiveTests
{
    private const string Prompt = "The opposite of hot is";
    private const string Expected = "cold";

    [SkippableFact]
    public async Task DefaultForwardPass_ReachesWitnessedAnswerThroughDirectAndConversationPaths()
    {
        Skip.IfNot(CanReachSeededSubstrate(), "seeded Postgres substrate not reachable");

        await using var conn = new NpgsqlConnection(LaplaceInstall.PostgresConnectionString());
        await conn.OpenAsync();

        var emitted = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT entity
            FROM generation.forward_text(@prompt)
            ORDER BY step
            """, conn))
        {
            cmd.Parameters.AddWithValue("prompt", Prompt);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(0))
                    emitted.Add(reader.GetString(0).Trim());
            }
        }

        Assert.NotEmpty(emitted);
        Assert.Contains(emitted, value =>
            string.Equals(value, Expected, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(emitted, LooksLikeInternalIdentity);

        await using var client = new SubstrateClient();
        var rows = await client.ConverseAsync(Prompt, session: null, CancellationToken.None);
        Assert.NotEmpty(rows);
        var reply = string.Concat(rows.Select(static row => row.Reply));
        Assert.Contains(Expected, reply, StringComparison.OrdinalIgnoreCase);
        Assert.False(LooksLikeInternalIdentity(reply.Trim()),
            $"default conversation leaked an internal identity surface: {reply}");
    }

    private static bool LooksLikeInternalIdentity(string value)
    {
        if (value.Length == 0) return false;
        if (value.Length is >= 16 and <= 36
            && value.All(static c => char.IsAsciiHexDigit(c) || c is '.' or '…'))
            return true;
        if (value.Length >= 8 && value[..^2].All(char.IsDigit)
            && value[^2] == '-' && "nvasrNVASR".Contains(value[^1]))
            return true;
        return value.Length > 1 && (value[0] is 'i' or 'I')
            && value[1..].All(char.IsDigit);
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
