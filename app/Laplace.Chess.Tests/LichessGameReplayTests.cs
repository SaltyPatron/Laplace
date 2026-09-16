using System.Net;
using System.Text.Json;
using Laplace.Chess.Service;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Tests;

public sealed class LichessGameReplayTests
{
    [Theory]
    [InlineData(200, "Accepted", true)]
    [InlineData(400, "Rejected", false)]
    [InlineData(403, "Rejected", false)]
    [InlineData(408, "Unknown", true)]
    [InlineData(500, "Unknown", true)]
    public async Task HttpDispositionNeverRecordsOrAdvancesTheSubmittedMove(
        int status, string expected, bool pendingExpected)
    {
        var replay = new LichessGameReplay("startpos");
        var written = new List<LichessStreamedPly>();
        await Observe(replay, "", written);
        var before = replay.State;
        var analysis = new ChessLivePlyAnalysis(35, 6, 1234, new[] { "e2e4", "e7e5" });
        var pending = replay.BeginSubmission(Move(replay, "e2e4"), analysis);
        using var handler = new SubmissionHandler(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        using var http = Client(handler);

        var disposition = await LichessBot.SendPostAsync(http, "/api/bot/game/test/move/e2e4", default);
        replay.ResolveSubmission(pending, disposition);

        Assert.Equal(expected, disposition.ToString());
        Assert.Equal(pendingExpected, replay.HasPendingSubmission);
        Assert.Equal(0, replay.AcceptedPlies);
        Assert.Same(before, replay.State);
        Assert.Empty(written);
        Assert.Equal(new[] { "/api/bot/game/test/move/e2e4" }, handler.Paths);

        var observed = await Observe(replay, "e2e4", written);
        Assert.Single(written);
        Assert.Single(observed);
        Assert.Equal(1, replay.AcceptedPlies);
        Assert.False(replay.HasPendingSubmission);
        Assert.Equal("e2e4", written[0].Uci);
        Assert.Equal(pendingExpected ? analysis : null, observed[0].SubmittedAnalysis);
        Assert.Empty(await Observe(replay, "e2e4", written));
        Assert.Single(written);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportFailureOrTimeoutRemainsPendingAcrossClockOnlySnapshots(bool timeout)
    {
        var replay = new LichessGameReplay("startpos");
        var written = new List<LichessStreamedPly>();
        await Observe(replay, "", written);
        var pending = replay.BeginSubmission(Move(replay, "e2e4"), new ChessLivePlyAnalysis(8));
        using var handler = new SubmissionHandler(_ => Task.FromException<HttpResponseMessage>(
            timeout ? new TaskCanceledException("fixture timeout") : new HttpRequestException("fixture disconnect")));
        using var http = Client(handler);

        var disposition = await LichessBot.SendPostAsync(http, "/api/bot/game/test/move/e2e4", default);
        replay.ResolveSubmission(pending, disposition);
        Assert.Equal(LichessSubmissionDisposition.Unknown, disposition);
        for (int clock = 3; clock > 0; clock--)
        {
            using var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                type = "gameState", moves = "", status = "started", wtime = clock * 1000, btime = 5000,
            }));
            Assert.Empty(await replay.ObserveAsync(snapshot.RootElement, Append(written)));
            Assert.True(replay.HasPendingSubmission);
            Assert.Throws<InvalidOperationException>(() =>
                replay.BeginSubmission(Move(replay, "d2d4"), new ChessLivePlyAnalysis()));
        }
        Assert.Single(handler.Paths);
        Assert.Empty(written);
        Assert.Equal(0, replay.AcceptedPlies);
        Assert.Null(replay.Disposition.Outcome);
    }

    [Fact]
    public async Task CallerCancellationIsNotReclassifiedAsServerRejection()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var handler = new SubmissionHandler(token => Task.FromCanceled<HttpResponseMessage>(token));
        using var http = Client(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LichessBot.SendPostAsync(http, "/api/bot/game/test/move/e2e4", cancellation.Token));
    }

    [Fact]
    public async Task RejectedAttemptCanRetryButOldResponseCannotClearNewAttempt()
    {
        var replay = new LichessGameReplay("startpos");
        var written = new List<LichessStreamedPly>();
        await Observe(replay, "", written);
        var first = replay.BeginSubmission(Move(replay, "e2e4"), new ChessLivePlyAnalysis(1));
        replay.ResolveSubmission(first, LichessSubmissionDisposition.Rejected);
        Assert.False(replay.HasPendingSubmission);
        var newerAnalysis = new ChessLivePlyAnalysis(2);
        var second = replay.BeginSubmission(Move(replay, "d2d4"), newerAnalysis);
        replay.ResolveSubmission(first, LichessSubmissionDisposition.Rejected);
        Assert.True(replay.HasPendingSubmission);
        replay.ResolveSubmission(second, LichessSubmissionDisposition.Accepted);
        Assert.Empty(written);

        var observed = await Observe(replay, "d2d4 d7d5", written);
        Assert.Equal(new[] { "d2d4", "d7d5" }, written.Select(p => p.Uci));
        Assert.Same(newerAnalysis, observed[0].SubmittedAnalysis);
        Assert.Null(observed[1].SubmittedAnalysis);
    }

    [Fact]
    public async Task DifferentConfirmedMoveDiscardsPendingAnalysisEvenIfItsUciOccursLater()
    {
        var replay = new LichessGameReplay("startpos");
        var written = new List<LichessStreamedPly>();
        await Observe(replay, "", written);
        var pending = replay.BeginSubmission(Move(replay, "e2e4"), new ChessLivePlyAnalysis(123));
        replay.ResolveSubmission(pending, LichessSubmissionDisposition.Unknown);

        var observed = await Observe(replay, "d2d4 d7d5 e2e4", written);
        Assert.Equal(3, replay.AcceptedPlies);
        Assert.Equal(new[] { 1, 2, 3 }, written.Select(p => p.Ply));
        Assert.All(observed, ply => Assert.Null(ply.SubmittedAnalysis));
        Assert.False(replay.HasPendingSubmission);
    }

    [Fact]
    public async Task DuplicateSnapshotsAndReconnectFullHistoryRecordEachPlyOncePerNewSession()
    {
        var replay = new LichessGameReplay("startpos");
        var written = new List<LichessStreamedPly>();
        await Observe(replay, "e2e4 e7e5", written);
        Assert.Empty(await Observe(replay, "e2e4 e7e5", written));
        await Observe(replay, "e2e4 e7e5 g1f3 b8c6", written);
        Assert.Equal(new[] { 1, 2, 3, 4 }, written.Select(p => p.Ply));

        // The host opens a fresh live session on a new game stream. No old pending
        // search evidence is reconstructed from a POST or attached on reconnect.
        var reopened = new LichessGameReplay("startpos");
        var replayed = new List<LichessStreamedPly>();
        await Observe(reopened, "e2e4 e7e5 g1f3 b8c6", replayed);
        Assert.Equal(written.Select(p => p.Uci), replayed.Select(p => p.Uci));
        var modality = new ChessModality();
        Assert.Equal(modality.StateKey(replay.State), modality.StateKey(reopened.State));
        Assert.All(replayed, ply => Assert.Null(ply.SubmittedAnalysis));
    }

    [Theory]
    [InlineData("e2e4")]
    [InlineData("d2d4 e7e5")]
    [InlineData("e2e4 e7e5 a1a8")]
    public async Task DivergentTruncatedOrIllegalHistoryCannotBecomeCompletedGame(string badMoves)
    {
        var replay = new LichessGameReplay("startpos");
        var written = new List<LichessStreamedPly>();
        await Observe(replay, "e2e4 e7e5", written);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Observe(replay, badMoves, written, "resign", "white"));
        Assert.True(replay.Faulted);
        Assert.Null(replay.Disposition.Outcome);
        Assert.Equal(2, written.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Observe(replay, "e2e4 e7e5", written, "draw"));
        Assert.Equal(2, written.Count);
    }

    [Fact]
    public async Task LateIllegalSuffixKeepsAcceptedPrefixButCannotExposeTheClaimedResult()
    {
        var replay = new LichessGameReplay("startpos");
        var written = new List<LichessStreamedPly>();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Observe(replay, "e2e4 e7e5 a1a8", written, "resign", "white"));
        Assert.Equal(new[] { "e2e4", "e7e5" }, written.Select(p => p.Uci));
        Assert.Equal(2, replay.AcceptedPlies);
        Assert.True(replay.Faulted);
        Assert.Null(replay.Disposition.Outcome);
    }

    [Fact]
    public async Task PartialWriterMutationThenFailurePoisonsSessionAndCannotBeRetriedOrCompleted()
    {
        var replay = new LichessGameReplay("startpos");
        var partialWriter = new List<string>();
        using var snapshot = State("e2e4", "resign", "black");
        Task FailingWriter(LichessStreamedPly ply, CancellationToken _)
        {
            partialWriter.Add(ply.Uci);
            return Task.FromException(new IOException("fixture failure after live-session mutation"));
        }

        await Assert.ThrowsAsync<IOException>(() => replay.ObserveAsync(snapshot.RootElement, FailingWriter));
        Assert.Single(partialWriter);
        Assert.Equal(0, replay.AcceptedPlies);
        Assert.True(replay.Faulted);
        Assert.True(replay.Disposition.Stopped);
        Assert.Null(replay.Disposition.Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            replay.ObserveAsync(snapshot.RootElement, FailingWriter));
        Assert.Single(partialWriter);
        Assert.Throws<InvalidOperationException>(() =>
            replay.BeginSubmission(Move(replay, "e2e4"), new ChessLivePlyAnalysis()));

        var reopened = new LichessGameReplay("startpos");
        var freshWriter = new List<LichessStreamedPly>();
        await Observe(reopened, "e2e4", freshWriter, "resign", "black");
        Assert.Single(freshWriter);
        Assert.Equal(1, reopened.Disposition.Outcome?.Winner);
    }

    [Fact]
    public async Task MateResultIsExposedOnlyAfterEveryAuthoritativePlyIsAppended()
    {
        var replay = new LichessGameReplay("startpos");
        var written = new List<LichessStreamedPly>();
        using var snapshot = State("f2f3 e7e5 g2g4 d8h4", "mate", "black");
        await replay.ObserveAsync(snapshot.RootElement, (ply, _) =>
        {
            Assert.Null(replay.Disposition.Outcome);
            written.Add(ply);
            return Task.CompletedTask;
        });
        Assert.Equal(new[] { "f2f3", "e7e5", "g2g4", "d8h4" }, written.Select(p => p.Uci));
        Assert.Equal(4, replay.AcceptedPlies);
        Assert.Equal(1, replay.Disposition.Outcome?.Winner);
        Assert.True(replay.Disposition.Stopped);
    }

    [Fact]
    public async Task BlackToMoveSetupPreservesActualMoverAndInitialPosition()
    {
        var fen = ChessModality.StartFen.Replace(" w ", " b ", StringComparison.Ordinal);
        var replay = new LichessGameReplay(fen);
        var written = new List<LichessStreamedPly>();
        await Observe(replay, "e7e5", written);
        Assert.False(written[0].Before.Board.WhiteToMove);
        Assert.True(written[0].After.Board.WhiteToMove);
        Assert.Equal(fen, replay.InitialFen);
    }

    [Theory]
    [InlineData("aborted", null)]
    [InlineData("aborted", "white")]
    [InlineData("noStart", null)]
    [InlineData("unknownFinish", null)]
    [InlineData("unknownFinish", "black")]
    [InlineData("futureStatus", "white")]
    [InlineData("mate", null)]
    [InlineData("resign", null)]
    [InlineData("cheat", null)]
    [InlineData("variantEnd", null)]
    [InlineData("draw", "white")]
    [InlineData("stalemate", "black")]
    [InlineData("outoftime", "nobody")]
    public async Task UnscoredOrContradictoryTerminationNeverBecomesDraw(string status, string? winner)
    {
        var replay = new LichessGameReplay("startpos");
        var written = new List<LichessStreamedPly>();
        await Observe(replay, "e2e4 e7e5", written, status, winner);
        Assert.True(replay.Disposition.Stopped);
        Assert.Null(replay.Disposition.Outcome);
        Assert.False(replay.Disposition.CanPlay);
        Assert.Equal(2, written.Count);
    }

    [Theory]
    [InlineData("draw")]
    [InlineData("stalemate")]
    [InlineData("insufficientMaterialClaim")]
    [InlineData("outoftime")]
    [InlineData("timeout")]
    public void GenuineNoWinnerCompletedStatusesRetainDraws(string status)
    {
        using var state = State("", status);
        var disposition = LichessGameReplay.Classify(state.RootElement);
        Assert.True(disposition.Stopped);
        Assert.True(disposition.Outcome?.IsDraw);
    }

    [Theory]
    [InlineData("mate", "white", 0)]
    [InlineData("resign", "black", 1)]
    [InlineData("outoftime", "white", 0)]
    [InlineData("timeout", "black", 1)]
    [InlineData("cheat", "white", 0)]
    [InlineData("noStart", "black", 1)]
    [InlineData("variantEnd", "white", 0)]
    public void ExplicitWinnerOfKnownCompletedStatusIsPreserved(string status, string winner, int side)
    {
        using var state = State("", status, winner);
        var disposition = LichessGameReplay.Classify(state.RootElement);
        Assert.Equal(side, disposition.Outcome?.Winner);
        Assert.True(disposition.Stopped);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"status\":42}")]
    [InlineData("{\"status\":\"outoftime\",\"winner\":42}")]
    [InlineData("{\"status\":\"mate\",\"winner\":\"\"}")]
    public void MissingOrMalformedResultMetadataRemainsUnscored(string json)
    {
        using var state = JsonDocument.Parse(json);
        var disposition = LichessGameReplay.Classify(state.RootElement);
        Assert.Null(disposition.Outcome);
        Assert.True(disposition.Stopped);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"moves\":null,\"status\":\"draw\"}")]
    [InlineData("{\"moves\":42,\"status\":\"draw\"}")]
    public async Task MissingMoveListCannotFabricateAnEmptyCompletedGame(string json)
    {
        var replay = new LichessGameReplay("startpos");
        var written = new List<LichessStreamedPly>();
        using var state = JsonDocument.Parse(json);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            replay.ObserveAsync(state.RootElement, Append(written)));
        Assert.Empty(written);
        Assert.True(replay.Faulted);
        Assert.Null(replay.Disposition.Outcome);
    }

    [Theory]
    [InlineData("created", false)]
    [InlineData("started", true)]
    public void OngoingStatusHasNoResultAndOnlyStartedAllowsPlay(string status, bool canPlay)
    {
        using var state = State("", status);
        var disposition = LichessGameReplay.Classify(state.RootElement);
        Assert.False(disposition.Stopped);
        Assert.Equal(canPlay, disposition.CanPlay);
        Assert.Null(disposition.Outcome);
    }

    private static ChessMove Move(LichessGameReplay replay, string uci)
        => Assert.Single(MoveGen.Legal(replay.State.Board), move => move.ToUci() == uci);

    private static Func<LichessStreamedPly, CancellationToken, Task> Append(List<LichessStreamedPly> written)
        => (ply, _) => { written.Add(ply); return Task.CompletedTask; };

    private static async Task<IReadOnlyList<LichessStreamedPly>> Observe(
        LichessGameReplay replay, string moves, List<LichessStreamedPly> written,
        string status = "started", string? winner = null)
    {
        using var snapshot = State(moves, status, winner);
        return await replay.ObserveAsync(snapshot.RootElement, Append(written));
    }

    private static JsonDocument State(string moves, string status, string? winner = null)
    {
        var fields = new Dictionary<string, object> { ["moves"] = moves, ["status"] = status };
        if (winner is not null) fields["winner"] = winner;
        return JsonDocument.Parse(JsonSerializer.Serialize(fields));
    }

    private static HttpClient Client(HttpMessageHandler handler)
        => new(handler) { BaseAddress = new Uri("https://lichess.invalid") };

    private sealed class SubmissionHandler(Func<CancellationToken, Task<HttpResponseMessage>> response)
        : HttpMessageHandler
    {
        public List<string> Paths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Paths.Add(request.RequestUri!.AbsolutePath);
            return response(cancellationToken);
        }
    }
}
