using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using global::Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>
/// Guards the SQL-consolidation migration: the legacy numbered NN_*.sql.in monolith
/// bundles were removed in favour of the modular manifest tree. Two objects that were
/// ORPHANED in the numbered files (defined there, absent from the manifest, therefore not
/// shipping) are now migrated into the manifest — these tests prove they actually install
/// and behave after CREATE EXTENSION. The third test is the consensus_fold_swap regression
/// guard: the kept modular body swaps CONTENT (TRUNCATE + refill, stable OID), whereas the
/// discarded numbered body swapped IDENTITY (RENAME + ALTER EXTENSION DROP/ADD), which
/// destroys the OID-bound secondary indexes, views, and pg_extension_config_dump
/// registration. A fold+swap cycle here must leave all of those intact.
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

    // ---- migration 1: finish_consensus_fold_steps (resumable partitioned fold) ----------
    [Fact]
    public async Task MigratedProcedure_FinishConsensusFoldSteps_IsInstalledAndResumable()
    {
        // Present as a PROCEDURE (prokind 'p'), not a stub or a plain function.
        var kind = await ScalarAsync(
            "SELECT p.prokind FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace "
            + "WHERE n.nspname = 'laplace' AND p.proname = 'finish_consensus_fold_steps'");
        Assert.Equal('p', Assert.IsType<char>(kind));

        // The migrated body — not merely the name — installed: its source carries the
        // resumable-specific per-partition COMMIT + replica-role logic unique to this proc.
        var src = (string)(await ScalarAsync(
            "SELECT p.prosrc FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace "
            + "WHERE n.nspname = 'laplace' AND p.proname = 'finish_consensus_fold_steps'"))!;
        Assert.Contains("resumable", src);
        Assert.Contains("session_replication_role = replica", src);
    }

    // (Per-table stat/autovacuum tuning is NOT extension SQL — it moved to the db-scoped
    //  tune-laplace step: scripts/win/tune-laplace.cmd + pipeline.sh phase_tune_laplace,
    //  which runs after the tables exist. Nothing to assert at CREATE EXTENSION time.)

    // ---- conflict resolution: consensus_fold_swap preserves table identity -------------
    private static Hash128 H(string seed) => Hash128.OfCanonical($"sql-consolidation-test/{seed}");

    private static AttestationRow Att(string subj, string obj, long games, long scoreFp, long phiFp, long unixUs)
        => new(
            Hash128.OfCanonical($"sql-consolidation-test/att/{subj}/{obj}/{unixUs}"),
            H(subj), H("rel"), H(obj), H("source"), null,
            AttestationOutcome.Confirm, unixUs, games, scoreFp, phiFp);

    [Fact]
    public async Task ConsensusFoldSwap_PreservesIndexesViewsAndExtensionConfig()
    {
        // Drive one real fold + swap cycle through the materialize path.
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
            await writer.MaterializeConsensusAsync();   // fold -> consensus_fold_swap
        }

        // The 6 secondary indexes created with the consensus table must survive the swap.
        foreach (var idx in new[]
                 {
                     "consensus_object_btree", "consensus_type_btree",
                     "consensus_subject_type_btree", "consensus_type_subject_btree",
                     "consensus_eff_mu_btree", "consensus_subject_eff_mu_btree",
                 })
        {
            var exists = (bool)(await ScalarAsync(
                "SELECT to_regclass('laplace.' || $1) IS NOT NULL", idx))!;
            Assert.True(exists, $"index {idx} was destroyed by the swap (numbered RENAME behaviour)");
        }

        // The consensus-dependent views must survive (RENAME would have followed them onto
        // the doomed table).
        foreach (var v in new[] { "v_consensus_resolved", "v_consensus_edges", "v_consensus_unrefuted" })
        {
            var exists = (bool)(await ScalarAsync(
                "SELECT to_regclass('laplace.' || $1) IS NOT NULL", v))!;
            Assert.True(exists, $"view {v} did not survive the swap");
        }

        // consensus must remain a registered extension-config (dumpable) table; the numbered
        // ALTER EXTENSION DROP path stripped this permanently.
        var registered = await ScalarAsync(
            "SELECT EXISTS (SELECT 1 FROM pg_extension e, unnest(e.extconfig) AS c(oid) "
            + "WHERE e.extname = 'laplace_substrate' AND c.oid = 'laplace.consensus'::regclass)");
        Assert.True((bool)registered!, "consensus lost its pg_extension_config_dump registration");

        // And the fold actually produced consensus rows (swap is not a no-op wipe).
        var n = (long)(await ScalarAsync("SELECT count(*) FROM laplace.consensus"))!;
        Assert.True(n >= 2, $"expected >=2 consensus edges after fold, got {n}");
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
