using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class MiddlewareTimeoutTests
{
    [Fact]
    public async Task WrappedUnavailableTimeout_IsReportedAsTimeout_NotConnectivityFailure()
    {
        var context = NewContext();
        var middleware = new ExceptionEnvelopeMiddleware(
            _ => Task.FromException(new SubstrateUnavailableException(
                "Substrate is unreachable.", new TimeoutException("command timeout"))),
            NullLogger<ExceptionEnvelopeMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        using var json = await ReadResponseAsync(context);
        var error = json.RootElement.GetProperty("error");
        Assert.Equal("substrate_timeout", error.GetProperty("code").GetString());
        Assert.Contains("time budget", error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unreachable", error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WrappedQueryTimeout_IsReportedAsTimeout_NotQueryFailure()
    {
        var context = NewContext();
        var middleware = new ExceptionEnvelopeMiddleware(
            _ => Task.FromException(new SubstrateQueryException(
                "recall_session query failed", new TimeoutException("command timeout"))),
            NullLogger<ExceptionEnvelopeMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        using var json = await ReadResponseAsync(context);
        Assert.Equal("substrate_timeout",
            json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ActualUnavailableFailure_RemainsUnavailable()
    {
        var context = NewContext();
        var middleware = new ExceptionEnvelopeMiddleware(
            _ => Task.FromException(new SubstrateUnavailableException(
                "Substrate is unreachable.", new InvalidOperationException("connection refused"))),
            NullLogger<ExceptionEnvelopeMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        using var json = await ReadResponseAsync(context);
        Assert.Equal("substrate_unavailable",
            json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddOptions()
                .BuildServiceProvider(),
        };
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<JsonDocument> ReadResponseAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(context.Response.Body);
    }
}
