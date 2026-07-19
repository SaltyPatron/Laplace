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
/// see the walk drop it — proving the C# deposit → fold → walk lanes wire to
/// the substrate end to end. Skipped when the substrate is unreachable.
///
/// The tokens are UNIQUE PER RUN (a fresh guid suffix). This is load-bearing,
/// not cosmetic: consensus cells persist and the CI runner shares one live
/// database, so fixed token ids would accumulate Glicko history across runs —
/// a prior run's refutes would poison the next run's "confirmed → served"
/// assertion (exactly the failure this replaces). A fresh cell each run makes
/// the confirm→served / refute→dropped transition deterministic and idempotent.
/// The deposits are truthful, trivial, low-trust (UserFeedback) episodes — the
/// substrate is built to accumulate episodes — so they are left in place rather
/// than hand-deleted (a direct live-DB DELETE is the manual mutation the project
/// bans; the deterministic, fully-isolated proof of the same loop is the
/// rolled-back chat_loop.sql regress test).
/// </summary>
public sealed class ConverseLoopLiveTests
{
    [SkippableFact]
    public async Task Loop_DepositConfirmWalkRefute_AnswerChanges()
    {
        Skip.IfNot(CanReachSubstrate(), "Postgres substrate not reachable");
        CodepointPerfcache.LoadDefault();

        // Fresh, collision-proof tokens so this run's cell has no prior history.
        var tag = Guid.NewGuid().ToString("N")[..12];
        var tokA = $"zzloop{tag}a";
        var tokB = $"zzloop{tag}b";

        await using var ds = new NpgsqlDataSourceBuilder(
            LaplaceInstall.PostgresConnectionString()).Build();

        // Deposit the "turn": mints the tokens as witnessed content, folded inline.
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
