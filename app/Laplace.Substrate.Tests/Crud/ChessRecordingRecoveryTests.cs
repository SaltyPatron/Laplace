using global::Npgsql;
using Laplace.Chess.Service;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class ChessRecordingRecoveryTests(LocalPgFixture pg)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalGameRecoversEveryLaneAndReplaysWithoutWrites(bool recordOnly)
    {
        string pgn = $"""
            [Event "Recording recovery {recordOnly}"]
            [Site "Laplace fixture"]
            [Date "2026.09.17"]
            [Round "1"]
            [White "Recovery white"]
            [Black "Recovery black"]
            [Result "1-0"]

            1. e4 e5 2. Qh5 Nc6 3. Bc4 Nf6 4. Qxf7# 1-0
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
            var witnessId = ChessPgnDecomposer.RecordingWitnessId(game);
            Assert.Contains(witnesses, row => row.Id == witnessId);
            Assert.Contains(analyzed.Attestations, row => row.SourceId == ChessAnalyze.SourceId);
            Assert.Contains(analyzed.Attestations, row => row.SourceId == ChessTransitions.SourceId);
            Assert.Contains(analyzed.Attestations, row => row.SourceId == ChessPositionOutcomes.SourceId);
            string? acceptedRecord = null;
            if (recordOnly)
            {
                // An earlier record-only ingest is valid source testimony, but it does
                // not prove that the current calculated owners have been accepted.
                await using var seedWriter = new ConsensusAccumulatingWriter(
                    new NpgsqlSubstrateWriter(pg.DataSource), pg.DataSource, persistEvidence: true);
                await seedWriter.ApplyAsync(recorded);
                acceptedRecord = await EvidenceSnapshotAsync(recorded.Attestations.Select(row => row.Id).ToArray());
            }
            else
            {
                // Establish the exact durable carriers independently of COPY lane
                // scheduling. A single-partition apply otherwise rolls E/P back with
                // its failed control transaction, which is also correct behavior.
                foreach (var change in new[] { recorded, analyzed })
                    Assert.All(change.IntentStages, stage => Assert.Equal(0, stage.AttestationCount));
                var carrierWriter = new NpgsqlSubstrateWriter(pg.DataSource);
                await carrierWriter.ApplyManyAsync(
                    [recorded with { Attestations = [] }, analyzed with { Attestations = [] }],
                    CancellationToken.None);
                var beforeFailure = new NpgsqlSubstrateReader(pg.DataSource);
                Assert.True(BitmapBits.IsSet(await beforeFailure.EntitiesExistBitmapAsync([game.PlayingId]), 0));
                Assert.Empty(await beforeFailure.PresentAttestationIdsAsync(
                    ChessVocabulary.PlaysLineType, [witnessId]));

                // Now exercise real control failure and retry against pre-landed
                // content, with unchanged production constraints and canonical IDs.
                await InstallFailureAsync(witnessId);
                try
                {
                    var error = await Assert.ThrowsAsync<PostgresException>(() =>
                        ingestor.IngestGamesAsync([pgn], "recording-recovery-failure"));
                    Assert.Contains("injected chess recording acceptance failure", error.MessageText);
                }
                finally { await RemoveFailureAsync(); }

                var reader = new NpgsqlSubstrateReader(pg.DataSource);
                Assert.True(BitmapBits.IsSet(await reader.EntitiesExistBitmapAsync([game.PlayingId]), 0));
                var markerIds = analyzed.Entities.Where(row =>
                    row.TypeId == ChessVocabulary.AnalysisMarkerType).Select(row => row.Id).ToArray();
                Assert.NotEmpty(markerIds);
                var markerPresence = await reader.EntitiesExistBitmapAsync(markerIds);
                for (int i = 0; i < markerIds.Length; i++)
                    Assert.True(BitmapBits.IsSet(markerPresence, i));

                reader.MarkProven([game.PlayingId], reader.CapturePresenceScope());
                Assert.True(reader.IsProvenPresent(game.PlayingId));
                Assert.Empty(await reader.PresentAttestationIdsAsync(
                    ChessVocabulary.PlaysLineType, [witnessId]));
                var selected = new List<ChessGameRecord>();
                await foreach (var item in ChessPgnDecomposer.FilterNovelAsync(
                    [game], reader, CancellationToken.None)) selected.Add(item);
                Assert.Same(game, Assert.Single(selected));
                selected.Clear();
                await foreach (var item in ChessPgnDecomposer.YieldNovelParsedAsync(
                    [new ChessPlayingPeek(game, game.PlayingId)], reader, false, CancellationToken.None))
                    selected.Add(item);
                Assert.Same(game, Assert.Single(selected));
            }

            var recovered = await ingestor.IngestGamesAsync([pgn], "recording-recovery-retry");
            Assert.Equal(1, recovered.Parsed);
            Assert.Equal(recordOnly ? 0 : 1, recovered.Novel);
            Assert.Equal(recordOnly ? 0 : 1, recovered.Applied);
            var exactReader = new NpgsqlSubstrateReader(pg.DataSource);
            foreach (var group in witnesses.GroupBy(row => row.TypeId))
            {
                var ids = group.Select(row => row.Id).ToArray();
                var present = await exactReader.PresentAttestationIdsAsync(group.Key, ids);
                Assert.Equal(ids.OrderBy(id => id.ToString(), StringComparer.Ordinal),
                    present.OrderBy(id => id.ToString(), StringComparer.Ordinal));
            }
            if (acceptedRecord is not null)
                Assert.Equal(acceptedRecord, await EvidenceSnapshotAsync(
                    recorded.Attestations.Select(row => row.Id).ToArray()));

            var idsBeforeReplay = witnesses.Select(row => row.Id).ToArray();
            string evidenceBefore = await EvidenceSnapshotAsync(idsBeforeReplay);
            string standingBefore = await StandingSnapshotAsync(witnesses);
            long journalBefore = await JournalCountAsync();
            var replay = await ingestor.IngestGamesAsync([pgn], "recording-recovery-replay");
            Assert.Equal(1, replay.Parsed);
            Assert.Equal(0, replay.Novel);
            Assert.Equal(0, replay.Applied);
            Assert.Equal(evidenceBefore, await EvidenceSnapshotAsync(idsBeforeReplay));
            Assert.Equal(standingBefore, await StandingSnapshotAsync(witnesses));
            Assert.Equal(journalBefore, await JournalCountAsync());
            Assert.Equal(game.PlayingId, ChessPgnDecomposer.TryParseGame(pgn)!.PlayingId);
        }
        finally
        {
            foreach (var change in new[] { recorded, analyzed })
                foreach (var stage in change.IntentStages) stage.Dispose();
        }
    }

    private async Task<string> EvidenceSnapshotAsync(IReadOnlyList<Hash128> ids)
    {
        await using var cmd = pg.DataSource.CreateCommand("""
            SELECT COALESCE(jsonb_agg(to_jsonb(a) ORDER BY a.type_id,a.subject_id,a.id), '[]'::jsonb)::text
            FROM laplace.attestations a WHERE a.id = ANY($1::bytea[])
            """);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            ids.Select(id => id.ToBytes()).ToArray());
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<string> StandingSnapshotAsync(IReadOnlyList<AttestationRow> rows)
    {
        await using var cmd = pg.DataSource.CreateCommand("""
            SELECT COALESCE(jsonb_agg(to_jsonb(c) ORDER BY c.type_id,c.subject_id,c.object_id), '[]'::jsonb)::text
            FROM laplace.consensus c
            WHERE c.subject_id = ANY($1::bytea[]) AND c.type_id = ANY($2::bytea[])
            """);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            rows.Select(row => row.SubjectId).Distinct().Select(id => id.ToBytes()).ToArray());
        cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            rows.Select(row => row.TypeId).Distinct().Select(id => id.ToBytes()).ToArray());
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> JournalCountAsync()
    {
        await using var cmd = pg.DataSource.CreateCommand("SELECT count(*) FROM laplace.ingest_flush_journal");
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task InstallFailureAsync(Hash128 id)
    {
        string hex = Convert.ToHexString(id.ToBytes());
        await using var cmd = pg.DataSource.CreateCommand($"""
            CREATE FUNCTION public.laplace_test_chess_recording_failure()
            RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.id = decode('{hex}', 'hex') THEN
                    RAISE EXCEPTION 'injected chess recording acceptance failure';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER laplace_test_chess_recording_failure BEFORE INSERT ON laplace.attestations
            FOR EACH ROW EXECUTE FUNCTION public.laplace_test_chess_recording_failure();
            ALTER TABLE laplace.attestations ENABLE ALWAYS TRIGGER laplace_test_chess_recording_failure;
            """);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task RemoveFailureAsync()
    {
        await using var cmd = pg.DataSource.CreateCommand("""
            DROP TRIGGER IF EXISTS laplace_test_chess_recording_failure ON laplace.attestations;
            DROP FUNCTION IF EXISTS public.laplace_test_chess_recording_failure();
            """);
        await cmd.ExecuteNonQueryAsync();
    }
}
