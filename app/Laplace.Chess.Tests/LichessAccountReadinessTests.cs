using System.Net;
using System.Text;
using System.Text.Json;
using Laplace.Chess.Service;
using Xunit;

namespace Laplace.Chess.Tests;

public sealed class LichessAccountReadinessTests
{
    private const string Token = "lip_test-only-secret-do-not-log";
    private const string Bot = """{"id":"laplacebot","username":"LaplaceBot","title":"BOT"}""";

    [Fact]
    public async Task ValidatesBotAccountAndExactScopeUsingOfficialReadOnlyEndpoints()
    {
        using var handler = new ProbeHandler(Bot, TokenResult("challenge:read, bot:play,challenge:write"));
        using var http = Client(handler);

        var result = await LichessAccountReadiness.CheckAsync(http, " " + Token + " ");

        Assert.True(result.Ready);
        Assert.True(result.TokenValid);
        Assert.True(result.BotAccount);
        Assert.True(result.BotPlayScope);
        Assert.Equal("LaplaceBot", result.Username);
        Assert.Null(result.Error);
        Assert.Equal(new[] { "/api/account", "/api/token/test" }, handler.Paths);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task AccountContractIgnoresUnrelatedProfilePayloadAndTokenMetadata()
    {
        const string account = """{"username":"LaplaceBot","title":"BOT","profile":{"bio":"profile content","future":[1,true,null]}}""";
        var response = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [Token] = new { scopes = "bot:play", futureMetadata = new { precise = 9007199254740993L } }
        });
        using var handler = new ProbeHandler(account, response);
        using var http = Client(handler);

        var result = await LichessAccountReadiness.CheckAsync(http, Token);

        Assert.True(result.Ready);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result));
        Assert.DoesNotContain("profile content", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("")]
    [InlineData("board:play")]
    [InlineData("challenge:read,bot:play-extra")]
    [InlineData("BOT:PLAY")]
    public async Task OrdinaryOrSimilarlyNamedScopesDoNotPermitBotPlay(string scopes)
    {
        using var handler = new ProbeHandler(Bot, TokenResult(scopes));
        using var http = Client(handler);

        var result = await LichessAccountReadiness.CheckAsync(http, Token);

        Assert.False(result.Ready);
        Assert.True(result.TokenValid);
        Assert.True(result.BotAccount);
        Assert.False(result.BotPlayScope);
        Assert.Contains("lacks bot:play", result.Error);
    }

    [Theory]
    [InlineData("""{"username":"HumanPlayer"}""")]
    [InlineData("""{"username":"HumanPlayer","title":"GM"}""")]
    [InlineData("""{"username":"HumanPlayer","title":"bot"}""")]
    public async Task AccountMustAlreadyHaveBotTitleAndIsNeverUpgraded(string account)
    {
        using var handler = new ProbeHandler(account, TokenResult("bot:play"));
        using var http = Client(handler);

        var result = await LichessAccountReadiness.CheckAsync(http, Token);

        Assert.False(result.Ready);
        Assert.True(result.TokenValid);
        Assert.False(result.BotAccount);
        Assert.True(result.BotPlayScope);
        Assert.Contains("not a BOT account", result.Error);
        Assert.DoesNotContain(handler.Paths, path => path.Contains("upgrade"));
    }

    [Fact]
    public async Task InvalidAccountTokenStopsBeforeScopeProbeAndDoesNotExposeResponseBody()
    {
        using var handler = new ProbeHandler(Token, "{}", HttpStatusCode.Unauthorized);
        using var http = Client(handler);

        var result = await LichessAccountReadiness.CheckAsync(http, Token);

        Assert.False(result.Ready);
        Assert.False(result.TokenValid);
        Assert.Null(result.BotPlayScope);
        Assert.Contains("HTTP 401", result.Error);
        Assert.Single(handler.Paths);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task TokenRevokedBetweenAccountAndScopeRequestsDoesNotPass()
    {
        using var handler = new ProbeHandler(Bot, JsonSerializer.Serialize(new Dictionary<string, object?> { [Token] = null }));
        using var http = Client(handler);

        var result = await LichessAccountReadiness.CheckAsync(http, Token);

        Assert.False(result.Ready);
        Assert.False(result.TokenValid);
        Assert.Contains("revoked", result.Error);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"username\":null}")]
    [InlineData("{\"username\":42}")]
    [InlineData("null")]
    [InlineData("{\"Username\":\"LaplaceBot\",\"title\":\"BOT\"}")]
    public async Task MissingAccountIdentityDoesNotClaimAuthenticatedReadiness(string account)
    {
        using var handler = new ProbeHandler(account, TokenResult("bot:play"));
        using var http = Client(handler);

        var result = await LichessAccountReadiness.CheckAsync(http, Token);

        Assert.False(result.Ready);
        Assert.Null(result.TokenValid);
        Assert.Single(handler.Paths);
        Assert.Contains("no username", result.Error);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("array")]
    [InlineData("wrong-scope-type")]
    [InlineData("invalid-json")]
    [InlineData("null")]
    [InlineData("missing-scopes")]
    public async Task MalformedTokenResponsesRemainUnknownAndNeverLeakTheToken(string shape)
    {
        var response = shape switch
        {
            "missing" => "{}",
            "array" => "[]",
            "wrong-scope-type" => JsonSerializer.Serialize(new Dictionary<string, object> { [Token] = new { scopes = new[] { "bot:play" } } }),
            "null" => "null",
            "missing-scopes" => JsonSerializer.Serialize(new Dictionary<string, object> { [Token] = new { expires = 0 } }),
            _ => "{\"" + Token + "\": invalid JSON"
        };
        using var handler = new ProbeHandler(Bot, response);
        using var http = Client(handler);

        var result = await LichessAccountReadiness.CheckAsync(http, Token);

        Assert.False(result.Ready);
        Assert.Null(result.BotPlayScope);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "HTTP 403")]
    [InlineData(HttpStatusCode.TooManyRequests, "wait at least one minute")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "HTTP 503")]
    public async Task UpstreamFailuresProduceActionableDiagnosticsWithoutResponseBodies(HttpStatusCode status, string diagnostic)
    {
        using var handler = new ProbeHandler(Bot, Token, tokenStatus: status);
        using var http = Client(handler);

        var result = await LichessAccountReadiness.CheckAsync(http, Token);

        Assert.False(result.Ready);
        Assert.Null(result.BotPlayScope);
        Assert.Contains(diagnostic, result.Error);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task EmptyTokenDoesNotSendARequest()
    {
        using var handler = new ProbeHandler(Bot, TokenResult("bot:play"));
        using var http = Client(handler);

        var result = await LichessAccountReadiness.CheckAsync(http, "  ");

        Assert.False(result.Ready);
        Assert.False(result.TokenValid);
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var handler = new ProbeHandler(Bot, TokenResult("bot:play"));
        using var http = Client(handler);
        using var lifetime = new CancellationTokenSource();
        lifetime.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LichessAccountReadiness.CheckAsync(http, Token, lifetime.Token));
    }

    [Fact]
    public async Task BotCannotOpenEventStreamWithUnreadyAccount()
    {
        var connected = new List<bool>();
        await using var bot = new LichessBot(Token, null!, onConnectionChanged: connected.Add);
        var account = new LichessAccountReadiness(TokenValid: true, BotAccount: false,
            BotPlayScope: true, Username: "HumanPlayer", Error: "Account is not a BOT.");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bot.RunVerifiedAsync(account, 2, CancellationToken.None));

        Assert.Equal(account.Error, error.Message);
        Assert.Empty(connected);
    }

    private static string TokenResult(string scopes) => JsonSerializer.Serialize(
        new Dictionary<string, object> { [Token] = new { userId = "laplacebot", scopes, expires = (long?)null } });

    private static HttpClient Client(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://lichess.org")
    };

    private sealed class ProbeHandler(string account, string tokenInfo,
        HttpStatusCode accountStatus = HttpStatusCode.OK,
        HttpStatusCode tokenStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            Assert.Equal("lichess.org", request.RequestUri.Host);
            Assert.DoesNotContain(Token, request.RequestUri.ToString());
            if (path == "/api/account")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal(Token, request.Headers.Authorization?.Parameter);
                return Response(accountStatus, account);
            }
            Assert.Equal("/api/token/test", path);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("text/plain", request.Content!.Headers.ContentType?.MediaType);
            Assert.Equal(Token, await request.Content.ReadAsStringAsync(ct));
            return Response(tokenStatus, tokenInfo);
        }

        private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
