using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// The closed OODA loop, live: deposit a turn (mint + inline fold), confirm a
/// triple through the feedback lane, see the walk serve it; refute it harder,
/// see the walk drop it. Runs on test-scoped nonsense tokens so no real
/// knowledge is touched. Skipped when the substrate is unreachable.
/// </summary>
public sealed class ConverseLoopLiveTests
{
    [SkippableFact]
    public async Task Loop_DepositConfirmWalkRefute_AnswerChanges()
    {
        Skip.IfNot(CanReachSubstrate(), "Postgres substrate not reachable");
        CodepointPerfcache.LoadDefault();

        await using var ds = new NpgsqlDataSourceBuilder(
            LaplaceInstall.PostgresConnectionString()).Build();

        // Deposit the "turn": mints the tokens as witnessed content, folded inline.
        const string prompt = "zzchatloopa zzchatloopb";
        var inner = new NpgsqlSubstrateWriter(ds);
        await using (var acc = new ConsensusAccumulatingWriter(inner, ds))
        {
            var writer = (ISubstrateWriter)acc;
            await writer.ApplyAsync(UserPromptContent.BuildBootstrapChange());
            Assert.True(UserPromptContent.TryBuildWitnessChange(
                Encoding.UTF8.GetBytes(prompt), "test/loop-turn", out var change, out _));
            await writer.ApplyAsync(change);
        }

        var resolved = await FeedbackContent.ResolveTokensAsync(
            ds, ["zzchatloopa", "zzchatloopb"]);
        Assert.All(resolved, t => Assert.True(t.Usable, $"token not deposited: {t.Token}"));
        var subj = resolved[0].Id!.Value;
        var obj = resolved[1].Id!.Value;

        // Confirm until the conservative signed µ (eff_mu − neutral) is positive:
        // the walk must serve the claim.
        for (int i = 0; i < 20; i++)
            await FeedbackContent.ApplyAsync(ds,
                FeedbackContent.BuildTriple(subj, "RELATED_TO", obj, confirm: true));
        Assert.True(await WalkSeesEdge(ds, subj, obj),
            "confirmed claim not served by walk_branches");

        // Refute harder: the edge goes signed-negative and the walk drops it —
        // the answer changed because the consensus changed.
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
            FROM laplace.walk_branches(@s, laplace.relation_type_id('RELATED_TO'), 1, 8) w
            WHERE w.entity_id = @o
            """, conn);
        cmd.Parameters.AddWithValue("s", subj.ToBytes());
        cmd.Parameters.AddWithValue("o", obj.ToBytes());
        return (long)(await cmd.ExecuteScalarAsync())! > 0;
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
