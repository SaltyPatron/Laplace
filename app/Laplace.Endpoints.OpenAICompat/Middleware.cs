using global::Npgsql;
using Laplace.Api.Contracts;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed class CorrelationIdMiddleware
{
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers["X-Correlation-Id"].ToString();
        if (string.IsNullOrWhiteSpace(correlationId))
            correlationId = Guid.NewGuid().ToString("N");

        context.Items["correlation_id"] = correlationId;
        context.Response.Headers["X-Correlation-Id"] = correlationId;
        await _next(context);
    }
}

internal sealed class ExceptionEnvelopeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionEnvelopeMiddleware> _logger;

    public ExceptionEnvelopeMiddleware(RequestDelegate next, ILogger<ExceptionEnvelopeMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (SubstrateQueryException ex)
        {

            _logger.LogError(ex, "Substrate query error.");
            await Results.Json(
                new ErrorResponse(new ErrorBody("substrate_query_error", "substrate_query_error", ex.Message)),
                statusCode: StatusCodes.Status500InternalServerError).ExecuteAsync(context);
        }
        catch (SubstrateUnavailableException ex)
        {
            _logger.LogError(ex, "Substrate unavailable.");
            await EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message).ExecuteAsync(context);
        }
        catch (Exception ex) when (ex is NpgsqlException or PostgresException or TimeoutException)
        {
            // A tripped command timeout surfaces as NpgsqlException wrapping
            // TimeoutException (or 57014 after a server-side cancel). Report it as a
            // time budget, not unreachability — the substrate is up, the query is slow.
            var timedOut = ex is TimeoutException
                || (ex as PostgresException)?.SqlState == PostgresErrorCodes.QueryCanceled
                || ex.InnerException is TimeoutException;
            _logger.LogError(ex, timedOut ? "Substrate query exceeded time budget." : "Substrate connection failed.");
            await EndpointJson.ServiceUnavailable(
                "substrate_unavailable",
                timedOut
                    ? $"Substrate query exceeded the API time budget ({SubstrateClient.DefaultCommandTimeoutSeconds}s)."
                    : $"Substrate unreachable: {ex.Message}").ExecuteAsync(context);
        }
        catch (InvalidOperationException ex) when (IsInfrastructureFailure(ex))
        {
            _logger.LogError(ex, "Substrate infrastructure not ready.");
            await EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message).ExecuteAsync(context);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Invalid operation.");
            await Results.Json(
                new ErrorResponse(new ErrorBody("internal_error", "invalid_operation", ex.Message)),
                statusCode: StatusCodes.Status500InternalServerError).ExecuteAsync(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled endpoint exception.");
            await Results.Json(
                new ErrorResponse(new ErrorBody("internal_error", "unhandled_exception", "Unexpected endpoint failure.")),
                statusCode: StatusCodes.Status500InternalServerError).ExecuteAsync(context);
        }
    }

    private static bool IsInfrastructureFailure(InvalidOperationException ex)
    {
        var msg = ex.Message;
        return msg.Contains("perfcache", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Postgres", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("substrate", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("connect", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }
}
