using global::Npgsql;
using Laplace.Chess.Service;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Logging.Abstractions;
using NpgsqlTypes;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>
/// Web chess imports share calculated testimony (position outcomes, Syzygy verdicts) with
/// every other applier by content, and <c>laplace_apply_batch</c> deliberately lets apply
/// transactions overlap. These pin the two halves of the Lab import contract: a concurrent
/// landing of the same attestation is re-probed (not a failed job, not a double witness),
/// and the job is journaled in ingest_run_journal / ingest_file_journal like a CLI run.
/// </summary>
[Collection("substrate-pg")]
[Trait("Category", "RequiresDatabase")]
[Trait("Tier", "db")]
public sealed class ChessConcurrentLandingTests(LocalPgFixture pg)
{
    private const long SeedGateKey = 0x4C61706C61636531; // test-only advisory key

    [Fact]
    public async Task ConcurrentLandingOfSharedTestimony_IsReprobedNotFatal()
    {
        string pgn = """
            [Event "Concurrent landing"]
            [Site "Laplace fixture"]
            [Date "2026.10.17"]
            [Round "1"]
            [White "Race white"]
            [Black "Race black"]
            [Result "0-1"]

            1. f3 e6 2. g4 Qh4# 0-1
            """;
        var game = ChessPgnDecomposer.TryParseGame(pgn, requireCompleteSource: true)!;
        Assert.NotNull(game);
        await using var host = await ChessLiveGameHost.CreateAsync(connString: pg.ConnectionString);
        await using var ingestor = await ChessPgnIngestor.AttachAsync(host);
        int offset = 0;
        using var expected = ChessPgnChunk.ComposeNext([game], new HashSet<Hash128> { game.PlayingId },
            ref offset, long.MaxValue);
        var recorded = await expected.Record.BuildAsync();
        var analyzed = await expected.Analyze.BuildAsync();
        try
        {
            var witnesses = recorded.Attestations.Concat(analyzed.Attestations)
                .DistinctBy(row => row.Id).ToArray();
            var reader = new NpgsqlSubstrateReader(pg.DataSource);
            // The shared row: calculated testimony this game composes that no one has landed.
            AttestationRow? target = null;
            foreach (var row in analyzed.Attestations.Where(row => row.SourceId == ChessPositionOutcomes.SourceId).Reverse())
                if (!(await reader.PresentAttestationIdsAsync(row.TypeId, [row.Id])).Any()) { target = row; break; }
            Assert.NotNull(target);

            await using var gate = await pg.DataSource.OpenConnectionAsync();
            await ExecAsync(gate, $"SELECT pg_advisory_lock({SeedGateKey})");
            await InstallSeedGateAsync(target.Id);
            Task seed = Task.CompletedTask;
            Task<ChessPgnIngestor.Result> import = Task.FromResult(default(ChessPgnIngestor.Result));
            try
            {
                // Another applier lands the shared row and is still uncommitted.
                var seedWriter = new ConsensusAccumulatingWriter(
                    new NpgsqlSubstrateWriter(pg.DataSource), pg.DataSource, persistEvidence: true);
                var other = recorded with
                {
                    Entities = [], Physicalities = [], IntentStages = [], Attestations = [target],
                    CountsAsUnit = false,
                };
                seed = Task.Run(async () =>
                {
                    try { await seedWriter.ApplyAsync(other); }
                    finally { await seedWriter.DisposeAsync(); }
                });
                await WaitForBackendAsync(gate, "advisory", seed);

                // The import's presence probe cannot see it; its COPY blocks on the key.
                import = Task.Run(() => ingestor.IngestGamesAsync([pgn], "concurrent-landing"));
                await WaitForBackendAsync(gate, "transactionid", import);
            }
            finally
            {
                await ExecAsync(gate, $"SELECT pg_advisory_unlock({SeedGateKey})");
                try { await seed; }
                finally { await RemoveSeedGateAsync(); }
            }

            // The other applier commits first; the import must re-probe and succeed.
            var result = await import.WaitAsync(TimeSpan.FromMinutes(2));
            Assert.Equal(1, result.Parsed);
            Assert.Equal(1, result.Applied);
            Assert.True(ingestor.ConcurrentLandingRetries >= 1, "the import never raced the uncommitted row");
            foreach (var group in witnesses.GroupBy(row => row.TypeId))
            {
                var ids = group.Select(row => row.Id).ToArray();
                var present = await reader.PresentAttestationIdsAsync(group.Key, ids);
                Assert.Equal(ids.Length, present.Count());
            }
            Assert.Equal(1L, await ScalarAsync<long>(
                "SELECT count(*) FROM laplace.attestations WHERE id = $1", target.Id));
            Assert.Equal(1L, await ScalarAsync<long>(
                "SELECT observation_count::bigint FROM laplace.attestations WHERE id = $1", target.Id));

            var replay = await ingestor.IngestGamesAsync([pgn], "concurrent-landing-replay");
            Assert.Equal(0, replay.Applied);
        }
        finally
        {
            foreach (var change in new[] { recorded, analyzed })
                foreach (var stage in change.IntentStages) stage.Dispose();
        }
    }

    [Fact]
    public async Task LabImportRun_IsJournaledLikeACliRun_IncludingItsFailure()
    {
        var run = await ChessLabIngestRun.StartAsync(
            pg.DataSource, NullLogger.Instance, "journal-test", "chesscom/journal-fixture", CancellationToken.None);
        Assert.NotEqual(Guid.Empty, run.RunId);
        Assert.Equal("running", await RunStatusAsync(run.RunId));

        await run.IngestUnitAsync("2018/07", 2,
            () => Task.FromResult(new ChessPgnIngestor.Result(2, 2, 2, 0, 2, 60)));
        var conflict = new PostgresException("duplicate key value violates unique constraint", "ERROR", "ERROR", "23505");
        await Assert.ThrowsAsync<PostgresException>(() => run.IngestUnitAsync("2018/08", 181,
            () => Task.FromException<ChessPgnIngestor.Result>(conflict)));
        run.Fail(conflict);

        await using var cmd = pg.DataSource.CreateCommand("""
            SELECT source_name, status, error, units_attempted, units_applied, units_failed, files_done
            FROM laplace.ingest_run_journal WHERE run_id = $1
            """);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Uuid, run.RunId);
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            Assert.True(await r.ReadAsync());
            Assert.Equal(ChessLabIngestRun.SourceName, r.GetString(0));
            Assert.Equal("failed", r.GetString(1));
            Assert.Contains("23505", r.GetString(2));
            Assert.Equal(183L, Convert.ToInt64(r.GetValue(3)));
            Assert.Equal(2L, Convert.ToInt64(r.GetValue(4)));
            Assert.Equal(181L, Convert.ToInt64(r.GetValue(5)));
            Assert.Equal(1L, Convert.ToInt64(r.GetValue(6)));
        }

        var files = new Dictionary<string, (string Status, string? Error)>();
        for (int attempt = 0; attempt < 50; attempt++)
        {
            files.Clear();
            await using var fc = pg.DataSource.CreateCommand(
                "SELECT file_label, status, error FROM laplace.ingest_file_journal WHERE run_id = $1");
            fc.Parameters.AddWithValue(NpgsqlDbType.Uuid, run.RunId);
            await using var fr = await fc.ExecuteReaderAsync();
            while (await fr.ReadAsync())
                files[fr.GetString(0)] = (fr.GetString(1), fr.IsDBNull(2) ? null : fr.GetString(2));
            if (files.Count == 2 && files.Values.All(f => f.Status != "running")) break;
            await Task.Delay(100);
        }
        Assert.Equal("ok", files["chesscom/journal-fixture/2018/07"].Status);
        Assert.Equal("failed", files["chesscom/journal-fixture/2018/08"].Status);
        Assert.Contains("23505", files["chesscom/journal-fixture/2018/08"].Error);
    }

    private async Task<string> RunStatusAsync(Guid runId)
    {
        await using var cmd = pg.DataSource.CreateCommand(
            "SELECT status FROM laplace.ingest_run_journal WHERE run_id = $1");
        cmd.Parameters.AddWithValue(NpgsqlDbType.Uuid, runId);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<T> ScalarAsync<T>(string sql, Hash128 id)
    {
        await using var cmd = pg.DataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bytea, id.ToBytes());
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Waits until a backend of this database waits on <paramref name="waitEvent"/>.</summary>
    private static async Task WaitForBackendAsync(NpgsqlConnection conn, string waitEvent, Task work)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (work.IsCompleted)
            {
                await work; // surfaces the failure
                throw new InvalidOperationException($"work completed before blocking on {waitEvent}");
            }
            await using var cmd = new NpgsqlCommand("""
                SELECT count(*) FROM pg_stat_activity
                WHERE datname = current_database() AND pid <> pg_backend_pid()
                  AND wait_event_type = 'Lock' AND wait_event = $1
                """, conn);
            cmd.Parameters.AddWithValue(waitEvent);
            if ((long)(await cmd.ExecuteScalarAsync())! > 0) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"no backend blocked on {waitEvent}");
    }

    private async Task InstallSeedGateAsync(Hash128 id)
    {
        // AFTER ROW: the row and its unique-index entry exist (uncommitted) while the
        // seeding transaction waits on the test's advisory key.
        string hex = Convert.ToHexString(id.ToBytes());
        await using var cmd = pg.DataSource.CreateCommand($"""
            CREATE FUNCTION public.laplace_test_chess_seed_gate()
            RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.id = decode('{hex}', 'hex') THEN
                    PERFORM pg_advisory_xact_lock_shared({SeedGateKey});
                END IF;
                RETURN NULL;
            END $$;
            CREATE TRIGGER laplace_test_chess_seed_gate AFTER INSERT ON laplace.attestations
            FOR EACH ROW EXECUTE FUNCTION public.laplace_test_chess_seed_gate();
            ALTER TABLE laplace.attestations ENABLE ALWAYS TRIGGER laplace_test_chess_seed_gate;
            """);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task RemoveSeedGateAsync()
    {
        await using var cmd = pg.DataSource.CreateCommand("""
            DROP TRIGGER IF EXISTS laplace_test_chess_seed_gate ON laplace.attestations;
            DROP FUNCTION IF EXISTS public.laplace_test_chess_seed_gate();
            """);
        await cmd.ExecuteNonQueryAsync();
    }
}
