using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD;

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

            string name = request.Name ?? "";
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { error = new { type = "invalid_request_error", code = "name_required", message = "name is required" } });

            string path = string.IsNullOrWhiteSpace(request.Path) ? name : request.Path!;
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
            catch (LegacyReplayRequiresReconciliationException ex)
            {
                return Results.Conflict(new { error = new { type = "reconciliation_required", code = "legacy_replay_requires_reconciliation", message = ex.Message } });
            }

            if (ids is not { } value)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            var scope = UserArtifactContent.Resolve(tenant.TenantId);
            return Results.Ok(new UserContentWriteResponse(
                Convert.ToHexStringLower(value.FileId.ToBytes()),
                Convert.ToHexStringLower(value.DocumentId.ToBytes()),
                Convert.ToHexStringLower(value.ContentId.ToBytes()),
                Convert.ToHexStringLower(value.MetadataId.ToBytes()),
                Convert.ToHexStringLower(value.SourceId.ToBytes()),
                scope.SourceName,
                bytes.LongLength,
                Modality: null));
        });

        app.MapPost("/v1/content/code", async (
            HttpContext http,
            UserCodeArtifactWriteRequest request,
            ITenantResolver tenants,
            ContentArtifactCloser closer,
            CancellationToken ct) =>
        {
            var tenant = await tenants.ResolveAsync(http, ct);
            if (!TryReadContent(request.Text, request.ContentBase64, out var bytes, out var error))
                return Results.BadRequest(new { error = new { type = "invalid_request_error", code = "invalid_content", message = error } });

            string name = request.Name ?? "";
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { error = new { type = "invalid_request_error", code = "name_required", message = "name is required" } });

            string path = string.IsNullOrWhiteSpace(request.Path) ? name : request.Path!;
            if (!ValidRelativePath(path))
                return Results.BadRequest(new { error = new { type = "invalid_request_error", code = "invalid_path", message = "path must be relative and may not contain '..' segments" } });

            string? modality = ResolveGrammarModality(path);
            if (modality is null)
                return Results.BadRequest(new { error = new { type = "invalid_request_error", code = "unsupported_grammar", message = "path extension has no registered grammar" } });

            UserArtifactContent.ArtifactIds? ids;
            try
            {
                ids = await closer.CloseCodeAsync(
                    tenant.TenantId,
                    name,
                    path,
                    bytes,
                    modality,
                    request.UserId,
                    request.ModifiedAt?.UtcDateTime,
                    ct);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = new { type = "invalid_request_error", code = "invalid_provenance", message = ex.Message } });
            }
            catch (LegacyReplayRequiresReconciliationException ex)
            {
                return Results.Conflict(new { error = new { type = "reconciliation_required", code = "legacy_replay_requires_reconciliation", message = ex.Message } });
            }

            if (ids is not { } value)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            var scope = UserArtifactContent.Resolve(tenant.TenantId);
            return Results.Ok(new UserContentWriteResponse(
                Convert.ToHexStringLower(value.FileId.ToBytes()),
                Convert.ToHexStringLower(value.DocumentId.ToBytes()),
                Convert.ToHexStringLower(value.ContentId.ToBytes()),
                Convert.ToHexStringLower(value.MetadataId.ToBytes()),
                Convert.ToHexStringLower(value.SourceId.ToBytes()),
                scope.SourceName,
                bytes.LongLength,
                modality));
        });

        app.MapPost("/v1/content/text/raw", async (
            HttpContext http,
            ITenantResolver tenants,
            ContentArtifactCloser closer,
            CancellationToken ct) =>
            await CloseRawAsync(http, tenants, closer, code: false, ct));

        app.MapPost("/v1/content/code/raw", async (
            HttpContext http,
            ITenantResolver tenants,
            ContentArtifactCloser closer,
            CancellationToken ct) =>
            await CloseRawAsync(http, tenants, closer, code: true, ct));

        app.MapGet("/v1/content/{idHex}/raw", async (
            HttpContext http,
            string idHex,
            ITenantResolver tenants,
            SubstrateClient substrate,
            CancellationToken ct) =>
        {
            var tenant = await tenants.ResolveAsync(http, ct);
            var export = await substrate.ExportUserContentAsync(tenant.TenantId, idHex, ct);
            if (export is null) return Results.NotFound();
            byte[] bytes = Convert.FromBase64String(export.ContentBase64);
            string fileName = Path.GetFileName(export.Name ?? export.Path ?? idHex + ".bin");
            return Results.File(bytes, "application/octet-stream", fileName);
        });

        app.MapGet("/v1/content/{idHex}", async (
            HttpContext http,
            string idHex,
            bool? compact,
            ITenantResolver tenants,
            SubstrateClient substrate,
            CancellationToken ct) =>
        {
            var tenant = await tenants.ResolveAsync(http, ct);
            var export = await substrate.ExportUserContentAsync(tenant.TenantId, idHex, ct);
            if (export is null) return Results.NotFound();
            if (compact == true)
            {
                const int PreviewChars = 131072;
                string? preview = export.Text;
                if (preview is { Length: > PreviewChars })
                {
                    preview = preview[..PreviewChars];
                    if (preview.Length > 0 && char.IsHighSurrogate(preview[^1])) preview = preview[..^1];
                }
                export = export with { ContentBase64 = "", Text = preview };
            }
            return Results.Ok(export);
        });

        return app;
    }

    private static async Task<IResult> CloseRawAsync(
        HttpContext http,
        ITenantResolver tenants,
        ContentArtifactCloser closer,
        bool code,
        CancellationToken ct)
    {
        var tenant = await tenants.ResolveAsync(http, ct);
        string name = http.Request.Query["name"].ToString();
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new { error = new { type = "invalid_request_error", code = "name_required", message = "name is required" } });

        string path = http.Request.Query["path"].ToString();
        if (string.IsNullOrWhiteSpace(path)) path = name;
        if (!ValidRelativePath(path))
            return Results.BadRequest(new { error = new { type = "invalid_request_error", code = "invalid_path", message = "path must be relative and may not contain '..' segments" } });

        DateTime? modifiedAt = null;
        string modified = http.Request.Query["modified_at"].ToString();
        if (!string.IsNullOrWhiteSpace(modified))
        {
            if (!DateTimeOffset.TryParse(modified, out var parsed))
                return Results.BadRequest(new { error = new { type = "invalid_request_error", code = "invalid_modified_at", message = "modified_at must be an ISO-8601 timestamp" } });
            modifiedAt = parsed.UtcDateTime;
        }

        byte[] bytes;
        try
        {
            using var body = http.Request.ContentLength is > 0 and <= int.MaxValue
                ? new MemoryStream((int)http.Request.ContentLength.Value)
                : new MemoryStream();
            await http.Request.Body.CopyToAsync(body, ct);
            bytes = body.ToArray();
            if (bytes.Length == 0)
                return Results.BadRequest(new { error = new { type = "invalid_request_error", code = "invalid_content", message = "content is empty" } });
            _ = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Results.BadRequest(new { error = new { type = "invalid_request_error", code = "invalid_content", message = "raw content must contain valid UTF-8 text bytes" } });
        }

        string? modality = code ? ResolveGrammarModality(path) : null;
        if (code && modality is null)
            return Results.BadRequest(new { error = new { type = "invalid_request_error", code = "unsupported_grammar", message = "path extension has no registered grammar" } });

        UserArtifactContent.ArtifactIds? ids;
        try
        {
            ids = code
                ? await closer.CloseCodeAsync(tenant.TenantId, name, path, bytes, modality!, null, modifiedAt, ct)
                : await closer.CloseTextAsync(tenant.TenantId, name, path, bytes, null, modifiedAt, ct);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = new { type = "invalid_request_error", code = "invalid_provenance", message = ex.Message } });
        }
        catch (LegacyReplayRequiresReconciliationException ex)
        {
            return Results.Conflict(new { error = new { type = "reconciliation_required", code = "legacy_replay_requires_reconciliation", message = ex.Message } });
        }

        if (ids is not { } value) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var scope = UserArtifactContent.Resolve(tenant.TenantId);
        return Results.Ok(new UserContentWriteResponse(
            Convert.ToHexStringLower(value.FileId.ToBytes()),
            Convert.ToHexStringLower(value.DocumentId.ToBytes()),
            Convert.ToHexStringLower(value.ContentId.ToBytes()),
            Convert.ToHexStringLower(value.MetadataId.ToBytes()),
            Convert.ToHexStringLower(value.SourceId.ToBytes()),
            scope.SourceName,
            bytes.LongLength,
            modality));
    }

    private static bool TryReadContent(
        UserTextArtifactWriteRequest request,
        out byte[] bytes,
        out string error)
        => TryReadContent(request.Text, request.ContentBase64, out bytes, out error);

    private static bool TryReadContent(
        string? text,
        string? contentBase64,
        out byte[] bytes,
        out string error)
    {
        bytes = [];
        error = "";
        bool hasText = text is not null;
        bool hasBase64 = !string.IsNullOrWhiteSpace(contentBase64);
        if (hasText == hasBase64)
        {
            error = "provide exactly one of text or content_base64";
            return false;
        }

        if (hasText)
        {
            bytes = Encoding.UTF8.GetBytes(text!);
            return bytes.Length > 0 || Fail("content is empty", out error);
        }

        try
        {
            bytes = Convert.FromBase64String(contentBase64!);
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

    internal static string? ResolveGrammarModality(string path)
    {
        string extension = Path.GetExtension(path);
        if (extension.Equals(".in", StringComparison.OrdinalIgnoreCase))
            extension = Path.GetExtension(path[..^extension.Length]);
        return extension.Length > 1
            ? GrammarDecomposer.ModalityByExt(extension[1..].ToLowerInvariant())
            : null;
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
