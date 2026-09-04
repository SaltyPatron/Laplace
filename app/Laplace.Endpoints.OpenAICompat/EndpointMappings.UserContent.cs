using System.Text;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Ingestion;

namespace Laplace.Endpoints.OpenAICompat;

internal static class UserContentEndpointMappings
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static IEndpointRouteBuilder MapUserContentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/content/text", async (
            HttpContext http,
            UserTextArtifactWriteRequest request,
            ITenantResolver tenants,
            ContentArtifactCloser closer,
            CancellationToken ct) =>
        {
            var tenant = await tenants.ResolveAsync(http, ct);
            if (!TryReadContent(request, out var bytes, out var error))
                return Results.BadRequest(new { error = new { type = "invalid_request_error", code = "invalid_content", message = error } });

            string name = request.Name?.Trim() ?? "";
            if (name.Length == 0)
                return Results.BadRequest(new { error = new { type = "invalid_request_error", code = "name_required", message = "name is required" } });

            string path = string.IsNullOrWhiteSpace(request.Path) ? name : request.Path!.Trim();
            if (!ValidRelativePath(path))
                return Results.BadRequest(new { error = new { type = "invalid_request_error", code = "invalid_path", message = "path must be relative and may not contain '..' segments" } });

            UserArtifactContent.ArtifactIds? ids;
            try
            {
                ids = await closer.CloseTextAsync(
                    tenant.TenantId,
                    name,
                    path,
                    bytes,
                    request.UserId,
                    request.ModifiedAt?.UtcDateTime,
                    ct);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = new { type = "invalid_request_error", code = "invalid_provenance", message = ex.Message } });
            }

            if (ids is not { } value)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            var scope = UserArtifactContent.Resolve(tenant.TenantId);
            return Results.Ok(new UserContentWriteResponse(
                value.FileId.ToString(),
                value.DocumentId.ToString(),
                value.ContentId.ToString(),
                value.MetadataId.ToString(),
                value.SourceId.ToString(),
                scope.SourceName,
                bytes.LongLength));
        });

        app.MapGet("/v1/content/{idHex}", async (
            HttpContext http,
            string idHex,
            ITenantResolver tenants,
            SubstrateClient substrate,
            CancellationToken ct) =>
        {
            var tenant = await tenants.ResolveAsync(http, ct);
            var export = await substrate.ExportUserContentAsync(tenant.TenantId, idHex, ct);
            return export is null ? Results.NotFound() : Results.Ok(export);
        });

        return app;
    }

    private static bool TryReadContent(
        UserTextArtifactWriteRequest request,
        out byte[] bytes,
        out string error)
    {
        bytes = [];
        error = "";
        bool hasText = request.Text is not null;
        bool hasBase64 = !string.IsNullOrWhiteSpace(request.ContentBase64);
        if (hasText == hasBase64)
        {
            error = "provide exactly one of text or content_base64";
            return false;
        }

        if (hasText)
        {
            bytes = Encoding.UTF8.GetBytes(request.Text!);
            return bytes.Length > 0 || Fail("content is empty", out error);
        }

        try
        {
            bytes = Convert.FromBase64String(request.ContentBase64!);
            if (bytes.Length == 0)
            {
                error = "content is empty";
                return false;
            }
            _ = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (FormatException)
        {
            error = "content_base64 is not valid base64";
            return false;
        }
        catch (DecoderFallbackException)
        {
            error = "content_base64 must contain valid UTF-8 text bytes";
            return false;
        }
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private static bool ValidRelativePath(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.Length > 0
            && !Path.IsPathRooted(normalized)
            && !normalized.Split('/').Any(static p => p is ".." or "");
    }
}
