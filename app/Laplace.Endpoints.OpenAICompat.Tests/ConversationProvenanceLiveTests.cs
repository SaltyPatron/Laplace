using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// Spec 34 §5 acceptance test, live: the same exchange deposited under two fresh
/// tenants proves — on the real writer spine and the real fold —
///   (1) evidence rows carry the per-tenant source AND the session as context,
///   (2) the two tenants' testimony is distinct evidence (provenance unmashed),
///   (3) the prompt→reply PRECEDES cell corroborates across tenants at ONE
///       consensus cell (isolated AND acting as a whole),
///   (4) scoped_consensus isolates tenant A's world from tenant B's,
///   (5) recall_session accepts the canonically minted session id.
/// Tenants/content are unique per run (fresh guid) so cells carry no prior
/// history — same discipline as ConverseLoopLiveTests. Tier=db: excluded from
/// the deploy gate; run locally against a seeded substrate.
/// </summary>
[Trait("Tier", "db")]
public sealed class ConversationProvenanceLiveTests
{
    [SkippableFact]
    public async Task Turns_CarryTenantSessionProvenance_FoldAndIsolate()
    {
        Skip.IfNot(SubstrateFloorPresent(), "substrate floor absent (unseeded/mid-reseed DB)");
        CodepointPerfcache.LoadDefault();

        var tag = Guid.NewGuid().ToString("N")[..10];
        var tenantA = $"t-{tag}a";
        var tenantB = $"t-{tag}b";
        var prompt = $"zzconv{tag} alpha question";
        var reply = $"zzconv{tag} alpha answer";

        await using var ds = new NpgsqlDataSourceBuilder(
            LaplaceInstall.PostgresConnectionString()).Build();

        var scopeA = ConversationContent.Resolve(tenantA);
        var scopeB = ConversationContent.Resolve(tenantB);
        var sessionA = ConversationContent.SessionId(tenantA, "s1");
        var sessionB = ConversationContent.SessionId(tenantB, "s1");

        Hash128 promptRoot = default, replyRoot = default;
        var inner = new NpgsqlSubstrateWriter(ds);
        await using (var acc = new ConsensusAccumulatingWriter(inner, ds))
        {
            var writer = (ISubstrateWriter)acc;
            foreach (var (scope, session) in new[] { (scopeA, sessionA), (scopeB, sessionB) })
            {
                foreach (var change in ConversationContent.BuildTenantBootstrapChanges(scope))
                    await writer.ApplyAsync(change);
                Assert.True(ConversationContent.TryBuildTurnChange(
                    scope, session,
                    Encoding.UTF8.GetBytes(prompt), Encoding.UTF8.GetBytes(reply),
                    userKey: "user-1",
                    out var turnChange, out promptRoot, out replyRoot));
                await writer.ApplyAsync(turnChange);
            }
        }

        // (1) evidence rows exist under the per-tenant source with session context.
        Assert.True(await CountAsync(ds,
            """
            SELECT count(*) FROM laplace.attestations
            WHERE source_id = laplace.source_id(@src) AND context_id = @ctx
            """,
            ("src", $"UserPrompt@{tenantA}"), ("ctx", sessionA.ToBytes())) >= 1,
            "tenant A's prompt testimony missing its source+session provenance");

        // (2) provenance unmashed: one PRECEDES evidence row per tenant.
        var precedesRows = await CountAsync(ds,
            """
            SELECT count(*) FROM laplace.attestations
            WHERE subject_id = @s AND type_id = laplace.relation_type_id('PRECEDES')
              AND object_id = @o
            """,
            ("s", promptRoot.ToBytes()), ("o", replyRoot.ToBytes()));
        Assert.True(precedesRows >= 2,
            $"expected distinct evidence rows for two tenants, saw {precedesRows}");

        // (3) both tenants corroborate ONE consensus cell.
        var witnesses = await CountAsync(ds,
            """
            SELECT COALESCE(max(witness_count), 0) FROM laplace.consensus
            WHERE subject_id = @s AND type_id = laplace.relation_type_id('PRECEDES')
              AND object_id = @o
            """,
            ("s", promptRoot.ToBytes()), ("o", replyRoot.ToBytes()));
        Assert.True(witnesses >= 2,
            $"PRECEDES cell should hold both tenants' testimony, witness_count={witnesses}");

        // (4) isolation: A's scoped world holds A's membership cell, not B's.
        await using (var conn = await ds.OpenConnectionAsync())
        {
            await using (var scopeCmd = new NpgsqlCommand(
                """
                CREATE TEMP TABLE consensus AS
                SELECT * FROM laplace.scoped_consensus(
                    ARRAY[laplace.source_id(@p), laplace.source_id(@r)])
                """, conn))
            {
                scopeCmd.Parameters.AddWithValue("p", $"UserPrompt@{tenantA}");
                scopeCmd.Parameters.AddWithValue("r", $"Response@{tenantA}");
                await scopeCmd.ExecuteNonQueryAsync();
            }

            Assert.True(await CountOnAsync(conn,
                "SELECT count(*) FROM consensus WHERE subject_id = @s AND object_id = @o",
                ("s", promptRoot.ToBytes()), ("o", sessionA.ToBytes())) >= 1,
                "tenant A's scoped world is missing A's own session membership");
            Assert.Equal(0, await CountOnAsync(conn,
                "SELECT count(*) FROM consensus WHERE subject_id = @s AND object_id = @o",
                ("s", promptRoot.ToBytes()), ("o", sessionB.ToBytes())));
        }

        // (5) recall_session accepts the canonically minted session id.
        await using (var conn = await ds.OpenConnectionAsync())
        await using (var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM laplace.recall_session(@p, @session)", conn))
        {
            cmd.Parameters.AddWithValue("p", prompt);
            cmd.Parameters.AddWithValue("session", sessionA.ToBytes());
            await cmd.ExecuteScalarAsync(); // must execute without error; rows may be 0
        }
    }

    private static async Task<long> CountAsync(
        NpgsqlDataSource ds, string sql, params (string Name, object Value)[] ps)
    {
        await using var conn = await ds.OpenConnectionAsync();
        return await CountOnAsync(conn, sql, ps);
    }

    private static async Task<long> CountOnAsync(
        NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static bool SubstrateFloorPresent()
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
