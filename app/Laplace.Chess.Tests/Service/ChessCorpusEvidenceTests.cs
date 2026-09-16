using System.Globalization;
using System.Text;
using System.Text.Json;
using Xunit;
using Counts = Laplace.Chess.Service.ChessRecordingMeasurement.WriterCounts;
using Game = Laplace.Chess.Service.ChessRecordingMeasurement.GameIdentity;
using Observation = Laplace.Chess.Service.ChessRecordingMeasurement.ScopeObservation;
using ScopeRow = Laplace.Chess.Service.ChessRecordingMeasurement.StoredScopeRow;
using MergeRow = Laplace.Chess.Service.ChessCorpusScopeMerge.Row;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessCorpusEvidenceTests : IDisposable
{
    // These are synthetic evidence-transport controls. They exercise real disk
    // retention/folding but provide no PostgreSQL, native chess, or throughput proof.
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "laplace-corpus-evidence-" + Guid.NewGuid().ToString("N"));
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly JsonSerializerOptions Json = ChessCorpusPreparation.Json;

    public ChessCorpusEvidenceTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);
    private string At(string name) => Path.Combine(_root, name);
    private static string Id(int value) => value.ToString("x32", CultureInfo.InvariantCulture);
    private static Game Body(int occurrence) => new(Id(100 + occurrence), Id(10), Id(20),
        [Id(30), Id(31)], "1-0", Id(40), Id(41), "synthetic transport fixture");
    private static ScopeRow[] Rows(long observations) =>
        [new(1, Id(1), 0), new(2, Id(2), 0), new(3, Id(3), observations)];
    private static Observation Scope(IReadOnlyList<ScopeRow> before, IReadOnlyList<ScopeRow> after,
        bool unchanged = false) => new([Id(1)], [Id(2)], [Id(3)], before, after, unchanged);
    private static Counts FreshCounts() => new() { ApplyCalls = 1, CopyTransactionsStarted = 1,
        CopyTransactionsCommitted = 1, EntitiesAttempted = 1, EntitiesInserted = 1, RoundTrips = 1 };

    private async Task<string> WriteRunAsync(string name, params MergeRow[] rows)
    {
        string path = At(name);
        await File.WriteAllTextAsync(path,
            string.Join("\n", rows.Select(row => JsonSerializer.Serialize(row, Json))) + "\n",
            new UTF8Encoding(false, true), Ct);
        return path;
    }

    private async Task<ChessCorpusEvidence> FreshAsync(string name, bool twoChunks = false)
    {
        var value = new ChessCorpusEvidence(At(name));
        await value.AppendAsync([Body(1), Body(2)], [Scope([], Rows(1))], 2, FreshCounts(), Ct);
        if (twoChunks)
            await value.AppendAsync([Body(3)], [Scope(Rows(1), Rows(2))], 1, FreshCounts(), Ct);
        await value.CompleteAsync(Ct);
        return value;
    }

    [Fact]
    public async Task MoreThanFanInFilesUseMultipleDiskPassesAndPreserveExactFinalCounts()
    {
        int chunks = ChessCorpusScopeMerge.FanIn + 3;
        var paths = new List<string>();
        for (int i = 1; i <= chunks; i++)
            paths.Add(await WriteRunAsync($"input-{i:D3}.jsonl",
                new(1, Id(1), i == 1 ? null : 0, 0, i, i),
                new(2, Id(2), i == 1 ? null : 0, 0, i, i),
                new(3, Id(3), i == 1 ? null : i - 1, i, i, i),
                new(3, Id(1000 + i), null, 1, i, i)));

        string directory = At("multi-pass");
        var result = await ChessCorpusScopeMerge.CompleteAsync(paths, directory, Ct);
        Assert.True(File.Exists(Path.Combine(directory, "merge-000-0000001.jsonl")));
        Assert.True(File.Exists(Path.Combine(directory, "merge-001-0000000.jsonl")));
        var states = (await File.ReadAllLinesAsync(result.File.Path, Ct))
            .Select(line => JsonSerializer.Deserialize<ChessCorpusScopeMerge.State>(line, Json)!).ToArray();
        Assert.Equal(chunks + 3, states.Length);
        Assert.Equal((long)states.Length, result.Rows);
        Assert.Equal(new ChessCorpusScopeMerge.State(1, Id(1), 0), states[0]);
        Assert.Equal(new ChessCorpusScopeMerge.State(2, Id(2), 0), states[1]);
        Assert.Equal(new ChessCorpusScopeMerge.State(3, Id(3), chunks), states[2]);
        for (int i = 1; i <= chunks; i++)
            Assert.Equal(new ChessCorpusScopeMerge.State(3, Id(1000 + i), 1), states[i + 2]);
        Assert.Equal(result.File, await ChessCorpusPreparation.IdentifyAsync(result.File.Path, Ct));
    }

    [Theory]
    [InlineData("unobserved-growth")]
    [InlineData("deleted-before")]
    [InlineData("decrease")]
    [InlineData("duplicate-chunk")]
    [InlineData("wrong-identity")]
    [InlineData("entity-observation")]
    public void FoldRejectsUnexplainedGrowthDeletionDecreaseAndOverlappingObservations(string mutation)
    {
        var first = new MergeRow(3, Id(3), null, 1, 1, 1);
        var second = new MergeRow(3, Id(3), 1, 2, 2, 2);
        second = mutation switch
        {
            "unobserved-growth" => second with { Before = 2, After = 3 },
            "deleted-before" => second with { Before = null },
            "decrease" => second with { Before = 2, After = 1 },
            "duplicate-chunk" => second with { FirstChunk = 1, LastChunk = 1 },
            "wrong-identity" => second with { Id = Id(4) },
            _ => second with { Kind = 1, Before = 0, After = 1 },
        };
        Assert.Throws<InvalidDataException>(() => ChessCorpusScopeMerge.Fold([first, second]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndividualDiskRunMustBeSortedAndUnique(bool duplicate)
    {
        var first = new MergeRow(3, Id(4), null, 1, 1, 1);
        var second = first with { Id = duplicate ? Id(4) : Id(3) };
        string input = await WriteRunAsync("invalid-order.jsonl", first, second);
        string output = At("invalid-order-fold");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ChessCorpusScopeMerge.CompleteAsync([input], output, Ct));
        Assert.False(File.Exists(Path.Combine(output, "state.jsonl")));
    }

    [Fact]
    public async Task FreshSharedCountGrowthAndZeroWriteReplayProduceIdenticalAggregateState()
    {
        var fresh = await FreshAsync("fresh", twoChunks: true);
        var replay = new ChessCorpusEvidence(At("replay"), fresh);
        await replay.AppendAsync([Body(1), Body(2)], [Scope(Rows(2), Rows(2), true)], 0, new(), Ct);
        await replay.AppendAsync([Body(3)], [Scope(Rows(2), Rows(2), true)], 0, new(), Ct);
        await replay.CompleteAsync(Ct);

        Assert.True(fresh.Summary.Completed);
        Assert.True(replay.Summary.Completed);
        Assert.Equal(3, fresh.Summary.NewlyRecordedGames);
        Assert.Equal(0, replay.Summary.NewlyRecordedGames);
        Assert.Equal(3, replay.Summary.ReadbackGames);
        Assert.Equal(2, replay.Summary.Chunks);
        var originalState = Assert.IsType<ChessCorpusScopeMerge.Result>(fresh.Summary.ExactScopeState);
        var replayState = Assert.IsType<ChessCorpusScopeMerge.Result>(replay.Summary.ExactScopeState);
        Assert.Equal(originalState.Rows, replayState.Rows);
        Assert.Equal(originalState.File.Bytes, replayState.File.Bytes);
        Assert.Equal(originalState.File.Sha256, replayState.File.Sha256);
        Assert.Equal(await File.ReadAllBytesAsync(originalState.File.Path, Ct),
            await File.ReadAllBytesAsync(replayState.File.Path, Ct));
        Assert.NotNull(replay.Summary.ChunkManifest);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("order")]
    [InlineData("count")]
    [InlineData("novel")]
    [InlineData("ApplyCalls")]
    [InlineData("EntitiesAttempted")]
    [InlineData("EntitiesInserted")]
    [InlineData("PhysicalitiesAttempted")]
    [InlineData("PhysicalitiesInserted")]
    [InlineData("AttestationsAttempted")]
    [InlineData("AttestationsInserted")]
    [InlineData("EntitiesSkippedAtMerge")]
    [InlineData("PhysicalitiesSkippedAtMerge")]
    [InlineData("RoundTrips")]
    [InlineData("CopyTransactionsStarted")]
    [InlineData("CopyTransactionsCommitted")]
    [InlineData("JournalReplayHits")]
    [InlineData("scope-flag")]
    [InlineData("scope-amplified")]
    [InlineData("scope-deleted")]
    [InlineData("scope-duplicate")]
    [InlineData("scope-new-baseline")]
    public async Task ReplayRejectsChangedGameSequenceAnyWriterCounterOrScope(string mutation)
    {
        var fresh = await FreshAsync("fresh");
        var replay = new ChessCorpusEvidence(At("replay"), fresh);
        Game[] games = [Body(1), Body(2)];
        var counts = new Counts();
        var scope = Scope(Rows(1), Rows(1), true);
        int novel = 0;
        switch (mutation)
        {
            case "body": games[0] = games[0] with { MoveIds = [Id(30), Id(32)] }; break;
            case "order": Array.Reverse(games); break;
            case "count": games = games[..1]; break;
            case "novel": novel = 1; break;
            case "scope-flag": scope = scope with { Unchanged = false }; break;
            case "scope-amplified": scope = scope with { After = Rows(2) }; break;
            case "scope-deleted": scope = scope with { After = Rows(1)[..2] }; break;
            case "scope-duplicate": scope = scope with { After = [Rows(1)[0], Rows(1)[1], Rows(1)[2], Rows(1)[2]] }; break;
            case "scope-new-baseline": scope = Scope(Rows(2), Rows(2), true); break;
            default:
                typeof(Counts).GetProperty(mutation)!.SetValue(counts, 1L);
                break;
        }
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await replay.AppendAsync(games, [scope], novel, counts, Ct);
            await replay.CompleteAsync(Ct);
        });
        Assert.False(replay.Summary.Completed);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("unsealed-original")]
    public async Task ReplayMustCoverExactlyTheSealedOriginalChunkSequence(string mutation)
    {
        ChessCorpusEvidence fresh;
        if (mutation == "unsealed-original")
        {
            fresh = new ChessCorpusEvidence(At("fresh"));
            await fresh.AppendAsync([Body(1), Body(2)], [Scope([], Rows(1))], 2, FreshCounts(), Ct);
        }
        else fresh = await FreshAsync("fresh", twoChunks: mutation == "missing");
        var replay = new ChessCorpusEvidence(At("replay"), fresh);
        long count = mutation == "missing" ? 2 : 1;
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await replay.AppendAsync([Body(1), Body(2)], [Scope(Rows(count), Rows(count), true)], 0, new(), Ct);
            if (mutation == "extra")
                await replay.AppendAsync([Body(3)], [Scope(Rows(count), Rows(count), true)], 0, new(), Ct);
            await replay.CompleteAsync(Ct);
        });
        Assert.False(replay.Summary.Completed);
    }

    [Theory]
    [InlineData("chunk-0000001.json")]
    [InlineData("scope-0000001.jsonl")]
    [InlineData("chunks.jsonl")]
    public async Task ChangedUnsealedEvidenceFailsBeforeAnyAggregateIsCreated(string filename)
    {
        string directory = At("fresh");
        var fresh = new ChessCorpusEvidence(directory);
        await fresh.AppendAsync([Body(1), Body(2)], [Scope([], Rows(1))], 2, FreshCounts(), Ct);
        await File.WriteAllTextAsync(Path.Combine(directory, filename), "tampered evidence\n", Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => fresh.CompleteAsync(Ct));
        Assert.False(fresh.Summary.Completed);
        Assert.False(Directory.Exists(Path.Combine(directory, "scope-fold")));
    }

    [Theory]
    [InlineData("chunk-0000001.json")]
    [InlineData("scope-0000001.jsonl")]
    [InlineData("chunks.jsonl")]
    [InlineData("scope-fold/state.jsonl")]
    public async Task ChangedSealedOriginalEvidenceFailsBeforeReplayAggregateIsCreated(string filename)
    {
        var fresh = await FreshAsync("fresh");
        string directory = At("replay");
        var replay = new ChessCorpusEvidence(directory, fresh);
        await replay.AppendAsync([Body(1), Body(2)], [Scope(Rows(1), Rows(1), true)], 0, new(), Ct);
        await File.WriteAllTextAsync(Path.Combine(At("fresh"), filename), "tampered retained original\n", Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => replay.CompleteAsync(Ct));
        Assert.False(replay.Summary.Completed);
        Assert.False(Directory.Exists(Path.Combine(directory, "scope-fold")));
    }
}
