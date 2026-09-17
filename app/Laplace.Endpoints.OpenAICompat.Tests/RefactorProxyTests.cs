using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class RefactorProxyTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public void RegisteredClientCannotFollowRedirectsOrReuseCookiesOrEnvironmentProxy()
    {
        using var services = new ServiceCollection().AddRefactorProxy().BuildServiceProvider();
        var clients = services.GetRequiredService<IHttpClientFactory>();
        using var client = clients.CreateClient(RefactorProxyMiddleware.ClientName);
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
        var handler = services.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(RefactorProxyMiddleware.ClientName);
        while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler!;
        var sockets = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(sockets.AllowAutoRedirect);
        Assert.False(sockets.UseCookies);
        Assert.False(sockets.UseProxy);
        Assert.Equal(DecompressionMethods.None, sockets.AutomaticDecompression);
        Assert.Equal(TimeSpan.FromSeconds(10), sockets.ConnectTimeout);
    }

    [Fact]
    public async Task ExactPrefixRedirectsPermanentlyWithQuery_AndOtherPathsStayInOriginal()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Must not forward."));
        using var proxy = Proxy(handler);
        using var client = proxy.CreateClient();
        using var redirect = await client.GetAsync("/refactor?scope=a%2Fb");
        Assert.Equal(HttpStatusCode.PermanentRedirect, redirect.StatusCode);
        Assert.Equal("/refactor/?scope=a%2Fb", redirect.Headers.Location?.OriginalString);
        foreach (var path in new[] { "/", "/refactorish/api", "/Refactor/api", "/api/v1/state" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal((HttpStatusCode)418, response.StatusCode);
        }
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("/refactor/", "/")]
    [InlineData("/refactor/app.js?v=2%2F3&v=4", "/app.js?v=2%2F3&v=4")]
    [InlineData("/refactor/api/v1/state?facet=x+y", "/api/v1/state?facet=x+y")]
    [InlineData("/refactor/mcp", "/mcp")]
    [InlineData("/refactor/v1/chat/completions", "/v1/chat/completions")]
    [InlineData("/refactor//evil.invalid/a", "//evil.invalid/a")]
    [InlineData("/refactor/http://evil.invalid/a", "/http://evil.invalid/a")]
    [InlineData("/refactor/a%2Fb/%2e%2e/c?q=%23%3F%2F", "/a%2Fb/%2e%2e/c?q=%23%3F%2F")]
    public async Task EscapedPathAndQueryStayOnFixedAuthority(string rawTarget, string expected)
    {
        var observed = false;
        await using var upstream = await LoopbackUpstream.StartAsync(async received =>
        {
            Assert.Equal("127.0.0.1:55434", received.Request.Host.Value);
            Assert.Equal(expected, received.Features.Get<IHttpRequestFeature>()!.RawTarget);
            observed = true;
            await received.Response.WriteAsync("asset");
        });
        var collection = new ServiceCollection().AddRefactorProxy();
        RouteToLoopback(collection, upstream);
        using var services = collection.BuildServiceProvider();
        var context = Context(rawTarget);
        context.Request.Host = new HostString("evil.invalid", 1234);
        context.Request.Headers["X-Forwarded-Host"] = "another.invalid";
        var middleware = new RefactorProxyMiddleware(
            _ => throw new InvalidOperationException("Unexpected fallthrough."),
            services.GetRequiredService<IHttpClientFactory>(), NullLogger<RefactorProxyMiddleware>.Instance);
        await middleware.InvokeAsync(context).WaitAsync(Deadline);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.True(observed);
    }

    [Theory]
    [InlineData("http://evil.invalid/refactor/api")]
    [InlineData("/%72efactor/api")]
    [InlineData("/refactor/\\evil.invalid")]
    [InlineData("/refactor/path#fragment")]
    public async Task AmbiguousRawTargetsAreRejectedBeforeSending(string rawTarget)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Must not forward."));
        var context = Context("/refactor/api");
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = rawTarget;
        await Middleware(handler).InvokeAsync(context);
        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ContentMetadataIsPreservedOnAnEmptyRequest()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        var context = Context("/refactor/api/v1/state");
        context.Request.ContentType = "application/json";
        await Middleware(handler).InvokeAsync(context);
        Assert.Equal(204, context.Response.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RequestCredentialsBodyAndEndToEndHeadersSurvive_ConnectionNominatedHeadersDoNot()
    {
        using var handler = new Handler(async (request, cancellation) =>
        {
            Assert.Equal("Bearer test-only", request.Headers.Authorization?.ToString());
            Assert.Equal("event-19", Assert.Single(request.Headers.GetValues("Last-Event-ID")));
            Assert.Equal("application/json", request.Content!.Headers.ContentType?.MediaType);
            Assert.Equal("{\"query\":\"x\"}", await request.Content.ReadAsStringAsync(cancellation));
            foreach (var name in new[] { "Connection", "Upgrade", "Keep-Alive", "Transfer-Encoding",
                "Proxy-Authorization", "Proxy-Connection", "TE", "Trailer", "X-Request-Hop" })
                Assert.DoesNotContain(request.Headers.Concat(request.Content.Headers),
                    header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase));
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("upstream denial", Encoding.UTF8, "application/problem+json")
            };
            response.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer realm=refactor");
            response.Headers.TryAddWithoutValidation("Set-Cookie", new[] { "one=1; Path=/refactor", "two=2; Path=/refactor" });
            response.Headers.TryAddWithoutValidation("Connection", "X-Response-Hop, Content-Language");
            response.Headers.TryAddWithoutValidation("X-Response-Hop", "remove");
            response.Content.Headers.TryAddWithoutValidation("Content-Language", "private");
            response.Headers.TryAddWithoutValidation("Keep-Alive", "timeout=10");
            response.Headers.TryAddWithoutValidation("X-Request-Id", "upstream-1");
            return response;
        });
        var context = Context("/refactor/api/v1/query?trace=1");
        context.Request.Method = "POST";
        var body = new TrackingMemoryStream(Encoding.UTF8.GetBytes("{\"query\":\"x\"}"));
        context.Request.Body = body;
        context.Request.ContentLength = body.Length;
        context.Request.ContentType = "application/json";
        context.Request.Headers.Authorization = "Bearer test-only";
        context.Request.Headers["Last-Event-ID"] = "event-19";
        context.Request.Headers.Connection = "Keep-Alive, X-Request-Hop, TE, Trailer, Upgrade";
        context.Request.Headers["X-Request-Hop"] = "remove";
        context.Request.Headers["Keep-Alive"] = "timeout=10";
        context.Request.Headers["TE"] = "trailers";
        context.Request.Headers["Trailer"] = "X-Trailer";
        context.Request.Headers["Upgrade"] = "websocket";
        context.Request.Headers["Proxy-Authorization"] = "Basic do-not-forward";
        context.Request.Headers["Proxy-Connection"] = "keep-alive";
        await Middleware(handler).InvokeAsync(context);
        Assert.Equal(401, context.Response.StatusCode);
        Assert.Equal("Bearer realm=refactor", context.Response.Headers.WWWAuthenticate);
        Assert.Equal(2, context.Response.Headers.SetCookie.Count);
        Assert.Equal("upstream-1", context.Response.Headers["X-Request-Id"]);
        Assert.Equal("application/problem+json; charset=utf-8", context.Response.ContentType);
        foreach (var name in new[] { "Connection", "Keep-Alive", "X-Response-Hop", "Content-Language" })
            Assert.False(context.Response.Headers.ContainsKey(name));
        Assert.Equal("upstream denial", Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
        Assert.False(body.Disposed);
        body.Dispose();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer incorrect")]
    [InlineData("Bearer test-only")]
    public async Task RefactorOwnsBearerDecision_ProxyNeverAddsACredential(string? authorization)
    {
        await using var upstream = await LoopbackUpstream.StartAsync(async context =>
        {
            var authorized = context.Request.Headers.Authorization == "Bearer test-only";
            context.Response.StatusCode = authorized ? 200 : 403;
            await context.Response.WriteAsync(authorized ? "allowed" : "denied");
        });
        using var proxy = Proxy(upstream);
        using var client = proxy.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/refactor/api/v1/state");
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        using var response = await client.SendAsync(request);
        Assert.Equal(authorization == "Bearer test-only" ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SseSplitFrameIsVisibleBeforeUpstreamCompletion()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var upstream = await LoopbackUpstream.StartAsync(async context =>
        {
            Assert.Equal("Bearer test-only", context.Request.Headers.Authorization);
            Assert.Equal("event-8", context.Request.Headers["Last-Event-ID"]);
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("id: 9\ndata: {\"kind\":", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await release.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync("\"tick\"}\n\n", context.RequestAborted);
        });
        using var proxy = Proxy(upstream);
        using var client = proxy.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/refactor/api/v1/events");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-only");
        request.Headers.TryAddWithoutValidation("Last-Event-ID", "event-8");
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).WaitAsync(Deadline);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("no", Assert.Single(response.Headers.GetValues("X-Accel-Buffering")));
            await using var stream = await response.Content.ReadAsStreamAsync();
            var first = new byte[20];
            await stream.ReadExactlyAsync(first).AsTask().WaitAsync(Deadline);
            Assert.Equal("id: 9\ndata: {\"kind\":", Encoding.UTF8.GetString(first));
            Assert.False(release.Task.IsCompleted);
            release.TrySetResult();
            using var reader = new StreamReader(stream);
            Assert.Equal("\"tick\"}\n\n", await reader.ReadToEndAsync().WaitAsync(Deadline));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task RequestBodyReachesUpstreamBeforeCallerCompletesIt()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipe = new Pipe();
        await using var upstream = await LoopbackUpstream.StartAsync(async context =>
        {
            var prefix = new byte[5];
            await context.Request.Body.ReadExactlyAsync(prefix, context.RequestAborted);
            Assert.Equal("first", Encoding.UTF8.GetString(prefix));
            received.TrySetResult();
            using var reader = new StreamReader(context.Request.Body);
            Assert.Equal("-last", await reader.ReadToEndAsync(context.RequestAborted));
            await context.Response.WriteAsync("received");
        });
        using var proxy = Proxy(upstream);
        using var client = proxy.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/refactor/api/v1/query")
        {
            Content = new StreamContent(pipe.Reader.AsStream())
        };
        var responseTask = client.SendAsync(request);
        try
        {
            await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("first"));
            await received.Task.WaitAsync(Deadline);
            Assert.False(responseTask.IsCompleted);
            await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("-last"));
            await pipe.Writer.CompleteAsync();
            using var response = await responseTask.WaitAsync(Deadline);
            Assert.Equal("received", await response.Content.ReadAsStringAsync());
        }
        finally { await pipe.Writer.CompleteAsync(); }
    }

    [Fact]
    public async Task CallerCancellationCancelsPendingSend()
    {
        using var aborted = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = false;
        using var handler = new Handler(async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { canceled = token.IsCancellationRequested; throw; }
            throw new InvalidOperationException("Unreachable.");
        });
        var context = Context("/refactor/api/v1/events");
        context.RequestAborted = aborted.Token;
        var pending = Middleware(handler).InvokeAsync(context);
        await entered.Task.WaitAsync(Deadline);
        aborted.Cancel();
        await pending.WaitAsync(Deadline);
        Assert.True(canceled);
    }

    [Fact]
    public async Task CallerCancellationDisposesStreamingUpstream_WithoutClosingRequestBody()
    {
        using var aborted = new CancellationTokenSource();
        var body = new BlockingStream();
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(body)
        }));
        var requestBody = new TrackingMemoryStream([]);
        var context = Context("/refactor/api/v1/events");
        context.Request.Body = requestBody;
        context.RequestAborted = aborted.Token;
        var pending = Middleware(handler).InvokeAsync(context);
        await body.Entered.Task.WaitAsync(Deadline);
        aborted.Cancel();
        await pending.WaitAsync(Deadline);
        Assert.True(body.Disposed);
        Assert.False(requestBody.Disposed);
        requestBody.Dispose();
    }

    [Fact]
    public async Task RedirectStatusAndLocationAreReturnedWithoutAnotherRequest()
    {
        using var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            response.Headers.Location = new Uri("https://elsewhere.invalid/never-follow");
            return Task.FromResult(response);
        });
        var context = Context("/refactor/api/v1/state");
        await Middleware(handler).InvokeAsync(context);
        Assert.Equal(307, context.Response.StatusCode);
        Assert.Equal("https://elsewhere.invalid/never-follow", context.Response.Headers.Location);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("CONNECT", 405)]
    [InlineData("GET", 502)]
    public async Task HttpUpgradeAndTunnelAreNotForwardingTargets(string method, int expectedStatus)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.SwitchingProtocols)));
        var context = Context("/refactor/api");
        context.Request.Method = method;
        await Middleware(handler).InvokeAsync(context);
        Assert.Equal(expectedStatus, context.Response.StatusCode);
        Assert.Equal(method == "CONNECT" ? 0 : 1, handler.Calls);
    }

    [Fact]
    public async Task UnavailableUpstreamReturnsBadGateway()
    {
        using var handler = new Handler((_, _) => throw new HttpRequestException("test refusal"));
        var context = Context("/refactor/api");
        await Middleware(handler).InvokeAsync(context);
        Assert.Equal(502, context.Response.StatusCode);
    }

    private static TestServer Proxy(HttpMessageHandler handler) => Proxy(services =>
        services.AddHttpClient(RefactorProxyMiddleware.ClientName).ConfigurePrimaryHttpMessageHandler(() => handler));

    private static TestServer Proxy(LoopbackUpstream upstream) => Proxy(services => RouteToLoopback(services, upstream));

    private static TestServer Proxy(Action<IServiceCollection> configure) => new(new WebHostBuilder()
        .ConfigureServices(services =>
        {
            services.AddRefactorProxy();
            configure(services);
        })
        .Configure(app =>
        {
            app.UseMiddleware<RefactorProxyMiddleware>();
            app.Run(context => { context.Response.StatusCode = 418; return Task.CompletedTask; });
        }));

    private static void RouteToLoopback(IServiceCollection services, LoopbackUpstream upstream) =>
        services.AddHttpClient(RefactorProxyMiddleware.ClientName)
            .ConfigurePrimaryHttpMessageHandler((handler, _) =>
                Assert.IsType<SocketsHttpHandler>(handler).ConnectCallback = upstream.ConnectAsync);

    // TestHost's ClientHandler decomposes the URI with GetComponents, which cannot
    // accept an exact-byte URI. Use the registered production socket transport and
    // a real bounded HTTP listener; change only where the test socket connects.
    private sealed class LoopbackUpstream(WebApplication application, IPEndPoint endpoint) : IAsyncDisposable
    {
        public static async Task<LoopbackUpstream> StartAsync(RequestDelegate handler)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var application = builder.Build();
            application.Run(handler);
            try
            {
                using var deadline = new CancellationTokenSource(Deadline);
                await application.StartAsync(deadline.Token);
                var address = new Uri(Assert.Single(application.Urls));
                Assert.Equal("127.0.0.1", address.Host);
                return new LoopbackUpstream(application, new IPEndPoint(IPAddress.Loopback, address.Port));
            }
            catch
            {
                await application.DisposeAsync();
                throw;
            }
        }

        public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellation)
        {
            Assert.Equal("127.0.0.1", context.DnsEndPoint.Host);
            Assert.Equal(55434, context.DnsEndPoint.Port);
            Assert.Equal("http", context.InitialRequestMessage.RequestUri!.Scheme);
            Assert.Equal("", context.InitialRequestMessage.RequestUri.UserInfo);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(endpoint, cancellation);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            using var deadline = new CancellationTokenSource(Deadline);
            try { await application.StopAsync(deadline.Token); }
            finally { await application.DisposeAsync(); }
        }
    }

    private static DefaultHttpContext Context(string rawTarget)
    {
        var query = rawTarget.IndexOf('?');
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = query < 0 ? rawTarget : rawTarget[..query];
        context.Request.QueryString = query < 0 ? QueryString.Empty : new QueryString(rawTarget[query..]);
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = rawTarget;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static RefactorProxyMiddleware Middleware(HttpMessageHandler handler) =>
        new(_ => throw new InvalidOperationException("Unexpected fallthrough."),
            new ClientFactory(handler), NullLogger<RefactorProxyMiddleware>.Instance);

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(RefactorProxyMiddleware.ClientName, name);
            return new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }

    private sealed class TrackingMemoryStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class BlockingStream : Stream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
