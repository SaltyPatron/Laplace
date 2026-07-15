using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using global::Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>
/// Guards the installed SQL surface: consensus_upsert (the inline fold) must be
/// present after CREATE EXTENSION, a real fold must leave the consensus table's
/// OID-bound dependents (secondary indexes, views, pg_extension_config_dump
/// registration) intact while producing rows, and the consolidated helpers stay
/// callable with their expected shapes.
/// </summary>
[Collection("substrate-pg")]
[Trait("Tier", "db")]
public class SqlConsolidationTests
{
    private readonly LocalPgFixture _pg;
    public SqlConsolidationTests(LocalPgFixture pg) => _pg = pg;

    private async Task<object?> ScalarAsync(string sql, params object[] args)
    {
        await using var cmd = _pg.DataSource.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        return await cmd.ExecuteScalarAsync();
    }

    // ---- the inline fold entry point must install ---------------------------------------
    [Fact]
    public async Task ConsensusUpsert_IsInstalled()
    {
        var kind = await ScalarAsync(
            "SELECT p.prokind FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace "
            + "WHERE n.nspname = 'laplace' AND p.proname = 'consensus_upsert'");
        Assert.Equal('f', Assert.IsType<char>(kind));

        // The body — not merely the name — installed: the ordered MERGE with the
        // server-side native fold is what makes the inline fold correct.
        var src = (string)(await ScalarAsync(
            "SELECT p.prosrc FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace "
            + "WHERE n.nspname = 'laplace' AND p.proname = 'consensus_upsert'"))!;
        Assert.Contains("MERGE INTO consensus", src);
        Assert.Contains("laplace_glicko2_accumulate_games", src);
    }

    // (Per-table stat/autovacuum tuning is NOT extension SQL — it moved to the db-scoped
    //  tune-laplace step: scripts/win/tune-laplace.cmd + pipeline.sh phase_tune_laplace,
    //  which runs after the tables exist. Nothing to assert at CREATE EXTENSION time.)

    // ---- the inline fold preserves the consensus table's OID-bound dependents ----------
    private static Hash128 H(string seed) => Hash128.OfCanonical($"sql-consolidation-test/{seed}");

    private static AttestationRow Att(string subj, string obj, long games, long scoreFp, long phiFp, long unixUs)
        => new(
            Hash128.OfCanonical($"sql-consolidation-test/att/{subj}/{obj}/{unixUs}"),
            H(subj), H("rel"), H(obj), H("source"), null,
            AttestationOutcome.Confirm, unixUs, games, scoreFp, phiFp);

    [Fact]
    public async Task InlineFold_PreservesIndexesViewsAndExtensionConfig()
    {
        // Drive one real inline fold through the apply path.
        long phi = 30_000_000_000L;
        long ts = IntentStage.PgEpochUnixUs + 5_000_000;
        var inner = new NpgsqlSubstrateWriter(_pg.DataSource);
        await using (var writer = new ConsensusAccumulatingWriter(inner, _pg.DataSource))
        {
            var change = new SubstrateChangeBuilder(H("source"), "swap-guard")
                .AddAttestation(Att("s1", "o1", 2, 1_000_000_000L, phi, ts))
                .AddAttestation(Att("s1", "o2", 1, 0L, phi, ts))
                .Build();
            await writer.ApplyAsync(change);
        }

        // The secondary indexes created with the consensus table must remain.
        foreach (var idx in new[]
                 {
                     "consensus_object_btree", "consensus_type_btree",
                     "consensus_subject_type_btree", "consensus_type_subject_btree",
                     "consensus_eff_mu_btree", "consensus_subject_eff_mu_btree",
                 })
        {
            var exists = (bool)(await ScalarAsync(
                "SELECT to_regclass('laplace.' || $1) IS NOT NULL", idx))!;
            Assert.True(exists, $"index {idx} missing after inline fold");
        }

        foreach (var v in new[] { "v_consensus_resolved", "v_consensus_edges", "v_consensus_unrefuted" })
        {
            var exists = (bool)(await ScalarAsync(
                "SELECT to_regclass('laplace.' || $1) IS NOT NULL", v))!;
            Assert.True(exists, $"view {v} missing after inline fold");
        }

        // consensus must remain a registered extension-config (dumpable) table.
        var registered = await ScalarAsync(
            "SELECT EXISTS (SELECT 1 FROM pg_extension e, unnest(e.extconfig) AS c(oid) "
            + "WHERE e.extname = 'laplace_substrate' AND c.oid = 'laplace.consensus'::regclass)");
        Assert.True((bool)registered!, "consensus lost its pg_extension_config_dump registration");

        // And the fold actually produced consensus rows at apply time — nothing deferred.
        var n = (long)(await ScalarAsync("SELECT count(*) FROM laplace.consensus"))!;
        Assert.True(n >= 2, $"expected >=2 consensus edges after inline fold, got {n}");
    }

    // ---- conflict resolution: collocates + table_present_ordinals smoke ----------------
    [Fact]
    public async Task Collocates_And_TablePresentOrdinals_AreCallableWithExpectedShape()
    {
        // collocates now reads FROM v_consensus_unrefuted; proven body-equal to the inline
        // NOT refuted(...) form by the SQL parity harness — assert it is callable/typed.
        await using (var cmd = _pg.DataSource.CreateCommand(
            "SELECT next_word, mu, witnesses FROM laplace.collocates('a', 3)"))
        await using (var rd = await cmd.ExecuteReaderAsync())
        {
            Assert.Equal(3, rd.FieldCount);
        }

        // table_present_ordinals dispatches over the three base tables via UNION ALL helpers.
        var idx = await ScalarAsync(
            "SELECT count(*) FROM laplace.table_present_ordinals('entities', ARRAY[]::bytea[])");
        Assert.Equal(0L, (long)idx!);
    }
}
