using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// Live deposit → fold → walk: deposit a turn through the accumulating writer (mint +
/// inline fold), confirm a RELATED_TO triple through <c>FeedbackContent</c> until
/// <c>consensus.walk_branches</c> serves it, then refute it until the walk drops it.
/// Skipped when the substrate floor is absent.
///
/// Tokens carry a per-run guid suffix: consensus cells persist in the shared database,
/// so fixed ids would accumulate Glicko history across runs. The rolled-back
/// <c>chat_loop.sql</c> regress test proves the same loop in isolation.
/// </summary>
// Tier=live: deposits and folds durable cells in the seeded substrate; it is not a
// database-health gate.
[Trait("Tier", "live")]
public sealed class ConverseLoopLiveTests
{
    [SkippableFact]
    public async Task Loop_DepositConfirmWalkRefute_AnswerChanges()
    {
        Skip.IfNot(SubstrateFloorPresent(), "substrate floor absent (unseeded/mid-reseed DB)");
        CodepointPerfcache.LoadDefault();

        var tag = Guid.NewGuid().ToString("N")[..12];
        var tokA = $"zzloop{tag}a";
        var tokB = $"zzloop{tag}b";

        await using var ds = new NpgsqlDataSourceBuilder(
            LaplaceInstall.PostgresConnectionString()).Build();

        var inner = new NpgsqlSubstrateWriter(ds);
        await using (var acc = new ConsensusAccumulatingWriter(inner, ds))
        {
            var writer = (ISubstrateWriter)acc;
            await writer.ApplyAsync(UserPromptContent.BuildBootstrapChange());
            Assert.True(UserPromptContent.TryBuildWitnessChange(
                Encoding.UTF8.GetBytes($"{tokA} {tokB}"), "test/loop-turn", out var change, out _));
            await writer.ApplyAsync(change);
        }

        var resolved = await FeedbackContent.ResolveTokensAsync(ds, [tokA, tokB]);
        Assert.All(resolved, t => Assert.True(t.Usable, $"token not deposited: {t.Token}"));
        var subj = resolved[0].Id!.Value;
        var obj = resolved[1].Id!.Value;

        for (int i = 0; i < 20; i++)
            await FeedbackContent.ApplyAsync(ds,
                FeedbackContent.BuildTriple(subj, "RELATED_TO", obj, confirm: true));
        Assert.True(await WalkSeesEdge(ds, subj, obj),
            "confirmed claim not served by walk_branches");

        for (int i = 0; i < 60; i++)
            await FeedbackContent.ApplyAsync(ds,
                FeedbackContent.BuildTriple(subj, "RELATED_TO", obj, confirm: false));
        Assert.False(await WalkSeesEdge(ds, subj, obj),
            "refuted claim still served by walk_branches");
    }

    private static async Task<bool> WalkSeesEdge(NpgsqlDataSource ds, Hash128 subj, Hash128 obj)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM consensus.walk_branches(@s, laplace.relation_type_id('RELATED_TO'), 1, 8) w
            WHERE w.entity_id = @o
            """, conn);
        cmd.Parameters.AddWithValue("s", subj.ToBytes());
        cmd.Parameters.AddWithValue("o", obj.ToBytes());
        return (long)(await cmd.ExecuteScalarAsync())! > 0;
    }

    private static bool SubstrateFloorPresent()
    {
        try
        {
            // The floor is a reachable substrate plus the mapped Tier-0 ROM.
            using var conn = new NpgsqlConnection(LaplaceInstall.PostgresConnectionString());
            conn.Open();
            CodepointPerfcache.LoadDefault();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
