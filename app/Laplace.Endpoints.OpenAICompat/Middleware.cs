using global::Npgsql;
using Laplace.Api.Contracts;
using Serilog.Context;

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
        var correlationId = Laplace.Ops.LaplaceLogging.CorrelationId(context.Request.Headers["X-Correlation-Id"]);

        context.Items["correlation_id"] = correlationId;
        context.Response.Headers["X-Correlation-Id"] = correlationId;
        using var scope = LogContext.PushProperty("CorrelationId", correlationId);
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
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            _logger.LogInformation("HTTP request aborted by the caller.");
            if (!context.Response.HasStarted) context.Response.StatusCode = 499;
        }
        catch (Exception ex) when (context.Response.HasStarted)
        {
            _logger.LogError(ex, "HTTP request failed after the response started; the response cannot be replaced.");
            throw;
        }
        catch (Auth.ApiKeyTenantNotFoundException ex)
        {
            await Results.Json(
                new ErrorResponse(new ErrorBody("invalid_request_error", "tenant_not_found", ex.Message)),
                statusCode: StatusCodes.Status404NotFound).ExecuteAsync(context);
        }
        catch (SubstrateQueryException ex)
        {
            if (IsTimeoutFailure(ex))
            {
                _logger.LogError(ex, "Substrate operation timed out.");
                await WriteTimeoutAsync(context);
                return;
            }

            _logger.LogError(ex, "Substrate query error.");
            await Results.Json(
                new ErrorResponse(new ErrorBody("substrate_query_error", "substrate_query_error", ex.Message)),
                statusCode: StatusCodes.Status500InternalServerError).ExecuteAsync(context);
        }
        catch (SubstrateUnavailableException ex)
        {
            if (IsTimeoutFailure(ex))
            {
                _logger.LogError(ex, "Substrate operation timed out.");
                await WriteTimeoutAsync(context);
                return;
            }

            _logger.LogError(ex, "Substrate unavailable.");
            await EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message).ExecuteAsync(context);
        }
        catch (Exception ex) when (ex is NpgsqlException or PostgresException or TimeoutException)
        {
            // Command timeouts can be wrapped by Npgsql and, on the chat path,
            // by endpoint exception types before reaching this middleware. Walk
            // the complete exception chain so a slow query is never relabelled as
            // a connectivity failure merely because an intermediate layer wrapped it.
            if (IsTimeoutFailure(ex))
            {
                _logger.LogError(ex, "Substrate operation timed out.");
                await WriteTimeoutAsync(context);
                return;
            }

            _logger.LogError(ex, "Substrate connection failed.");
            await EndpointJson.ServiceUnavailable(
                "substrate_unavailable",
                $"Substrate unreachable: {ex.Message}").ExecuteAsync(context);
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

    private static Task WriteTimeoutAsync(HttpContext context) =>
        EndpointJson.ServiceUnavailable(
            "substrate_timeout",
            "Substrate operation timed out. Use the request correlation ID to locate the server diagnostics.")
        .ExecuteAsync(context);

    private static bool IsTimeoutFailure(Exception failure)
    {
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException)
                return true;
            if (current is PostgresException pg && pg.SqlState == PostgresErrorCodes.QueryCanceled)
                return true;
        }
        return false;
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
