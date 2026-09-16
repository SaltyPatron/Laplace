using Laplace.Chess.Service;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using System.Text.Json;
using NpgsqlTypes;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class ChessPositionPlayingPersistenceTests(LocalPgFixture pg)
{
    private sealed record Evidence(string Id, string Subject, string? Context, long Count, long Score, string ObservedAt);
    private sealed record Standing(string Subject, long Rating, long Rd, long Volatility, long Count);

    [Fact]
    public async Task CompleteDistinctPlayingsFoldOnceAndExactReplayPreservesEvidenceAndStanding()
    {
        var games = new[] { 1, 2 }.Select(i => File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", $"position-playing-game-{i}.pgn"))).ToArray();
        Assert.Equal(2, games.Length);
        await using var host = await ChessLiveGameHost.CreateAsync(connString: pg.ConnectionString);
        await using var ingestor = await ChessPgnIngestor.AttachAsync(host);
        Assert.Empty(await ReadEvidenceAsync());

        var firstResult = await ingestor.IngestGamesAsync([games[0]], "position-playing-first");
        Assert.Equal(1, firstResult.Parsed);
        Assert.Equal(1, firstResult.Novel);
        Assert.True(firstResult.Applied > 0);
        var first = await ReadEvidenceAsync();
        Assert.NotEmpty(first);
        Assert.All(first, row => Assert.NotNull(row.Context));
        string firstPlaying = Assert.Single(first.Select(row => row.Context!).Distinct());
        await AssertPlayingExistsAsync(firstPlaying);
        var firstStanding = await ReadStandingAsync(first.Select(row => row.Subject).Distinct().ToArray());
        Assert.Equal(first.Select(row => row.Subject).Distinct().Count(), firstStanding.Count);

        var secondResult = await ingestor.IngestGamesAsync([games[1]], "position-playing-second");
        Assert.Equal(1, secondResult.Parsed);
        Assert.Equal(1, secondResult.Novel);
        Assert.True(secondResult.Applied > 0);
        var second = await ReadEvidenceAsync();
        var contexts = second.Select(row => row.Context).Distinct().ToArray();
        Assert.Equal(2, contexts.Length);
        Assert.All(contexts, value => Assert.NotNull(value));
        string secondPlaying = Assert.Single(contexts, value => value != firstPlaying)!;
        await AssertPlayingExistsAsync(secondPlaying);
        var firstBySubject = first.ToDictionary(row => row.Subject);
        var secondOnly = second.Where(row => row.Context == secondPlaying).ToDictionary(row => row.Subject);
        Assert.Equal(firstBySubject.Keys.Order(), secondOnly.Keys.Order());
        foreach (var (subject, row) in firstBySubject)
        {
            Assert.NotEqual(row.Id, secondOnly[subject].Id);
            Assert.Equal(row.Count, secondOnly[subject].Count);
            Assert.Equal(row.Score, secondOnly[subject].Score);
        }
        Assert.Equal<Evidence>(first, second.Where(row => row.Context == firstPlaying));
        var secondStanding = await ReadStandingAsync(firstBySubject.Keys.ToArray());
        var priorStanding = firstStanding.ToDictionary(row => row.Subject);
        foreach (var standing in secondStanding)
            Assert.Equal(priorStanding[standing.Subject].Count + secondOnly[standing.Subject].Count, standing.Count);
        Assert.Contains(secondStanding, row =>
            (row.Rating, row.Rd, row.Volatility) !=
            (priorStanding[row.Subject].Rating, priorStanding[row.Subject].Rd, priorStanding[row.Subject].Volatility));

        var replayResult = await ingestor.IngestGamesAsync([games[0]], "position-playing-exact-replay");
        Assert.Equal(1, replayResult.Parsed);
        Assert.Equal(0, replayResult.Novel);
        Assert.Equal(0, replayResult.Applied);
        Assert.Equal<Evidence>(second, await ReadEvidenceAsync());
        Assert.Equal<Standing>(secondStanding, await ReadStandingAsync(firstBySubject.Keys.ToArray()));

        // Reproduce the persisted v2 recipe with the actual writer, after retaining
        // the fresh v3 control above: its null context collapses different playings.
        var substrateReader = new NpgsqlSubstrateReader(pg.DataSource);
        await substrateReader.EvictSourceAsync(ChessPositionOutcomes.SourceId, null,
            [ChessVocabulary.AnalysisMarkerType]);
        Assert.Empty(await ReadEvidenceAsync());
        var legacyBuilder = new SubstrateChangeBuilder(ChessPositionOutcomes.SourceId, "test/chess/position-outcomes/v2");
        foreach (var row in first)
            legacyBuilder.AddAttestation(NativeAttestation.Aggregated(
                Hash128.FromBytes(Convert.FromHexString(row.Subject)), ChessVocabulary.OutcomeType,
                ChessVocabulary.OutcomeObject, ChessPositionOutcomes.SourceId, null,
                row.Count, row.Score, 0.9));
        await using (var legacyWriter = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(pg.DataSource), pg.DataSource, persistEvidence: true))
            await legacyWriter.ApplyAsync(legacyBuilder.Build());
        var legacy = await ReadEvidenceAsync();
        Assert.Equal(first.Count, legacy.Count);
        Assert.All(legacy, row => Assert.Null(row.Context));
        var legacyStanding = await ReadStandingAsync(firstBySubject.Keys.ToArray());

        string evidenceRoot = Environment.GetEnvironmentVariable("LAPLACE_CHESS_OBSERVATION_TEST_DIRECTORY")
            ?? Path.Combine(Path.GetTempPath(), "laplace-chess-position-observation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidenceRoot);
        async Task AssertLegacyPreservedAsync()
        {
            Assert.Equal<Evidence>(legacy, await ReadEvidenceAsync());
            Assert.Equal<Standing>(legacyStanding, await ReadStandingAsync(firstBySubject.Keys.ToArray()));
            Assert.False(File.Exists(Path.Combine(evidenceRoot, "pending.json")));
        }

        // A remaining White/Black header group must not turn a missing result into
        // a draw. Retain and restore the actual provider rows without synthesizing
        // replacement ids, timestamps or fold inputs.
        long retainedLegacyBytes;
        var priorDirectories = Directory.GetDirectories(evidenceRoot).ToHashSet(StringComparer.Ordinal);
        await using (var connection = await pg.DataSource.OpenConnectionAsync())
        {
            await using var remove = new global::Npgsql.NpgsqlBatch(connection);
            var backup = new global::Npgsql.NpgsqlBatchCommand("""
                CREATE TEMP TABLE chess_result_evidence_backup AS
                SELECT a.* FROM laplace.attestations a
                WHERE a.context_id=$1 AND a.type_id=$2 AND a.source_id=ANY($3)
                """);
            backup.Parameters.AddWithValue(Convert.FromHexString(firstPlaying));
            backup.Parameters.AddWithValue(RelationTypeRegistry.RelationTypeId("HAS_RESULT").ToBytes());
            backup.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, new[]
            {
                ChessVocabulary.PgnSourceId.ToBytes(), ChessVocabulary.BookSourceId.ToBytes(), ChessVocabulary.SourceId.ToBytes(),
            });
            remove.BatchCommands.Add(backup);
            remove.BatchCommands.Add(new global::Npgsql.NpgsqlBatchCommand("""
                DELETE FROM laplace.attestations a USING chess_result_evidence_backup saved
                WHERE a.id=saved.id AND a.type_id=saved.type_id AND a.subject_id=saved.subject_id
                """));
            // Keep both statements in one implicit transaction, completed before
            // migration reads through its own connections.
            Assert.True(await remove.ExecuteNonQueryAsync() > 0);
            try
            {
                var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                    ChessPositionOutcomesMigration.RunAsync(pg.DataSource, evidenceRoot, invocationLabel: "missing-result"));
                Assert.Contains("missing recorded result", error.Message);
                await AssertLegacyPreservedAsync();
                string retainedDirectory = Assert.Single(Directory.GetDirectories(evidenceRoot),
                    path => !priorDirectories.Contains(path));
                retainedLegacyBytes = new FileInfo(Path.Combine(retainedDirectory, "source-before.jsonl")).Length;
                Assert.True(retainedLegacyBytes > 0);
            }
            finally
            {
                await using var restore = new global::Npgsql.NpgsqlCommand("""
                    INSERT INTO laplace.attestations SELECT * FROM chess_result_evidence_backup
                    """, connection);
                await restore.ExecuteNonQueryAsync();
            }
        }

        var tinyEnvelope = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ChessPositionOutcomesMigration.RunAsync(pg.DataSource, evidenceRoot,
                maximumRetainedBytes: 65536, invocationLabel: "tiny-retention"));
        Assert.Contains("envelope", tinyEnvelope.Message);
        await AssertLegacyPreservedAsync();
        // Admit the measured legacy archive but leave less than the actual
        // hydration reservation. Refusal must precede pending publication/writes.
        var tinyMaterialization = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ChessPositionOutcomesMigration.RunAsync(pg.DataSource, evidenceRoot,
                maximumRetainedBytes: retainedLegacyBytes + 65536, invocationLabel: "tiny-materialization"));
        Assert.Contains("materialization envelope", tinyMaterialization.Message);
        await AssertLegacyPreservedAsync();

        // A second real canonical line binding is competing recorded evidence.
        // Its object is the witnessed line's actual first board (the valid
        // zero-move line under singleton identity), not an arbitrary made-up id.
        Hash128 playingId = Hash128.FromBytes(Convert.FromHexString(firstPlaying));
        Hash128 playsLine = RelationTypeRegistry.RelationTypeId("PLAYS_LINE");
        var recordedLines = await NpgsqlSubstrateReads.AttestationsBySubjectsAndTypeAsync(
            pg.DataSource, [playingId.ToBytes()], playsLine.ToBytes(), CancellationToken.None);
        string recordedLineHex = Assert.Single(recordedLines.Select(row => Convert.ToHexString(row.ObjectId)).Distinct());
        byte[] recordedLine = Convert.FromHexString(recordedLineHex);
        var recordedManifest = await NpgsqlSubstrateReads.TrajectoryConstituentsAsync(
            pg.DataSource, [recordedLine], CancellationToken.None);
        Hash128 zeroMoveLine = Hash128.FromBytes(recordedManifest.OrderBy(row => row.Ordinal).First().EntityId);
        Assert.NotEqual(Hash128.FromBytes(recordedLine), zeroMoveLine);
        Assert.Empty(await ReadEvidenceIdsAsync(ChessVocabulary.BookSourceId, playsLine));
        var competing = NativeAttestation.CategoricalResolved(playingId, playsLine,
            zeroMoveLine, ChessVocabulary.BookSourceId, null, 0.9);
        var competingBuilder = new SubstrateChangeBuilder(ChessVocabulary.BookSourceId,
            "test/chess/competing-recorded-line");
        competingBuilder.AddAttestation(competing);
        await using (var competingWriter = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(pg.DataSource), pg.DataSource, persistEvidence: true))
            await competingWriter.ApplyAsync(competingBuilder.Build());
        string competingId = Convert.ToHexString(competing.Id.ToBytes()).ToLowerInvariant();
        try
        {
            Assert.Equal<string>([competingId], await ReadEvidenceIdsAsync(ChessVocabulary.BookSourceId, playsLine));
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                ChessPositionOutcomesMigration.RunAsync(pg.DataSource, evidenceRoot, invocationLabel: "competing-line"));
            Assert.Contains("competing PLAYS_LINE", error.Message);
            await AssertLegacyPreservedAsync();
        }
        finally
        {
            // The scope was empty before this control and contains exactly its
            // native attestation. Existing eviction owns the corresponding refold.
            await substrateReader.EvictSourceAsync(ChessVocabulary.BookSourceId, [playsLine], null);
        }
        Assert.Empty(await ReadEvidenceIdsAsync(ChessVocabulary.BookSourceId, playsLine));
        await AssertLegacyPreservedAsync();

        // An actually missing source body must stop before eviction, even though
        // both playings and their result/header witnesses still exist.
        await using (var connection = await pg.DataSource.OpenConnectionAsync())
        {
            await using var remove = new global::Npgsql.NpgsqlBatch(connection);
            var backup = new global::Npgsql.NpgsqlBatchCommand("""
                CREATE TEMP TABLE chess_position_content_backup AS
                SELECT p.* FROM laplace.physicalities p
                WHERE p.type=$1 AND p.entity_id IN (
                    SELECT object_id FROM laplace.attestations WHERE subject_id=$2 AND type_id=$3)
                """);
            backup.Parameters.AddWithValue((short)PhysicalityType.Content);
            backup.Parameters.AddWithValue(Convert.FromHexString(firstPlaying));
            backup.Parameters.AddWithValue(RelationTypeRegistry.RelationTypeId("PLAYS_LINE").ToBytes());
            remove.BatchCommands.Add(backup);
            remove.BatchCommands.Add(new global::Npgsql.NpgsqlBatchCommand("""
                DELETE FROM laplace.physicalities p USING chess_position_content_backup saved WHERE p.id=saved.id
                """));
            // Keep both statements in one implicit transaction, completed before
            // migration reads through its own connections.
            Assert.True(await remove.ExecuteNonQueryAsync() > 0);
            try
            {
                var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                    ChessPositionOutcomesMigration.RunAsync(pg.DataSource, evidenceRoot));
                Assert.Contains("completely reconstructed", error.Message);
                Assert.Equal<Evidence>(legacy, await ReadEvidenceAsync());
                Assert.Equal<Standing>(legacyStanding, await ReadStandingAsync(firstBySubject.Keys.ToArray()));
                Assert.False(File.Exists(Path.Combine(evidenceRoot, "pending.json")));
            }
            finally
            {
                await using var restore = new global::Npgsql.NpgsqlCommand("""
                    INSERT INTO laplace.physicalities
                        (id,entity_id,type,coord,hilbert_index,trajectory,n_constituents,alignment_residual,source_dim,observed_at)
                    SELECT id,entity_id,type,coord,hilbert_index,trajectory,n_constituents,alignment_residual,source_dim,observed_at
                    FROM chess_position_content_backup
                    """, connection);
                await restore.ExecuteNonQueryAsync();
            }
        }

        // Reproduce the old interrupted fixed-name publication boundary. The
        // real pending writer must recover without deleting this retained file.
        string orphanPending = Path.Combine(evidenceRoot, "pending.json.next");
        const string orphanBytes = "interrupted unpublished pending receipt";
        await File.WriteAllTextAsync(orphanPending, orphanBytes);
        var migration = await ChessPositionOutcomesMigration.RunAsync(pg.DataSource, evidenceRoot);
        Assert.Equal("replaced-legacy-observations", migration.Disposition);
        Assert.Equal(2, migration.SelectedPlayings);
        Assert.Equal(2, migration.HydratedPlayings);
        Assert.Equal(2, migration.InputUnitsDone);
        Assert.Equal(legacy.Count, migration.Before.NullContextOutcomeRows);
        Assert.Equal(0, migration.After.NullContextOutcomeRows);
        Assert.Equal(2, migration.After.OutcomeContexts);
        Assert.False(File.Exists(Path.Combine(evidenceRoot, "pending.json")));
        Assert.Equal(orphanBytes, await File.ReadAllTextAsync(orphanPending));
        var restoredEvidence = await ReadEvidenceAsync();
        Assert.Equal(second.Select(RowIdentity), restoredEvidence.Select(RowIdentity));
        var rebuiltStanding = await ReadStandingAsync(firstBySubject.Keys.ToArray());
        Assert.Equal(secondStanding.Select(row => (row.Subject, row.Count)), rebuiltStanding.Select(row => (row.Subject, row.Count)));
        Assert.Contains(rebuiltStanding, row => !legacyStanding.Contains(row));
        var retainedLegacy = File.ReadLines(Path.Combine(migration.Directory, "source-before.jsonl"))
            .Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Equal(legacy.Count, retainedLegacy.Count(document =>
                document.RootElement.GetProperty("section").GetString() == "source_evidence"));
        }
        finally { foreach (var document in retainedLegacy) document.Dispose(); }
        var replayAfterMigration = await ingestor.IngestGamesAsync([games[0]], "position-playing-after-migration");
        Assert.Equal(0, replayAfterMigration.Applied);
        Assert.Equal<Evidence>(restoredEvidence, await ReadEvidenceAsync());
        Assert.Equal<Standing>(rebuiltStanding, await ReadStandingAsync(firstBySubject.Keys.ToArray()));
        var repeatedMigration = await ChessPositionOutcomesMigration.RunAsync(pg.DataSource, evidenceRoot);
        Assert.Equal("legacy-absent-verified", repeatedMigration.Disposition);
        Assert.Equal(0, repeatedMigration.InputUnitsDone);
        Assert.Equal<Evidence>(restoredEvidence, await ReadEvidenceAsync());
        Assert.Equal<Standing>(rebuiltStanding, await ReadStandingAsync(firstBySubject.Keys.ToArray()));

        // Restore the actual durable pending receipt to model an interrupted
        // completion acknowledgement. Its original archive remains authoritative.
        // Resume must evict/refold and rederive, rather than count these games twice.
        string completedPending = Path.Combine(migration.Directory, "completed-pending.json");
        Assert.True(File.Exists(completedPending));
        File.Copy(completedPending, Path.Combine(evidenceRoot, "pending.json"));
        var resumedMigration = await ChessPositionOutcomesMigration.RunAsync(pg.DataSource, evidenceRoot,
            invocationLabel: "resumed-transition");
        Assert.Equal("rebuilt-incomplete-observations", resumedMigration.Disposition);
        Assert.Equal(2, resumedMigration.SelectedPlayings);
        Assert.Equal(2, resumedMigration.HydratedPlayings);
        Assert.Equal(2, resumedMigration.InputUnitsDone);
        Assert.Equal(0, resumedMigration.Before.NullContextOutcomeRows);
        Assert.Equal(2, resumedMigration.Before.OutcomeContexts);
        Assert.Equal(2, resumedMigration.After.OutcomeContexts);
        Assert.False(File.Exists(Path.Combine(evidenceRoot, "pending.json")));
        Assert.Equal(orphanBytes, await File.ReadAllTextAsync(orphanPending));
        var resumedEvidence = await ReadEvidenceAsync();
        Assert.Equal(restoredEvidence.Select(RowIdentity), resumedEvidence.Select(RowIdentity));
        var resumedStanding = await ReadStandingAsync(firstBySubject.Keys.ToArray());
        Assert.Equal(rebuiltStanding.Select(row => (row.Subject, row.Count)),
            resumedStanding.Select(row => (row.Subject, row.Count)));
        var replayAfterResume = await ingestor.IngestGamesAsync([games[0]], "position-playing-after-resume");
        Assert.Equal(0, replayAfterResume.Applied);
        Assert.Equal<Evidence>(resumedEvidence, await ReadEvidenceAsync());
        Assert.Equal<Standing>(resumedStanding, await ReadStandingAsync(firstBySubject.Keys.ToArray()));

        // A count of two contexts plus two existing markers is insufficient:
        // replace one playing's evidence with a valid witness on a non-playing
        // context, preserving that count and every current marker.
        await using (var remove = pg.DataSource.CreateCommand("""
            DELETE FROM laplace.attestations
            WHERE source_id=$1 AND type_id=$2 AND object_id=$3 AND context_id=$4
            """))
        {
            remove.Parameters.AddWithValue(ChessPositionOutcomes.SourceId.ToBytes());
            remove.Parameters.AddWithValue(ChessVocabulary.OutcomeType.ToBytes());
            remove.Parameters.AddWithValue(ChessVocabulary.OutcomeObject.ToBytes());
            remove.Parameters.AddWithValue(Convert.FromHexString(secondPlaying));
            Assert.Equal(secondOnly.Count, await remove.ExecuteNonQueryAsync());
        }
        Hash128 nonPlayingContext = Hash128.FromBytes(recordedLine);
        await using (var check = pg.DataSource.CreateCommand(
            "SELECT EXISTS(SELECT FROM laplace.entities WHERE id=$1 AND type_id=$2)"))
        {
            check.Parameters.AddWithValue(recordedLine);
            check.Parameters.AddWithValue(ChessVocabulary.PlayingType.ToBytes());
            Assert.False((bool)(await check.ExecuteScalarAsync())!);
        }
        var substitution = NativeAttestation.Aggregated(
            Hash128.FromBytes(Convert.FromHexString(first[0].Subject)), ChessVocabulary.OutcomeType,
            ChessVocabulary.OutcomeObject, ChessPositionOutcomes.SourceId, nonPlayingContext, 1, 0, 0.9);
        var substitutionBuilder = new SubstrateChangeBuilder(ChessPositionOutcomes.SourceId,
            "test/chess/non-playing-context-substitution");
        substitutionBuilder.AddAttestation(substitution);
        await using (var substitutionWriter = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(pg.DataSource), pg.DataSource, persistEvidence: true))
            await substitutionWriter.ApplyAsync(substitutionBuilder.Build());
        var substitutedEvidence = await ReadEvidenceAsync();
        Assert.Equal(2, substitutedEvidence.Select(row => row.Context).Distinct().Count());
        Assert.DoesNotContain(substitutedEvidence, row => row.Context == secondPlaying);
        Assert.Contains(substitutedEvidence, row => row.Context == Convert.ToHexString(recordedLine).ToLowerInvariant());
        var markerBitmap = await substrateReader.EntitiesExistBitmapAsync(
            [ChessPositionOutcomes.MarkerId(playingId),
                ChessPositionOutcomes.MarkerId(Hash128.FromBytes(Convert.FromHexString(secondPlaying)))]);
        Assert.True(BitmapBits.IsSet(markerBitmap, 0));
        Assert.True(BitmapBits.IsSet(markerBitmap, 1));
        var repairedSubstitution = await ChessPositionOutcomesMigration.RunAsync(pg.DataSource, evidenceRoot,
            invocationLabel: "context-substitution");
        Assert.Equal("rebuilt-incomplete-observations", repairedSubstitution.Disposition);
        Assert.Equal(2, repairedSubstitution.InputUnitsDone);
        Assert.Equal(2, repairedSubstitution.Before.OutcomeContexts);
        Assert.Equal(0, repairedSubstitution.Before.NullContextOutcomeRows);
        Assert.Equal(2, repairedSubstitution.After.OutcomeContexts);
        var repairedEvidence = await ReadEvidenceAsync();
        Assert.Equal(restoredEvidence.Select(RowIdentity), repairedEvidence.Select(RowIdentity));
        var repairedStanding = await ReadStandingAsync(firstBySubject.Keys.ToArray());
        Assert.Equal(rebuiltStanding.Select(row => (row.Subject, row.Count)),
            repairedStanding.Select(row => (row.Subject, row.Count)));
        var replayAfterSubstitution = await ingestor.IngestGamesAsync([games[1]], "position-playing-after-context-repair");
        Assert.Equal(0, replayAfterSubstitution.Applied);
        Assert.Equal<Evidence>(repairedEvidence, await ReadEvidenceAsync());
        Assert.Equal<Standing>(repairedStanding, await ReadStandingAsync(firstBySubject.Keys.ToArray()));
        Console.WriteLine($"CHESS_POSITION_PLAYING_OBSERVATION complete_games=2 plies_per_game=154 "
            + $"distinct_playings=2 shared_subjects={firstBySubject.Count} "
            + $"first_observations={first.Sum(row => row.Count)} "
            + $"total_observations={second.Sum(row => row.Count)} replay_applied=0 "
            + "missing_result_preserved=true competing_line_preserved=true "
            + "resource_refusal_preserved=true resumed_without_double_observation=true "
            + "context_substitution_rebuilt=true");
    }

    private static object RowIdentity(Evidence row) => (row.Id, row.Subject, row.Context, row.Count, row.Score);

    private async Task<List<string>> ReadEvidenceIdsAsync(Hash128 source, Hash128 type)
    {
        await using var command = pg.DataSource.CreateCommand(
            "SELECT encode(id,'hex') FROM laplace.attestations WHERE source_id=$1 AND type_id=$2 ORDER BY id");
        command.Parameters.AddWithValue(source.ToBytes());
        command.Parameters.AddWithValue(type.ToBytes());
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
        return ids;
    }

    private async Task<List<Evidence>> ReadEvidenceAsync()
    {
        await using var command = pg.DataSource.CreateCommand(
            "SELECT encode(id,'hex'),encode(subject_id,'hex'),encode(context_id,'hex'),"
            + "observation_count,sum_score_fp1e9,last_observed_at::text FROM laplace.attestations "
            + "WHERE source_id=$1 AND type_id=$2 AND object_id=$3 ORDER BY id");
        command.Parameters.AddWithValue(ChessPositionOutcomes.SourceId.ToBytes());
        command.Parameters.AddWithValue(ChessVocabulary.OutcomeType.ToBytes());
        command.Parameters.AddWithValue(ChessVocabulary.OutcomeObject.ToBytes());
        var rows = new List<Evidence>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3), reader.GetInt64(4), reader.GetString(5)));
        return rows;
    }

    private async Task<List<Standing>> ReadStandingAsync(string[] subjects)
    {
        await using var command = pg.DataSource.CreateCommand(
            "SELECT encode(subject_id,'hex'),rating,rd,volatility,witness_count FROM laplace.consensus "
            + "WHERE type_id=$1 AND object_id=$2 AND subject_id=ANY($3) ORDER BY subject_id");
        command.Parameters.AddWithValue(ChessVocabulary.OutcomeType.ToBytes());
        command.Parameters.AddWithValue(ChessVocabulary.OutcomeObject.ToBytes());
        command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            subjects.Select(Convert.FromHexString).ToArray());
        var rows = new List<Standing>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)));
        return rows;
    }

    private async Task AssertPlayingExistsAsync(string id)
    {
        await using var command = pg.DataSource.CreateCommand(
            "SELECT EXISTS(SELECT FROM laplace.entities WHERE id=$1 AND type_id=$2)");
        command.Parameters.AddWithValue(Convert.FromHexString(id));
        command.Parameters.AddWithValue(ChessVocabulary.PlayingType.ToBytes());
        Assert.True((bool)(await command.ExecuteScalarAsync())!);
    }
}
