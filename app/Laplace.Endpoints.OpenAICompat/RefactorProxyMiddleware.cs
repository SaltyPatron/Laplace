using System.Buffers;
using System.Net;
using Microsoft.AspNetCore.Http.Features;

namespace Laplace.Endpoints.OpenAICompat;

internal static class RefactorProxyRegistration
{
    public static IServiceCollection AddRefactorProxy(this IServiceCollection services)
    {
        services.AddHttpClient(RefactorProxyMiddleware.ClientName, client =>
        {
            // SSE may outlive an ordinary HTTP request; the caller owns its lifetime.
            client.Timeout = Timeout.InfiniteTimeSpan;
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        });
        return services;
    }
}

// Transport only: Refactor owns its public assets and explicit operator bearer policy.
// There is deliberately no configurable destination or credential lookup here.
internal sealed class RefactorProxyMiddleware(
    RequestDelegate next, IHttpClientFactory clients, ILogger<RefactorProxyMiddleware> logger)
{
    internal const string ClientName = "refactor";
    private const string Prefix = "/refactor";
    private const string Origin = "http://127.0.0.1:55434";
    private static readonly string[] HopByHop =
    [
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "Proxy-Connection", "TE", "Trailer", "Transfer-Encoding", "Upgrade"
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request;
        if (incoming.Path.Equals(new PathString(Prefix), StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
            context.Response.Headers.Location = Prefix + "/" + incoming.QueryString.ToUriComponent();
            return;
        }
        if (!(incoming.Path.Value ?? "").StartsWith(Prefix + "/", StringComparison.Ordinal))
        {
            await next(context);
            return;
        }
        if (HttpMethods.IsConnect(incoming.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(rawTarget))
            rawTarget = incoming.Path.ToUriComponent() + incoming.QueryString.ToUriComponent();
        // Only an origin-form target with this literal prefix is accepted. Concatenating
        // a complete fixed authority keeps //host, encoded slashes and scheme-like paths
        // as paths; disabling URI canonicalization preserves the escaped path/query.
        if (!rawTarget.StartsWith(Prefix + "/", StringComparison.Ordinal)
            || rawTarget.Any(char.IsControl) || rawTarget.Contains('\\') || rawTarget.Contains('#'))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var destination = new Uri(Origin + rawTarget[Prefix.Length..],
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        if (destination.Scheme != "http" || destination.Host != "127.0.0.1"
            || destination.Port != 55434 || destination.UserInfo.Length != 0)
            throw new InvalidOperationException("Refactor forwarding requires the fixed loopback origin.");

        var cancellation = context.RequestAborted;
        using var request = new HttpRequestMessage(new HttpMethod(incoming.Method), destination);
        var canHaveBody = context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody
            ?? (incoming.ContentLength is not null || incoming.Headers.ContainsKey("Transfer-Encoding"));
        if (canHaveBody)
            request.Content = new RequestBodyContent(incoming.Body, incoming.ContentLength, cancellation);

        var requestExcluded = ExcludedHeaders(incoming.Headers.Connection);
        requestExcluded.Add("Host"); // HttpClient writes the fixed upstream authority.
        foreach (var header in incoming.Headers)
        {
            if (requestExcluded.Contains(header.Key)) continue;
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                // Content metadata is still end-to-end on an empty request.
                request.Content ??= new RequestBodyContent(Stream.Null, 0, cancellation);
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        using var client = clients.CreateClient(ClientName);
        try
        {
            using var upstream = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (upstream.StatusCode == HttpStatusCode.SwitchingProtocols)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }
            context.Response.StatusCode = (int)upstream.StatusCode;
            var responseExcluded = ExcludedHeaders(upstream.Headers.TryGetValues("Connection", out var connection)
                ? connection : []);
            foreach (var header in upstream.Headers.Concat(upstream.Content.Headers))
                if (!responseExcluded.Contains(header.Key))
                    context.Response.Headers[header.Key] = header.Value.ToArray();

            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            if (string.Equals(upstream.Content.Headers.ContentType?.MediaType, "text/event-stream",
                StringComparison.OrdinalIgnoreCase))
                context.Response.Headers["X-Accel-Buffering"] = "no";

            await context.Response.StartAsync(cancellation);
            if (HttpMethods.IsHead(incoming.Method)
                || upstream.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotModified)
                return;

            await using var body = await upstream.Content.ReadAsStreamAsync(cancellation);
            await CopyAndFlushAsync(body, context.Response.Body, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            context.Abort();
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            logger.LogWarning("Refactor upstream transport failed ({ErrorType}).", error.GetType().Name);
            if (context.Response.HasStarted) context.Abort();
            else
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
            }
        }
    }

    // HttpClient's outgoing content stream can retain a short write until it is
    // flushed. Both directions must expose each available chunk while the peer
    // is still producing the rest of an upload or SSE response.
    private static async Task CopyAndFlushAsync(Stream source, Stream destination, CancellationToken cancellation)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(), cancellation)) != 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellation);
                await destination.FlushAsync(cancellation);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static HashSet<string> ExcludedHeaders(IEnumerable<string?> connection)
    {
        var excluded = new HashSet<string>(HopByHop, StringComparer.OrdinalIgnoreCase);
        foreach (var value in connection)
            foreach (var name in (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                excluded.Add(name);
        return excluded;
    }

    // The ASP.NET request stream belongs to the server. Disposing the outbound
    // message must release HttpContent without closing that server-owned stream.
    private sealed class RequestBodyContent(Stream body, long? length, CancellationToken aborted) : HttpContent
    {
        protected override bool TryComputeLength(out long result)
        {
            result = length.GetValueOrDefault();
            return length.HasValue;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => CopyAndFlushAsync(body, stream, aborted);

        protected override Task SerializeToStreamAsync(
            Stream stream, TransportContext? context, CancellationToken cancellationToken)
            => CopyAndFlushAsync(body, stream, cancellationToken);
    }
}
