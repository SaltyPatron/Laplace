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
///   (3) ordered message occurrences remain distinct while exact content is shared,
///   (4) scoped_consensus isolates tenant A's world from tenant B's,
///   (5) exact session recall returns both surfaces and roles without writing.
/// Tenants/content are unique per run (fresh guid) so cells carry no prior
/// history — same discipline as ConverseLoopLiveTests. Tier=live: this is a
/// seeded/shared product acceptance probe, not a database-health fixture.
/// </summary>
[Trait("Tier", "live")]
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
        Hash128[] turnsA = [], turnsB = [];
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
                    out var turnChange, out promptRoot, out replyRoot, out var turnIds));
                Assert.Equal(2, turnIds.Length);
                if (session == sessionA) turnsA = turnIds;
                else turnsB = turnIds;
                await acc.ApplyConversationTurnAsync(turnChange, session, turnIds);
            }
        }

        Assert.True(await CountAsync(ds,
            """
            SELECT count(*) FROM laplace.attestations
            WHERE source_id = laplace.source_id(@src) AND context_id = @ctx
            """,
            ("src", $"UserPrompt@{tenantA}"), ("ctx", sessionA.ToBytes())) >= 1,
            "tenant A's prompt testimony missing its source+session provenance");

        Assert.NotEqual(turnsA[0], turnsB[0]);
        Assert.NotEqual(turnsA[1], turnsB[1]);
        foreach (var (scope, session, turns) in new[]
                 { (scopeA, sessionA, turnsA), (scopeB, sessionB, turnsB) })
        {
            await using var manifest = ds.CreateCommand(
                "SELECT turn_id FROM converse.session_turn_ids(@session,NULL) ORDER BY ordinal");
            manifest.Parameters.AddWithValue("session", session.ToBytes());
            await using (var reader = await manifest.ExecuteReaderAsync())
            {
                foreach (var turn in turns)
                {
                    Assert.True(await reader.ReadAsync());
                    Assert.Equal(turn.ToBytes(), reader.GetFieldValue<byte[]>(0));
                }
                Assert.False(await reader.ReadAsync());
            }
            Assert.Equal(1, await CountAsync(ds,
            """
            SELECT count(*) FROM laplace.attestations
            WHERE subject_id = @s AND type_id = laplace.relation_type_id('DEPENDS_ON')
              AND object_id = @o AND source_id = @source AND context_id = @session
            """,
                ("s", turns[1].ToBytes()), ("o", turns[0].ToBytes()),
                ("source", scope.ResponseSource.ToBytes()), ("session", session.ToBytes())));
            Assert.Equal(1, await CountAsync(ds,
                """
                SELECT count(*) FROM laplace.consensus
                WHERE subject_id=@s AND type_id=laplace.relation_type_id('DEPENDS_ON')
                  AND object_id=@o AND witness_count=1
                """, ("s", turns[1].ToBytes()), ("o", turns[0].ToBytes())));
        }

        // Both tenants' occurrences bind the same exact content roots. Session
        // order is read from the physicality, never recreated as PRECEDES.
        Assert.Equal(2, await CountAsync(ds,
            """
            SELECT count(*) FROM converse.message_content_ids(@messages)
            WHERE content_id = @content
            """,
            ("messages", new[] { turnsA[0].ToBytes(), turnsB[0].ToBytes() }),
            ("content", promptRoot.ToBytes())));
        Assert.Equal(2, await CountAsync(ds,
            """
            SELECT count(*) FROM converse.message_content_ids(@messages)
            WHERE content_id = @content
            """,
            ("messages", new[] { turnsA[1].ToBytes(), turnsB[1].ToBytes() }),
            ("content", replyRoot.ToBytes())));

        await using (var conn = await ds.OpenConnectionAsync())
        {
            await using (var scopeCmd = new NpgsqlCommand(
                """
                CREATE TEMP TABLE consensus AS
                SELECT * FROM consensus.scoped_consensus(
                    ARRAY[laplace.source_id(@p), laplace.source_id(@r)])
                """, conn))
            {
                scopeCmd.Parameters.AddWithValue("p", $"UserPrompt@{tenantA}");
                scopeCmd.Parameters.AddWithValue("r", $"Response@{tenantA}");
                await scopeCmd.ExecuteNonQueryAsync();
            }

            Assert.True(await CountOnAsync(conn,
                "SELECT count(*) FROM consensus WHERE subject_id = @s AND object_id = @o",
                ("s", turnsA[0].ToBytes()), ("o", sessionA.ToBytes())) >= 1,
                "tenant A's scoped world is missing A's own session membership");
            Assert.Equal(0, await CountOnAsync(conn,
                "SELECT count(*) FROM consensus WHERE subject_id = @s AND object_id = @o",
                ("s", turnsB[0].ToBytes()), ("o", sessionB.ToBytes())));
        }

        await using (var conn = await ds.OpenConnectionAsync())
        {
            await using var transaction = await conn.BeginTransactionAsync();
            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, transaction))
                await readOnly.ExecuteNonQueryAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT role,surface FROM converse.session_turns(@session,NULL) ORDER BY ordinal", conn, transaction);
            cmd.Parameters.AddWithValue("session", sessionA.ToBytes());
            await using var reader = await cmd.ExecuteReaderAsync();
            foreach (var (role, surface) in new[] { ("user", prompt), ("assistant", reply) })
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(role, reader.GetString(0));
                Assert.Equal(surface, reader.GetString(1));
            }
            Assert.False(await reader.ReadAsync());
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
