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
/// This test executes the canonical traceable native forward program, its normal text
/// projection, and the public SubstrateClient conversation path against the same
/// witnessed prompt. The receipt assertions ensure a non-empty answer cannot hide a
/// disconnected query/evidence path. Tier=live is intentional: correctness depends on
/// the standing seeded substrate rather than a miniature fixture.
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

        var trace = new List<ForwardReceipt>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT t.step,
                   converse.label_or_hex(t.entity),
                   t.candidate_count,
                   t.ordered_context_count,
                   t.proposal_channel_count,
                   t.exact_channel_count,
                   t.sequence_occurrences,
                   t.covered_occurrences,
                   t.relation_families,
                   t.opposed_occurrences,
                   t.support_relation = laplace.relation_type_id('IS_ANTONYM_OF') AS antonym_support,
                   t.event,
                   t.routing_round,
                   t.root_id IS NOT NULL AS has_root
            FROM generation.forward_trace(
                @prompt,
                24, 5, 0.0, 10, NULL,
                8, 10, NULL, NULL) AS t
            ORDER BY t.step, t.routing_round
            """, conn))
        {
            cmd.Parameters.AddWithValue("prompt", Prompt);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                trace.Add(new ForwardReceipt(
                    Step: reader.GetInt32(0),
                    Entity: reader.IsDBNull(1) ? "" : reader.GetString(1).Trim(),
                    CandidateCount: reader.GetInt32(2),
                    OrderedContextCount: reader.GetInt32(3),
                    ProposalChannelCount: reader.GetInt32(4),
                    ExactChannelCount: reader.GetInt32(5),
                    SequenceOccurrences: reader.GetInt64(6),
                    CoveredOccurrences: reader.GetInt32(7),
                    RelationFamilies: reader.GetInt32(8),
                    OpposedOccurrences: reader.GetInt32(9),
                    AntonymSupport: !reader.IsDBNull(10) && reader.GetBoolean(10),
                    Event: reader.GetString(11),
                    RoutingRound: reader.GetInt32(12),
                    HasRoot: reader.GetBoolean(13)));
            }
        }

        Assert.NotEmpty(trace);
        Assert.All(trace, row =>
        {
            Assert.True(row.HasRoot);
            Assert.True(row.CandidateCount > 0);
            Assert.True(row.OrderedContextCount > 0);
            Assert.True(row.ProposalChannelCount >= 0);
            Assert.True(row.ExactChannelCount >= 0);
            Assert.True(row.SequenceOccurrences >= 0);
            Assert.True(row.CoveredOccurrences >= 0);
            Assert.True(row.RelationFamilies >= 0);
            Assert.True(row.OpposedOccurrences >= 0);
            Assert.True(row.RoutingRound >= 0);
            Assert.Contains(row.Event, new[] { "route", "emit" });
        });
        Assert.Contains(trace, row => row.ExactChannelCount > 0);
        Assert.Contains(trace, row =>
            string.Equals(row.Entity, Expected, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(trace, row =>
            string.Equals(row.Entity, Expected, StringComparison.OrdinalIgnoreCase)
            && row.AntonymSupport);

        var emitted = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT entity
            FROM generation.forward_text(@prompt, 24, 5, 0.0, 10)
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
        var rows = await client.ConverseAsync(
            Prompt,
            session: null,
            new ConverseOptions(MaxTokens: 24, Window: 5, Temperature: 0.0, TopK: 10),
            CancellationToken.None);
        Assert.NotEmpty(rows);
        var reply = string.Concat(rows.Select(static row => row.Reply));
        Assert.Contains(Expected, reply, StringComparison.OrdinalIgnoreCase);
        Assert.False(LooksLikeInternalIdentity(reply.Trim()),
            $"default conversation leaked an internal identity surface: {reply}");
    }

    private readonly record struct ForwardReceipt(
        int Step,
        string Entity,
        int CandidateCount,
        int OrderedContextCount,
        int ProposalChannelCount,
        int ExactChannelCount,
        long SequenceOccurrences,
        int CoveredOccurrences,
        int RelationFamilies,
        int OpposedOccurrences,
        bool AntonymSupport,
        string Event,
        int RoutingRound,
        bool HasRoot);

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
