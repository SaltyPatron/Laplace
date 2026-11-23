using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessHardeningTests
{
    private sealed class FakeChessEngineRuntime(string replyUci, string source = "native-c") : ILaplaceEngineRuntime
    {
        public int ChessCalls { get; private set; }
        public EngineChessRequest? LastChessRequest { get; private set; }

        public EngineTurnResponse Turn(EngineTurnRequest request)
            => throw new NotSupportedException();

        public Task<EngineTurnResponse> TurnAsync(EngineTurnRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public EngineChessResponse ChessPlay(EngineChessRequest request)
        {
            ChessCalls++;
            LastChessRequest = request;
            return new EngineChessResponse(
                StatusCode: 0,
                ScoreCp: 32,
                DepthReached: request.MaxDepth,
                SyzygyWdl: 0,
                FromSyzygy: false,
                NodesSearched: 64,
                WallUs: 500,
                DbProbes: 1,
                PositionId: Hash128.Zero,
                MoveId: Hash128.Zero,
                NextPositionId: Hash128.Zero,
                ClaimId: Hash128.Zero,
                BestUci: replyUci,
                BestSan: replyUci,
                Source: source,
                Error: "");
        }

        public EngineMoldResponse MoldCheckpoint(EngineMoldRequest request)
            => throw new NotSupportedException();

        public void Dispose() { }
    }

    [Fact]
    public async Task PlayBestMoveAsync_UsesInjectedNativeRuntimeWhenLoaded()
    {
        using var engine = new RecordingEngine();
        using var ds = NpgsqlDataSource.Create("Host=localhost;Database=laplace-chess-test-none");
        var host = new ChessLiveGameHost(ds, engine.Recorder());
        var fakeRuntime = new FakeChessEngineRuntime("g1f3");

        await using var svc = new ChessEngineService(0.5d, host, nativeRuntime: () => fakeRuntime);
        var start = await svc.StartPlaySessionAsync(
            recordToSubstrate: false,
            moves: ["e2e4", "e7e5"],
            tenantId: "public",
            userId: null,
            variant: null,
            startFen: null);

        var best = await svc.PlayBestMoveAsync(start.SessionId, start.Fen, depth: 3, substrate: true);

        Assert.Equal("g1f3", best.Uci);
        Assert.Equal("native-c", best.Source);
        Assert.Equal(1, fakeRuntime.ChessCalls);
        Assert.Equal(3, fakeRuntime.LastChessRequest?.MaxDepth);
    }

    [Fact]
    public async Task StartPlaySessionAsync_ComposesStartFenAndMoves_AndRejectsIllegalInputs()
    {
        using var engine = new RecordingEngine();
        using var ds = NpgsqlDataSource.Create("Host=localhost;Database=laplace-chess-test-none");
        var host = new ChessLiveGameHost(ds, engine.Recorder());
        await using var svc = new ChessEngineService(0.5d, host);

        const string customFen = "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq - 0 1";
        var ok = await svc.StartPlaySessionAsync(
            recordToSubstrate: false,
            moves: ["e7e5", "g1f3"],
            tenantId: "public",
            userId: null,
            variant: null,
            startFen: customFen);

        Assert.Equal(2, ok.Ply);
        Assert.Contains("5N2", ok.Fen);

        var badFen = await Assert.ThrowsAsync<ArgumentException>(() => svc.StartPlaySessionAsync(
            recordToSubstrate: false,
            moves: null,
            tenantId: "public",
            userId: null,
            variant: null,
            startFen: "not-a-valid-fen"));
        Assert.Equal("startFen", badFen.ParamName);

        var badMove = await Assert.ThrowsAsync<ArgumentException>(() => svc.StartPlaySessionAsync(
            recordToSubstrate: false,
            moves: ["e7e5", "e1e8"],
            tenantId: "public",
            userId: null,
            variant: null,
            startFen: customFen));
        Assert.Equal("moves", badMove.ParamName);
    }

    [Fact]
    public void ResolveFinishOutcome_ComputesBoardRulesAndRejectsUnknownStatus()
    {
        var modality = new ChessModality();
        var ongoing = modality.Initial();
        var backRankMate = modality.FromFen("R5k1/5ppp/8/8/8/8/8/6K1 b - - 0 1");

        var (mateOutcome, mateAdj) = ChessEngineService.ResolveFinishOutcome(null, backRankMate, modality, null);
        Assert.Equal(GameOutcome.WonBy(0), mateOutcome);
        Assert.False(mateAdj);

        var (incOutcome, incAdj) = ChessEngineService.ResolveFinishOutcome(null, ongoing, modality, null);
        Assert.True(incOutcome.IsUnknown);
        Assert.Equal("*", incOutcome.ResultToken);
        Assert.True(incAdj);

        Assert.Equal(GameOutcome.WonBy(1), ChessEngineService.ResolveFinishOutcome("resign_white", ongoing, modality, null).Outcome);
        Assert.Equal(GameOutcome.WonBy(0), ChessEngineService.ResolveFinishOutcome("resign_black", ongoing, modality, null).Outcome);
        Assert.Equal(GameOutcome.WonBy(1), ChessEngineService.ResolveFinishOutcome("timeout_white", ongoing, modality, null).Outcome);
        Assert.Equal(GameOutcome.WonBy(0), ChessEngineService.ResolveFinishOutcome("timeout_black", ongoing, modality, null).Outcome);
        Assert.Equal(GameOutcome.Draw, ChessEngineService.ResolveFinishOutcome("draw_agreement", ongoing, modality, null).Outcome);
        Assert.True(ChessEngineService.ResolveFinishOutcome("incomplete", ongoing, modality, null).Outcome.IsUnknown);

        var bad = Assert.Throws<ArgumentException>(() =>
            ChessEngineService.ResolveFinishOutcome("bogus_status", ongoing, modality, null));
        Assert.Equal("status", bad.ParamName);
    }

    [Fact]
    public async Task ModalityEngine_And_MatchRunnerAsync_EmitUnknownOutcomeOnPlyCap()
    {
        var modality = new ChessModality();
        var engine = new ModalityEngine<ChessState, ChessMove>(
            modality, Hash128.Zero, new StubAddresser(), new StubRatings());
        var played = await engine.PlayGameAsync(modality.Initial(), temperature: 100d, new Random(11), maxPlies: 2);

        Assert.True(played.Adjudicated);
        Assert.True(played.Outcome.IsUnknown);
        Assert.Equal("*", played.Outcome.ResultToken);
        Assert.All(played.Edges, e => Assert.Equal(PlyOutcome.Neutral, e.MoverOutcome));

        var asyncResult = await MatchRunner.PlayAsync(
            () => MatchRunner.RandomChooser,
            () => MatchRunner.RandomChooser,
            games: 2,
            maxPlies: 2,
            seed: 5,
            concurrency: 1);

        Assert.Equal(2, asyncResult.Games);
        Assert.Equal(2, asyncResult.Incomplete);
        Assert.Equal(0, asyncResult.Draws);
        Assert.EndsWith("*\n\n", ChessPgnWriter.GameToPgn([], -1, ChessModality.StartFen));
    }

    [Fact]
    public void ChessLabConfigValidation_And_AtomicConflictGuard_Work()
    {
        Assert.Throws<ArgumentException>(() => ChessLabRunners.ValidateConfig(
            ChessLabJobKind.SubstrateTest,
            new Dictionary<string, string> { ["games"] = "0" }));
        Assert.Throws<ArgumentException>(() => ChessLabRunners.ValidateConfig(
            ChessLabJobKind.SubstrateTest,
            new Dictionary<string, string> { ["games"] = "abc" }));
        Assert.Throws<ArgumentException>(() => ChessLabRunners.ValidateConfig(
            ChessLabJobKind.SubstrateTest,
            new Dictionary<string, string> { ["depth"] = "0" }));
        Assert.Throws<ArgumentException>(() => ChessLabRunners.ValidateConfig(
            ChessLabJobKind.Cutechess,
            new Dictionary<string, string> { ["rounds"] = "-1" }));

        ChessLabRunners.ValidateConfig(
            ChessLabJobKind.Cutechess,
            new Dictionary<string, string> { ["depth"] = "0", ["rounds"] = "1" });
        ChessLabRunners.ValidateConfig(
            ChessLabJobKind.SubstrateTest,
            new Dictionary<string, string> { ["games"] = "2", ["depth"] = "2", ["concurrency"] = "0" });

        var gate = new TaskCompletionSource<ChessLiveGameHost>();
        using var lab = new ChessLabService(_ => gate.Task);

        Assert.Throws<ArgumentException>(() => lab.StartJob(
            ChessLabJobKind.SubstrateTest,
            new Dictionary<string, string> { ["games"] = "0" }));

        var firstId = lab.StartJob(
            ChessLabJobKind.SubstrateTest,
            new Dictionary<string, string> { ["games"] = "1", ["depth"] = "1", ["maxPlies"] = "2" });
        Assert.NotNull(firstId);

        var conflictingId = lab.StartJob(
            ChessLabJobKind.Cutechess,
            new Dictionary<string, string> { ["rounds"] = "1" });
        Assert.Null(conflictingId);

        lab.StopJob(firstId!);
        gate.TrySetCanceled();
    }

    [Fact]
    public async Task ChessLabEventStream_BroadcastsToMultipleViewers_AndReplaysAfterSeq()
    {
        var stream = new ChessLabEventStream();
        long s1 = stream.Publish(new ChessLabLogEvent("info", "one"));
        long s2 = stream.Publish(new ChessLabLogEvent("info", "two"));

        Assert.Equal([1L, 2L], [s1, s2]);

        var subAll = new List<long>();
        var subAfter = new List<long>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var taskAll = Task.Run(async () =>
        {
            await foreach (var item in stream.ReadAsync(afterSeq: 0, cts.Token))
                subAll.Add(item.Seq);
        }, cts.Token);

        var taskAfter = Task.Run(async () =>
        {
            await foreach (var item in stream.ReadAsync(afterSeq: 1, cts.Token))
                subAfter.Add(item.Seq);
        }, cts.Token);

        await Task.Delay(50, cts.Token);
        stream.Publish(new ChessLabLogEvent("info", "three"));
        stream.Complete();

        await Task.WhenAll(taskAll, taskAfter);
        Assert.Equal([1L, 2L, 3L], subAll);
        Assert.Equal([2L, 3L], subAfter);
    }

    private sealed class StubAddresser : IContentAddresser
    {
        public Hash128 Address(string canonicalSurface)
            => Hash128.Blake3(System.Text.Encoding.UTF8.GetBytes(canonicalSurface));
    }

    private sealed class StubRatings : IEdgeRatings
    {
        public Task<double[]> EffMuAsync(IReadOnlyList<Hash128> edgeIds, CancellationToken ct = default)
            => Task.FromResult(Enumerable.Repeat(GlickoPriors.UnratedEffMu, edgeIds.Count).ToArray());
    }
}
