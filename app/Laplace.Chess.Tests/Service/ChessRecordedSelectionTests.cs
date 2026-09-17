using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessRecordedSelectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(),
        "laplace-recorded-selection-" + Guid.NewGuid().ToString("N"));
    private static readonly CancellationToken Ct = CancellationToken.None;
    private string At(string name) => Path.Combine(_directory, name);
    public ChessRecordedSelectionTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string Hex(Laplace.Engine.Core.Hash128 id) => Convert.ToHexStringLower(id.ToBytes());
    private static string Fixture(int number) => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", $"position-playing-game-{number}.pgn"));

    private async Task<(string Path, string Sha, JsonObject Document)> SelectionAsync(int completeChunks = 1)
    {
        string source = At("source.pgn");
        await File.WriteAllTextAsync(source, Fixture(1) + "\n" + Fixture(2), new UTF8Encoding(false, true));
        var games = PgnGames.StreamGames(source, requireUtf8: true).Select(text =>
            Assert.IsType<ChessGameRecord>(ChessPgnDecomposer.TryParseGame(text, requireCompleteSource: true))).ToArray();
        Assert.Equal(2, games.Length);
        string selected = At("original-selection.jsonl");
        await File.WriteAllTextAsync(selected, string.Concat(games.Select((game, index) =>
            JsonSerializer.Serialize(ChessCorpusPreparation.Describe(index + 1, game),
                ChessCorpusPreparation.Json) + "\n")));
        var evidence = new ChessCorpusEvidence(At("original-failed-fresh"));
        var observedRows = new Dictionary<(short Kind, string Id), ChessRecordingMeasurement.StoredScopeRow>();
        for (int index = 0; index < completeChunks; index++)
        {
            var game = games[index];
            string playing = Hex(game.PlayingId);
            string carrier = Hex(game.LineId);
            string witness = new((char)('a' + index), 32);
            var rows = new ChessRecordingMeasurement.StoredScopeRow[]
            {
                new(1, playing, 0), new(2, carrier, 0), new(3, witness, 1),
            };
            // Repeated game bodies share the same physical line carrier. The second
            // occurrence observes that existing carrier before admitting its new playing.
            var before = rows.Where(row => observedRows.ContainsKey((row.Kind, row.Id)))
                .Select(row => observedRows[(row.Kind, row.Id)]).ToArray();
            var scope = new ChessRecordingMeasurement.ScopeObservation(
                [playing], [carrier], [witness], before, rows, false);
            foreach (var row in rows) observedRows[(row.Kind, row.Id)] = row;
            var body = new ChessRecordingMeasurement.GameIdentity(playing, Hex(game.LineId),
                Hex(game.PositionIds[0]), game.MoveIds.Select(Hex).ToArray(), game.Result.ResultToken,
                null, null, PgnGames.TagStr(game.GameText, "Termination"));
            await evidence.AppendAsync([body], [scope], 1, new()
            {
                ApplyCalls = index + 1, CopyTransactionsStarted = index + 1,
                CopyTransactionsCommitted = index + 1,
            }, Ct);
        }
        Assert.False(evidence.Completed);
        string parent = At("original-failed-receipt.json");
        await File.WriteAllTextAsync(parent, """{"status":"failed","requestedGames":2000,"completed":false}""");
        var chunks = new JsonArray();
        foreach (string line in File.ReadLines(At("original-failed-fresh/chunks.jsonl")))
            chunks.Add(JsonNode.Parse(line));
        var document = new JsonObject
        {
            ["schema"] = "laplace.chess-recorded-selection/v1",
            ["source"] = JsonSerializer.SerializeToNode(await ChessCorpusPreparation.IdentifyAsync(source, Ct),
                ChessCorpusPreparation.Json),
            ["selectionManifest"] = JsonSerializer.SerializeToNode(await ChessCorpusPreparation.IdentifyAsync(selected, Ct),
                ChessCorpusPreparation.Json),
            ["chunks"] = chunks,
        };
        return await WriteManifestAsync(document);
    }

    private async Task<(string Path, string Sha, JsonObject Document)> WriteManifestAsync(JsonObject value)
    {
        string path = At("selection.json");
        await File.WriteAllTextAsync(path, value.ToJsonString());
        var identity = await ChessCorpusPreparation.IdentifyAsync(path, Ct);
        return (path, identity.Sha256, value);
    }

    [Fact]
    public async Task CompletePrefixExcludesUnsealedGamesAndKeepsOriginalSourceIdentity()
    {
        var input = await SelectionAsync();
        var selection = await ChessRecordedSelection.LoadAsync(input.Path, input.Sha, Ct);
        Assert.Equal(1, selection.SelectedGames);
        Assert.Equal(154, selection.Plies);
        Assert.Equal(At("source.pgn"), selection.Source.Path);
        var prepared = ChessCorpusPreparation.FromRecordedSelection(selection);
        Assert.Equal(1, prepared.Selected.Count);
        Assert.Equal(selection.PlayingIds[0].ToBytes(), Convert.FromHexString(prepared.Selected[0].PlayingId));
        var text = Assert.Single(prepared.ReadSelected(Ct));
        var game = Assert.IsType<ChessGameRecord>(
            ChessPgnDecomposer.TryParseGame(text, requireCompleteSource: true));
        prepared.ValidateParsed(0, game);
        Assert.Equal(selection.PlayingIds[0], game.PlayingId);
    }

    [Fact]
    public async Task ImportedBaselinePreservesExactBodiesScopesAndBoundariesWithoutChangingFailedParent()
    {
        var input = await SelectionAsync(completeChunks: 2);
        var selection = await ChessRecordedSelection.LoadAsync(input.Path, input.Sha, Ct);
        Assert.Equal(selection.Chunks[0].Games[0].LineId, selection.Chunks[1].Games[0].LineId);
        Assert.Contains(selection.Chunks[1].Scopes[0].Before,
            row => row.Kind == 2 && row.Id == selection.Chunks[0].Games[0].LineId);
        var parentBefore = await ChessCorpusPreparation.IdentifyAsync(At("original-failed-receipt.json"), Ct);
        var originalBefore = await ChessCorpusPreparation.IdentifyAsync(At("original-failed-fresh/chunks.jsonl"), Ct);
        var baseline = await ChessCorpusEvidence.ImportRecordedSelectionAsync(At("verified-subset"), selection, Ct);
        Assert.True(baseline.Completed);
        Assert.Equal(2, baseline.Summary.ReadbackGames);
        Assert.Equal(2, baseline.Summary.Chunks);
        Assert.NotNull(baseline.Summary.ExactScopeState);
        var replay = new ChessCorpusEvidence(At("replay"), baseline);
        Assert.Equal(1, replay.NextReplayChunkGames);
        foreach (var chunk in selection.Chunks)
        {
            var old = chunk.Scopes[0];
            var same = old with { Before = old.After, Unchanged = true };
            await replay.AppendAsync(chunk.Games, [same], 0, new(), Ct);
        }
        Assert.Equal(0, replay.NextReplayChunkGames);
        await replay.CompleteAsync(Ct);
        Assert.True(replay.Completed);
        await ChessCorpusPreparation.RequireUnchangedAsync(parentBefore, Ct);
        await ChessCorpusPreparation.RequireUnchangedAsync(originalBefore, Ct);
        await selection.VerifyUnchangedAsync(Ct);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("selectionManifest")]
    [InlineData("body")]
    [InlineData("scope")]
    public async Task ChangedRetainedBytesRejectEvenWhenManifestIsUnchanged(string target)
    {
        var input = await SelectionAsync();
        var identity = target is "body" or "scope"
            ? input.Document["chunks"]![0]![target]!
            : input.Document[target]!;
        await File.AppendAllTextAsync(identity["path"]!.GetValue<string>(), "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ChessRecordedSelection.LoadAsync(input.Path, input.Sha, Ct));
    }

    [Theory]
    [InlineData("index")]
    [InlineData("firstSelectedGame")]
    [InlineData("games")]
    [InlineData("novelGames")]
    [InlineData("plies")]
    [InlineData("gameBodiesSha256")]
    public async Task ChangedChunkInventoryRejectsWithoutReadingAnotherPlaying(string field)
    {
        var input = await SelectionAsync();
        var chunk = input.Document["chunks"]![0]!;
        chunk[field] = field == "gameBodiesSha256" ? JsonValue.Create(new string('0', 64))
            : JsonValue.Create(chunk[field]!.GetValue<int>() + 1);
        var changed = await WriteManifestAsync(input.Document);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ChessRecordedSelection.LoadAsync(changed.Path, changed.Sha, Ct));
    }

    [Fact]
    public async Task OriginalSelectionCannotBeReboundToAnotherPlaying()
    {
        var input = await SelectionAsync();
        string path = input.Document["selectionManifest"]!["path"]!.GetValue<string>();
        var lines = await File.ReadAllLinesAsync(path);
        var first = JsonNode.Parse(lines[0])!;
        first["playingId"] = new string('0', 32);
        lines[0] = first.ToJsonString();
        await File.WriteAllLinesAsync(path, lines);
        input.Document["selectionManifest"] = JsonSerializer.SerializeToNode(
            await ChessCorpusPreparation.IdentifyAsync(path, Ct), ChessCorpusPreparation.Json);
        var changed = await WriteManifestAsync(input.Document);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ChessRecordedSelection.LoadAsync(changed.Path, changed.Sha, Ct));
    }

    [Fact]
    public async Task ScopeFileMustMatchTheRetainedChunkScopeNotOnlyItsClaimedFileHash()
    {
        var input = await SelectionAsync();
        var chunk = input.Document["chunks"]![0]!;
        string path = chunk["scope"]!["path"]!.GetValue<string>();
        await File.AppendAllTextAsync(path, "\n");
        chunk["scope"] = JsonSerializer.SerializeToNode(
            await ChessCorpusPreparation.IdentifyAsync(path, Ct), ChessCorpusPreparation.Json);
        var changed = await WriteManifestAsync(input.Document);
        var selection = await ChessRecordedSelection.LoadAsync(changed.Path, changed.Sha, Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ChessCorpusEvidence.ImportRecordedSelectionAsync(At("refused"), selection, Ct));
    }

    [Fact]
    public async Task EmptySelectionAndDuplicatePropertiesReject()
    {
        var input = await SelectionAsync();
        input.Document["chunks"] = new JsonArray();
        var empty = await WriteManifestAsync(input.Document);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ChessRecordedSelection.LoadAsync(empty.Path, empty.Sha, Ct));
        string duplicated = input.Document.ToJsonString();
        duplicated = duplicated[..^1] + ",\"schema\":\"laplace.chess-recorded-selection/v1\"}";
        await File.WriteAllTextAsync(input.Path, duplicated);
        string hash = (await ChessCorpusPreparation.IdentifyAsync(input.Path, Ct)).Sha256;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ChessRecordedSelection.LoadAsync(input.Path, hash, Ct));
    }

    [Fact]
    public async Task ManifestHashAndEveryRetainedInputRemainBoundAfterLoading()
    {
        var input = await SelectionAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ChessRecordedSelection.LoadAsync(input.Path, new string('0', 64), Ct));
        var selection = await ChessRecordedSelection.LoadAsync(input.Path, input.Sha, Ct);
        await File.AppendAllTextAsync(At("source.pgn"), "\n");
        await Assert.ThrowsAsync<InvalidDataException>(() => selection.VerifyUnchangedAsync(Ct));
    }

    [Fact]
    public void ReadOnlyVerificationRefusesRepairsBeforeTheWriter()
    {
        ChessPgnIngestor.RequireWritableAdmission(true, 0);
        ChessPgnIngestor.RequireWritableAdmission(false, 1);
        Assert.Throws<InvalidDataException>(() => ChessPgnIngestor.RequireWritableAdmission(true, 1));
    }


    [Fact]
    public void RetainedScopeExcludesLaterCompletionMetadataButKeepsEveryHistoricalIdentity()
    {
        var game = Assert.IsType<ChessGameRecord>(
            ChessPgnDecomposer.TryParseGame(Fixture(1), requireCompleteSource: true));
        using var historicalBuilder = new SubstrateChangeBuilder(
            ChessVocabulary.PgnSourceId, "retained-scope/control").DeclareSourcePrior(SourceTrust.StructuredCorpus);
        using var currentBuilder = new SubstrateChangeBuilder(
            ChessVocabulary.PgnSourceId, "retained-scope/control").DeclareSourcePrior(SourceTrust.StructuredCorpus);
        ChessPgnDecomposer.RecordGame(game, historicalBuilder);
        ChessPgnDecomposer.RecordGame(game, currentBuilder);
        IngestUnitCompletion.Emit(currentBuilder, game.PlayingId, ChessVocabulary.PgnSourceId, 0);
        var historical = historicalBuilder.Build();
        var current = currentBuilder.Build();
        try
        {
            var completionType = IngestUnitCompletion.RelationTypeId(0);
            Assert.DoesNotContain(historical.Entities, row => row.Id == completionType);
            Assert.Contains(current.Entities, row => row.Id == completionType);
            var entities = ChessRecordingMeasurement.SelectRetainedRows(
                historical.Entities.Select(row => Hex(row.Id)).Distinct().ToArray(),
                current.Entities, row => row.Id, "entity");
            Assert.Equal(historical.Entities.Select(row => row.Id).Distinct(), entities.Select(row => row.Id));
            Assert.DoesNotContain(entities, row => row.Id == completionType);
            var playings = new HashSet<Hash128> { game.PlayingId };
            var witnesses = historical.Attestations
                .Where(row => ChessRecordingMeasurement.IsGameWitness(row, playings)).ToArray();
            Assert.NotEmpty(witnesses);
            var selected = ChessRecordingMeasurement.SelectRetainedRows(
                witnesses.Select(row => Hex(row.Id)).Distinct().ToArray(),
                current.Attestations, row => row.Id, "witness");
            Assert.Equal(witnesses.Select(row => row.Id).Distinct(), selected.Select(row => row.Id));
            var omitted = current.Attestations.Where(row => row.Id != witnesses[0].Id).ToArray();
            Assert.Throws<InvalidDataException>(() => ChessRecordingMeasurement.SelectRetainedRows(
                witnesses.Select(row => Hex(row.Id)).Distinct().ToArray(), omitted, row => row.Id, "witness"));
        }
        finally
        {
            foreach (var change in new[] { historical, current })
                foreach (var stage in change.IntentStages) stage.Dispose();
        }
    }

    [Theory]
    [InlineData("entity")]
    [InlineData("physicality")]
    [InlineData("witness")]
    public void RetainedScopeRejectsMissingDuplicateOrMalformedIdentities(string kind)
    {
        var first = Hash128.FromBytes(Convert.FromHexString(new string('1', 32)));
        var second = Hash128.FromBytes(Convert.FromHexString(new string('2', 32)));
        Hash128[] current = [first, second];
        Assert.Equal(new[] { second, first }, ChessRecordingMeasurement.SelectRetainedRows(
            new[] { Hex(second), Hex(first) }, current, id => id, kind));
        Assert.Throws<InvalidDataException>(() => ChessRecordingMeasurement.SelectRetainedRows(
            new[] { new string('3', 32) }, current, id => id, kind));
        Assert.Throws<InvalidDataException>(() => ChessRecordingMeasurement.SelectRetainedRows(
            new[] { Hex(first), Hex(first) }, current, id => id, kind));
        Assert.Throws<InvalidDataException>(() => ChessRecordingMeasurement.SelectRetainedRows(
            new[] { "invalid" }, current, id => id, kind));
    }

    [Fact]
    public async Task RetainedScopeComesFromTheExactAuthenticatedNextChunk()
    {
        var input = await SelectionAsync(completeChunks: 2);
        var selection = await ChessRecordedSelection.LoadAsync(input.Path, input.Sha, Ct);
        var baseline = await ChessCorpusEvidence.ImportRecordedSelectionAsync(At("baseline"), selection, Ct);
        var replay = new ChessCorpusEvidence(At("scope-replay"), baseline);
        var first = await replay.ReadRetainedScopeAsync(Ct);
        Assert.Equal(selection.Chunks[0].Scopes[0].WitnessIds, first.WitnessIds);
        await replay.AppendAsync(selection.Chunks[0].Games,
            [first with { Before = first.After, Unchanged = true }], 0, new(), Ct);
        var second = await replay.ReadRetainedScopeAsync(Ct);
        Assert.Equal(selection.Chunks[1].Scopes[0].WitnessIds, second.WitnessIds);
        await File.AppendAllTextAsync(Path.Combine(baseline.Summary.Directory, "chunk-0000002.json"), "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => replay.ReadRetainedScopeAsync(Ct));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(29)]
    [InlineData(86401)]
    public void VerificationRejectsUnsupportedDeadlines(int deadline)
    {
        Assert.Throws<ArgumentException>(() => ChessRecordedCorpusVerification.ValidateOptions(
            new(At("selection.json"), new string('a', 64), At("verification"), deadline)));
    }
}
