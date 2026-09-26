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

        await AssertPlayingEnumerationAsync(
            [Hash128.FromBytes(Convert.FromHexString(firstPlaying)),
             Hash128.FromBytes(Convert.FromHexString(secondPlaying))]);
    }

    private async Task AssertPlayingEnumerationAsync(Hash128[] playingIds)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = stop.Token;
        var source = Hash128.OfCanonical("test/chess/playing-enumeration/" + Guid.NewGuid().ToString("N"));
        byte[][] ids = playingIds.Select(id => id.ToBytes()).ToArray();
        byte[][] sources = [source.ToBytes()];
        byte[] playingType = ChessVocabulary.PlayingType.ToBytes();
        byte[] playsLineType = ChessVocabulary.PlaysLineType.ToBytes();
        var lines = await NpgsqlSubstrateReads.AttestationsBySubjectsAndTypeAsync(
            pg.DataSource, ids, playsLineType, ct);
        var byPlaying = lines.GroupBy(row => Hash128.FromBytes(row.SubjectId))
            .ToDictionary(group => group.Key,
                group => Assert.Single(group.Select(row => Hash128.FromBytes(row.ObjectId)).Distinct()));
        Assert.Equal(playingIds.Length, byPlaying.Count);
        Hash128 nonPlaying = byPlaying[playingIds[0]];
        await using (var check = pg.DataSource.CreateCommand("""
            SELECT EXISTS (SELECT FROM laplace.entities
                           WHERE id=$1 AND type_id=$2)
            """))
        {
            check.Parameters.AddWithValue(nonPlaying.ToBytes());
            check.Parameters.AddWithValue(playingType);
            Assert.False((bool)(await check.ExecuteScalarAsync(ct))!);
        }

        // Isolate the count/page scope while retaining the actual complete PGN
        // bodies for ordinary strict hydration. The negative witness has the same
        // source/relation but its subject is a line, not a Playing.
        using var builder = new SubstrateChangeBuilder(source, "test/chess/playing-enumeration")
            .DeclareSourcePrior(SourceTrust.StructuredCorpus)
            .AddEntity(source, EntityTier.Word, BootstrapIntentBuilder.SourceTypeId);
        foreach (var playing in playingIds)
            builder.AddAttestation(NativeAttestation.CategoricalResolved(playing,
                ChessVocabulary.PlaysLineType, byPlaying[playing], source, null, 0.9));
        builder.AddAttestation(NativeAttestation.CategoricalResolved(nonPlaying,
            ChessVocabulary.PlaysLineType, nonPlaying, source, null, 0.9));
        await using (var writer = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(pg.DataSource), pg.DataSource, persistEvidence: true))
            await writer.ApplyAsync(builder.Build(), ct);

        async Task AssertEnumerationAsync()
        {
            Assert.Equal(2L, await NpgsqlSubstrateReads.CountChessEventsWithPlaysLineAsync(
                pg.DataSource, playingType, playsLineType, sources, ct));
            byte[] after = [];
            var observed = new List<Hash128>();
            for (int pageNumber = 0; pageNumber <= playingIds.Length; pageNumber++)
            {
                var page = await NpgsqlSubstrateReads.ChessEventIdPageAsync(
                    pg.DataSource, playingType, playsLineType, sources, after, 1, ct);
                if (pageNumber == playingIds.Length)
                {
                    Assert.Empty(page);
                    break;
                }
                byte[] next = Assert.Single(page);
                observed.Add(Hash128.FromBytes(next));
                after = next;
            }
            Assert.Equal(playingIds.OrderBy(id => id.ToString(), StringComparer.Ordinal),
                observed.OrderBy(id => id.ToString(), StringComparer.Ordinal));
            Assert.DoesNotContain(nonPlaying, observed);
            Assert.Equal(0L, await NpgsqlSubstrateReads.CountChessEventsWithPlaysLineAsync(
                pg.DataSource, playingType, playsLineType, [], ct));
            Assert.Empty(await NpgsqlSubstrateReads.ChessEventIdPageAsync(
                pg.DataSource, playingType, playsLineType, [], [], 1, ct));
        }

        await AssertEnumerationAsync();
        var ordinary = new ChessStartingSideInventory.DatabaseSource(pg.DataSource);
        long? originalCount = await ordinary.CountAsync(ct);
        var before = await ordinary.HydrateAsync(playingIds, 64L * 1024 * 1024, ct);

        await AssertEnumerationAsync();
        Assert.Equal(originalCount, await ordinary.CountAsync(ct));
        var afterHydration = await ordinary.HydrateAsync(playingIds, 64L * 1024 * 1024, ct);
        Assert.Equal(2, afterHydration.Games.Count);
        foreach (var game in afterHydration.Games)
        {
            var expected = Assert.Single(before.Games, item => item.PlayingId == game.PlayingId);
            Assert.Equal(expected.LineId, game.LineId);
            Assert.Equal(expected.StartPositionId, game.StartPositionId);
            Assert.Equal(expected.Result, game.Result);
            Assert.Equal<string>(expected.Moves, game.Moves);
            Assert.Equal<Hash128>(expected.MoveIds, game.MoveIds);
            Assert.Equal(154, game.MoveIds.Count);
            Assert.NotNull(game.AdmittedReplay);
            Assert.Null(game.AdmittedReplay.Truncated);
            Assert.Equal(154, game.AdmittedReplay.Plies.Count);
        }
        Console.WriteLine("CHESS_PLAYING_ENUMERATION counted=2 paged=2 hydrated=2 "
            + "plies_per_game=154 non_playing_excluded=true");
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
