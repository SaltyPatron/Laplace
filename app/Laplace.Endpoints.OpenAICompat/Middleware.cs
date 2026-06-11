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
            // DB is up; the query/schema is wrong. Surface the real SQL error, do not mislabel as unavailable.
            _logger.LogError(ex, "Substrate query error.");
            await Results.Json(
                new ErrorResponse(new ErrorBody("substrate_query_error", "substrate_query_error", ex.Message)),
                statusCode: StatusCodes.Status500InternalServerError).ExecuteAsync(context);
        }
        catch (SubstrateUnavailableException ex)
        {
            _logger.LogError(ex, "Substrate unavailable.");
            await EndpointJson.ServiceUnavailable("db_unavailable", ex.Message).ExecuteAsync(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled endpoint exception.");
            await Results.Json(
                new ErrorResponse(new ErrorBody("internal_error", "unhandled_exception", "Unexpected endpoint failure.")),
                statusCode: StatusCodes.Status500InternalServerError).ExecuteAsync(context);
        }
    }
}
