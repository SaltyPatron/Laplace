using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Laplace.Chess.Service;
using Laplace.Modality.Chess;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.Chess.Tests;

public sealed class LichessGameStreamTests
{
    private const string Path = "/api/bot/game/stream/reconnect-fixture";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EofOrReadFailureResumesSameAcceptedPrefixWithoutDuplicateRecording(bool disconnect)
    {
        using var handler = new ScriptedHandler(
            () => Response(Full("e2e4"), disconnect: disconnect),
            () => Response(Full("e2e4 e7e5") + State("e2e4 e7e5 g1f3", "draw")));
        using var http = Client(handler);
        var replay = new LichessGameReplay("startpos");
        var host = new ChessLiveGameHost(null!, null!, null!);
        var id = ChessLiveGameHost.LichessGameId("reconnect-fixture");
        await host.OpenGameAsync(id, "chess/test/reconnect");
        var scope = host.CaptureGameScope(id);
        Assert.NotNull(scope);
        var written = new List<string>();
        var delays = new List<TimeSpan>();
        var modality = new ChessModality();
        using (scope)
        {
            await foreach (var ev in LichessGameStream.ReadGameAsync(
                http, Path, NullLogger.Instance, default, Wait(delays)))
            {
                await replay.ObserveAsync(GameState(ev), async (ply, token) =>
                {
                    await host.RecordPlyAsync(id, ply.Ply, modality.StateKey(ply.Before),
                        modality.StateKey(ply.After), ply.Uci, null, token);
                    written.Add(ply.Uci);
                });
                if (replay.Disposition.Stopped) break;
            }
            Assert.NotNull(host.CaptureGameScope(id));
            Assert.Equal(new[] { "e2e4", "e7e5", "g1f3" }, written);
            Assert.Equal(3, replay.AcceptedPlies);
            Assert.True(replay.Disposition.Outcome?.IsDraw);
            Assert.Equal(0, host.GamesCompleted); // No persistence/completion is faked by this transport test.
        }
        Assert.Null(host.CaptureGameScope(id));
        Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, delays);
        Assert.Equal(2, handler.Paths.Count);
        Assert.All(handler.Streams, stream => Assert.True(stream.Disposed));
    }

    [Fact]
    public async Task UnresolvedSubmissionSurvivesReconnectAndOnlyExactStreamConfirmationRecordsIt()
    {
        using var handler = new ScriptedHandler(
            () => Response(Full("")),
            () => Response(Full("") + State("e2e4") + State("e2e4 e7e5", "draw")));
        using var http = Client(handler);
        var replay = new LichessGameReplay("startpos");
        var written = new List<LichessStreamedPly>();
        var analysis = new ChessLivePlyAnalysis(23, 4, 321);
        bool submitted = false;
        int snapshotsBeforeConfirmation = 0;
        await foreach (var ev in LichessGameStream.ReadGameAsync(
            http, Path, NullLogger.Instance, default, Wait(new())))
        {
            await replay.ObserveAsync(GameState(ev), (ply, _) =>
            {
                written.Add(ply);
                return Task.CompletedTask;
            });
            if (replay.AcceptedPlies == 0)
            {
                snapshotsBeforeConfirmation++;
                if (!submitted)
                {
                    var move = Assert.Single(MoveGen.Legal(replay.State.Board), m => m.ToUci() == "e2e4");
                    var pending = replay.BeginSubmission(move, analysis);
                    replay.ResolveSubmission(pending, LichessSubmissionDisposition.Unknown);
                    submitted = true;
                }
                else
                {
                    Assert.True(replay.HasPendingSubmission);
                    Assert.Empty(written);
                    Assert.Throws<InvalidOperationException>(() => replay.BeginSubmission(
                        Assert.Single(MoveGen.Legal(replay.State.Board), m => m.ToUci() == "d2d4"),
                        new ChessLivePlyAnalysis()));
                }
            }
            if (replay.Disposition.Stopped) break;
        }
        Assert.Equal(2, snapshotsBeforeConfirmation);
        Assert.Equal(new[] { "e2e4", "e7e5" }, written.Select(p => p.Uci));
        Assert.Same(analysis, written[0].SubmittedAnalysis);
        Assert.Null(written[1].SubmittedAnalysis);
        Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    [Theory]
    [InlineData(408)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task TransientHttpFailureDisposesResponseBeforeRetry(int status)
    {
        using var handler = new ScriptedHandler(
            () => Response("", (HttpStatusCode)status),
            () => Response(Full("", "draw")));
        using var http = Client(handler);
        var delays = new List<TimeSpan>();
        int events = 0;
        await foreach (var _ in LichessGameStream.ReadGameAsync(
            http, Path, NullLogger.Instance, default, Wait(delays)))
        {
            events++;
            break;
        }
        Assert.Equal(1, events);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, delays);
        Assert.Equal(2, handler.Paths.Count);
        Assert.All(handler.Streams, stream => Assert.True(stream.Disposed));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task PermanentHttpFailureEndsUnscoredWithoutRetry(int status)
    {
        using var handler = new ScriptedHandler(() => Response("", (HttpStatusCode)status));
        using var http = Client(handler);
        var replay = new LichessGameReplay("startpos");
        var delays = new List<TimeSpan>();
        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
        {
            await foreach (var ev in LichessGameStream.ReadGameAsync(
                http, Path, NullLogger.Instance, default, Wait(delays)))
                await replay.ObserveAsync(GameState(ev), (_, _) => Task.CompletedTask);
        });
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Empty(delays);
        Assert.Single(handler.Paths);
        Assert.Null(replay.Disposition.Outcome);
        Assert.Equal(0, replay.AcceptedPlies);
        Assert.All(handler.Streams, stream => Assert.True(stream.Disposed));
    }

    [Theory]
    [InlineData(-1, 60)]
    [InlineData(2, 60)]
    [InlineData(95, 95)]
    public async Task RateLimitedStreamHonorsMinimumAndRetryAfter(int seconds, int expected)
    {
        using var handler = new ScriptedHandler(
            () => RateLimited(seconds),
            () => Response(Full("", "draw")));
        using var http = Client(handler);
        var delays = new List<TimeSpan>();
        await foreach (var _ in LichessGameStream.ReadGameAsync(
            http, Path, NullLogger.Instance, default, Wait(delays))) break;
        Assert.Equal(new[] { TimeSpan.FromSeconds(expected) }, delays);
        Assert.Equal(2, handler.Paths.Count);
    }

    [Fact]
    public async Task RetryAfterDateIsRetainedWithoutShorteningTheRequestedWait()
    {
        var retryAt = DateTimeOffset.UtcNow.AddMinutes(3);
        using var handler = new ScriptedHandler(
            () =>
            {
                var response = Response("", HttpStatusCode.ServiceUnavailable);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAt);
                return response;
            },
            () => Response(Full("", "draw")));
        using var http = Client(handler);
        var delays = new List<TimeSpan>();
        await foreach (var _ in LichessGameStream.ReadGameAsync(
            http, Path, NullLogger.Instance, default, Wait(delays))) break;
        Assert.Single(delays);
        Assert.InRange(delays[0].TotalSeconds, 170, 180);
    }

    [Fact]
    public async Task RepeatedImmediateEofUsesCappedExponentialBackoff()
    {
        var responses = Enumerable.Range(0, 8).Select(_ => (Func<HttpResponseMessage>)(() => Response("")))
            .Append(() => Response(Full("", "draw"))).ToArray();
        using var handler = new ScriptedHandler(responses);
        using var http = Client(handler);
        var delays = new List<TimeSpan>();
        await foreach (var _ in LichessGameStream.ReadGameAsync(
            http, Path, NullLogger.Instance, default, Wait(delays))) break;
        Assert.Equal(new double[] { 1, 2, 4, 8, 16, 32, 60, 60 }, delays.Select(d => d.TotalSeconds));
        Assert.Equal(9, handler.Paths.Count);
    }

    [Fact]
    public async Task CancellationDuringBackoffStopsBeforeAnotherConnection()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new ScriptedHandler(() => Response(""));
        using var http = Client(handler);
        Task CancelWait(TimeSpan _, CancellationToken token)
        {
            Assert.All(handler.Streams, stream => Assert.True(stream.Disposed));
            cancellation.Cancel();
            return Task.FromCanceled(token);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in LichessGameStream.ReadGameAsync(
                http, Path, NullLogger.Instance, cancellation.Token, CancelWait))
                Assert.Fail("empty fixture must not emit state");
        });
        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task CancellationInterruptsBlockedStreamReadAndDisposesIt()
    {
        using var cancellation = new CancellationTokenSource();
        var blocked = new BlockingStream();
        using var handler = new ScriptedHandler(() =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ProbeContent(blocked) });
        using var http = Client(handler);
        var consume = Task.Run(async () =>
        {
            await foreach (var _ in LichessGameStream.ReadGameAsync(
                http, Path, NullLogger.Instance, cancellation.Token, Wait(new())))
                Assert.Fail("blocked fixture must not emit state");
        });
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consume.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(blocked.Disposed);
        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task StalledHeadersAreCanceledBeforeOpeningTheReplacementConnection()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bool canceled = false;
        using var handler = new ScriptedHandler(
            async token =>
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { canceled = token.IsCancellationRequested; }
                throw new InvalidOperationException("canceled fixture unexpectedly resumed");
            },
            token => Task.FromResult(Response(Full("", "draw"))));
        using var http = Client(handler);
        var delays = new List<TimeSpan>();
        await foreach (var _ in LichessGameStream.ReadGameAsync(
            http, Path, NullLogger.Instance, lifetime.Token, Wait(delays),
            receiveTimeout: TimeSpan.FromMilliseconds(100), cleanupTimeout: TimeSpan.FromSeconds(1))) break;
        Assert.True(canceled);
        Assert.Equal(2, handler.Paths.Count);
        Assert.Equal(1, handler.MaximumActiveRequests);
        Assert.Equal(0, handler.ActiveRequests);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, delays);
        Assert.All(handler.Streams, stream => Assert.True(stream.Disposed));
    }

    [Fact]
    public async Task StalledNextLineResumesTheAcceptedPrefixAfterCancelAndDisposal()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var blocked = new BlockingAfterPayloadStream(Full("e2e4"));
        using var handler = new ScriptedHandler(
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ProbeContent(blocked) },
            () => Response(Full("e2e4 e7e5", "draw")));
        using var http = Client(handler);
        var replay = new LichessGameReplay("startpos");
        var written = new List<string>();
        var delays = new List<TimeSpan>();
        await foreach (var ev in LichessGameStream.ReadGameAsync(
            http, Path, NullLogger.Instance, lifetime.Token, Wait(delays),
            receiveTimeout: TimeSpan.FromMilliseconds(100), cleanupTimeout: TimeSpan.FromSeconds(1)))
        {
            await replay.ObserveAsync(GameState(ev), (ply, _) =>
            {
                written.Add(ply.Uci);
                return Task.CompletedTask;
            });
            if (replay.Disposition.Stopped) break;
            Assert.Null(replay.Disposition.Outcome);
        }
        Assert.True(blocked.Canceled);
        Assert.True(blocked.Disposed);
        Assert.Equal(new[] { "e2e4", "e7e5" }, written);
        Assert.Equal(2, handler.Paths.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, delays);
    }

    [Fact]
    public async Task BlankHeartbeatsRefreshTheReceiveDeadline()
    {
        var heartbeat = new HeartbeatStream(Full("", "draw"), 6, TimeSpan.FromMilliseconds(200));
        using var handler = new ScriptedHandler(
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ProbeContent(heartbeat) });
        using var http = Client(handler);
        var delays = new List<TimeSpan>();
        var started = System.Diagnostics.Stopwatch.StartNew();
        await foreach (var _ in LichessGameStream.ReadGameAsync(
            http, Path, NullLogger.Instance, default, Wait(delays),
            receiveTimeout: TimeSpan.FromSeconds(1), cleanupTimeout: TimeSpan.FromSeconds(1))) break;
        Assert.True(started.Elapsed >= TimeSpan.FromSeconds(1));
        Assert.Equal(6, heartbeat.HeartbeatsRead);
        Assert.Empty(delays);
        Assert.Single(handler.Paths);
        Assert.True(heartbeat.Disposed);
    }

    [Fact]
    public async Task CallerProcessingTimeDoesNotConsumeTheReceiveDeadline()
    {
        using var handler = new ScriptedHandler(() => Response(Full("") + State("", "draw")));
        using var http = Client(handler);
        var delays = new List<TimeSpan>();
        int events = 0;
        await foreach (var _ in LichessGameStream.ReadGameAsync(
            http, Path, NullLogger.Instance, default, Wait(delays),
            receiveTimeout: TimeSpan.FromMilliseconds(100), cleanupTimeout: TimeSpan.FromSeconds(1)))
        {
            events++;
            if (events == 2) break;
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            Assert.False(Assert.Single(handler.Streams).Disposed);
        }
        Assert.Equal(2, events);
        Assert.Empty(delays);
        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task NoncooperativeReceiveFailsUnscoredWithoutAnOverlappingRequestAndDisposesLateResponse()
    {
        var late = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new ScriptedHandler(_ => late.Task);
        using var http = Client(handler);
        var delays = new List<TimeSpan>();
        var replay = new LichessGameReplay("startpos");
        var response = Response(Full("", "draw"));
        var stream = ((ProbeContent)response.Content).Stream;
        try
        {
            await Assert.ThrowsAsync<LichessStreamCleanupException>(async () =>
            {
                await foreach (var ev in LichessGameStream.ReadGameAsync(
                    http, Path, NullLogger.Instance, default, Wait(delays),
                    receiveTimeout: TimeSpan.FromMilliseconds(50), cleanupTimeout: TimeSpan.FromMilliseconds(50)))
                    await replay.ObserveAsync(GameState(ev), (_, _) => Task.CompletedTask);
            }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(replay.Disposition.Outcome);
            Assert.Equal(0, replay.AcceptedPlies);
            Assert.Single(handler.Paths);
            Assert.Equal(1, handler.ActiveRequests);
            Assert.Equal(1, handler.MaximumActiveRequests);
            Assert.Empty(delays);
        }
        finally { late.TrySetResult(response); }
        await stream.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, handler.ActiveRequests);
        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task AccountOwnerStopsAfterUnsettledReceiveWithoutOpeningAReplacement()
    {
        var late = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new ScriptedHandler(_ => late.Task);
        using var http = Client(handler);
        await using var bot = new LichessBot("fixture-not-a-credential", null!, substrate: false, record: false);
        var account = new LichessAccountReadiness(true, true, true, "fixture-bot");
        var delays = new List<TimeSpan>();
        var response = Response("");
        var stream = ((ProbeContent)response.Content).Stream;
        IAsyncEnumerable<JsonElement> Read(CancellationToken token)
            => LichessBot.ReadStreamAttemptAsync(http, "/api/stream/event", null, NullLogger.Instance, token,
                receiveTimeout: TimeSpan.FromMilliseconds(50), cleanupTimeout: TimeSpan.FromMilliseconds(50));
        try
        {
            await Assert.ThrowsAsync<LichessStreamCleanupException>(() =>
                bot.RunVerifiedAsync(account, 1, default, Read, Wait(delays))).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "/api/stream/event" }, handler.Paths);
            Assert.Equal(1, handler.ActiveRequests);
            Assert.Equal(1, handler.MaximumActiveRequests);
            Assert.Empty(delays);
        }
        finally { late.TrySetResult(response); }
        await stream.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, handler.ActiveRequests);
        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task AccountOwnerStillRetriesOrdinaryMalformedNdjsonAfterDisposal()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var blocked = new BlockingStream();
        using var handler = new ScriptedHandler(
            () => Response("{bad-json}\n"),
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ProbeContent(blocked) });
        using var http = Client(handler);
        await using var bot = new LichessBot("fixture-not-a-credential", null!, substrate: false, record: false);
        var account = new LichessAccountReadiness(true, true, true, "fixture-bot");
        var delays = new List<TimeSpan>();
        IAsyncEnumerable<JsonElement> Read(CancellationToken token)
            => LichessBot.ReadStreamAttemptAsync(http, "/api/stream/event", null, NullLogger.Instance, token);
        var run = bot.RunVerifiedAsync(account, 1, lifetime.Token, Read, Wait(delays));
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lifetime.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, handler.Paths.Count);
        Assert.Single(delays);
        Assert.InRange(delays[0].TotalSeconds, 1, 1.5);
        Assert.All(handler.Streams, value => Assert.True(value.Disposed));
    }

    [Theory]
    [InlineData("wrong-first")]
    [InlineData("malformed")]
    public async Task MissingGameFullOrMalformedJsonCannotBeOmittedBeforeCompletion(string kind)
    {
        var payload = kind == "wrong-first" ? State("", "draw") : Full("") + "{bad-json}\n" + State("", "draw");
        using var handler = new ScriptedHandler(() => Response(payload));
        using var http = Client(handler);
        var replay = new LichessGameReplay("startpos");
        var delays = new List<TimeSpan>();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var ev in LichessGameStream.ReadGameAsync(
                http, Path, NullLogger.Instance, default, Wait(delays)))
                await replay.ObserveAsync(GameState(ev), (_, _) => Task.CompletedTask);
        });
        Assert.Null(replay.Disposition.Outcome);
        Assert.Empty(delays);
        Assert.Single(handler.Paths);
        Assert.All(handler.Streams, stream => Assert.True(stream.Disposed));
    }

    [Fact]
    public async Task WriterFailureIsOutsideTransportRetryEvenWhenItIsAnIOException()
    {
        using var handler = new ScriptedHandler(
            () => Response(Full("e2e4")),
            () => Response(Full("e2e4", "draw")));
        using var http = Client(handler);
        var replay = new LichessGameReplay("startpos");
        int appends = 0;
        var delays = new List<TimeSpan>();
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var ev in LichessGameStream.ReadGameAsync(
                http, Path, NullLogger.Instance, default, Wait(delays)))
                await replay.ObserveAsync(GameState(ev), (_, _) =>
                {
                    appends++;
                    return Task.FromException(new IOException("writer partially mutated then failed"));
                });
        });
        Assert.Equal(1, appends);
        Assert.True(replay.Faulted);
        Assert.Null(replay.Disposition.Outcome);
        Assert.Single(handler.Paths);
        Assert.Empty(delays);
        Assert.All(handler.Streams, stream => Assert.True(stream.Disposed));
    }

    [Fact]
    public async Task MovePost429KeepsAttemptPendingUntilRateWaitFinishes()
    {
        using var handler = new ScriptedHandler(() => RateLimited(95)) { ExpectedMethod = HttpMethod.Post };
        using var http = Client(handler);
        var replay = new LichessGameReplay("startpos");
        using var initial = JsonDocument.Parse(State("").Trim());
        await replay.ObserveAsync(initial.RootElement, (_, _) => Task.CompletedTask);
        var pending = replay.BeginSubmission(
            Assert.Single(MoveGen.Legal(replay.State.Board), m => m.ToUci() == "e2e4"), new ChessLivePlyAnalysis());
        var entered = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Hold(TimeSpan delay, CancellationToken token)
        {
            entered.SetResult(delay);
            await release.Task.WaitAsync(token);
        }
        var send = LichessBot.SendPostAsync(http, "/api/bot/game/test/move/e2e4", default, wait: Hold);
        Assert.Equal(TimeSpan.FromSeconds(95), await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(send.IsCompleted);
        Assert.True(replay.HasPendingSubmission);
        Assert.Equal(0, replay.AcceptedPlies);
        release.SetResult(true);
        replay.ResolveSubmission(pending, await send.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(replay.HasPendingSubmission);
        Assert.Equal(0, replay.AcceptedPlies);
        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task FaultedGameTaskDoesNotEscapeDrainOrCauseFalseDeadlineCancellation()
    {
        using var lifetime = new CancellationTokenSource();
        var failed = Task.FromException(new TimeoutException("game task failed, not drain deadline"));
        await LichessBot.DrainGamesAsync(new[] { failed }, lifetime, TimeSpan.FromSeconds(1), NullLogger.Instance);
        Assert.False(lifetime.IsCancellationRequested);
        Assert.True(failed.IsFaulted);
    }

    [Fact]
    public async Task DrainDeadlineCancelsRemainingGameAndObservesBothFaultAndCancellation()
    {
        using var lifetime = new CancellationTokenSource();
        var canceled = Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token);
        var failed = Task.FromException(new IOException("other game failed"));
        await LichessBot.DrainGamesAsync(new[] { canceled, failed }, lifetime, TimeSpan.Zero, NullLogger.Instance)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(lifetime.IsCancellationRequested);
        Assert.True(canceled.IsCanceled);
        Assert.True(failed.IsFaulted);
    }

    private static JsonElement GameState(JsonElement ev)
        => ev.GetProperty("type").GetString() == "gameFull" ? ev.GetProperty("state") : ev;

    private static string Full(string moves, string status = "started")
        => JsonSerializer.Serialize(new { type = "gameFull", initialFen = "startpos", state = new { moves, status } }) + "\n";

    private static string State(string moves, string status = "started")
        => JsonSerializer.Serialize(new { type = "gameState", moves, status }) + "\n";

    private static Func<TimeSpan, CancellationToken, Task> Wait(List<TimeSpan> delays)
        => (delay, token) => { token.ThrowIfCancellationRequested(); delays.Add(delay); return Task.CompletedTask; };

    private static HttpClient Client(HttpMessageHandler handler)
        => new(handler) { BaseAddress = new Uri("https://lichess.invalid"), Timeout = Timeout.InfiniteTimeSpan };

    private static HttpResponseMessage Response(
        string payload, HttpStatusCode status = HttpStatusCode.OK, bool disconnect = false)
        => new(status) { Content = new ProbeContent(new TrackingStream(payload, disconnect)) };

    private static HttpResponseMessage RateLimited(int seconds)
    {
        var response = Response("", HttpStatusCode.TooManyRequests);
        if (seconds >= 0) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return response;
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _responses;
        public ScriptedHandler(params Func<HttpResponseMessage>[] responses)
            : this(responses.Select(response => (Func<CancellationToken, Task<HttpResponseMessage>>)
                (_ => Task.FromResult(response()))).ToArray()) { }
        public ScriptedHandler(params Func<CancellationToken, Task<HttpResponseMessage>>[] responses)
            => _responses = new(responses);
        public HttpMethod ExpectedMethod { get; init; } = HttpMethod.Get;
        public List<string> Paths { get; } = new();
        public List<HttpMethod> Methods { get; } = new();
        public List<TrackingStream> Streams { get; } = new();
        public int ActiveRequests { get; private set; }
        public int MaximumActiveRequests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(ExpectedMethod, request.Method);
            Assert.All(Streams, stream => Assert.True(stream.Disposed));
            Assert.Equal(0, ActiveRequests);
            Paths.Add(request.RequestUri!.AbsolutePath);
            Methods.Add(request.Method);
            Assert.NotEmpty(_responses);
            ActiveRequests++;
            MaximumActiveRequests = Math.Max(MaximumActiveRequests, ActiveRequests);
            try
            {
                var response = await _responses.Dequeue()(cancellationToken);
                if (response.Content is ProbeContent content) Streams.Add(content.Stream);
                return response;
            }
            finally { ActiveRequests--; }
        }
    }

    private sealed class ProbeContent(TrackingStream stream) : StreamContent(stream)
    {
        public TrackingStream Stream { get; } = stream;
    }

    private class TrackingStream(string payload, bool disconnect = false) : MemoryStream(Encoding.UTF8.GetBytes(payload))
    {
        public bool Disposed { get; private set; }
        public TaskCompletionSource<bool> DisposedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (disconnect && Position == Length)
                return ValueTask.FromException<int>(new IOException("fixture connection lost"));
            return base.ReadAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
            DisposedSignal.TrySetResult(true);
        }
    }

    private sealed class BlockingAfterPayloadStream(string payload) : TrackingStream(payload)
    {
        public bool Canceled { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, cancellationToken);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally { Canceled = cancellationToken.IsCancellationRequested; }
            return 0;
        }
    }

    private sealed class HeartbeatStream(string payload, int count, TimeSpan interval) : TrackingStream(payload)
    {
        public int HeartbeatsRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (HeartbeatsRead < count)
            {
                await Task.Delay(interval, cancellationToken);
                buffer.Span[0] = (byte)'\n';
                HeartbeatsRead++;
                return 1;
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class BlockingStream() : TrackingStream("")
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
