using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Engine.Core;
using Laplace.Ingestion;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Uploads (Wiki Sequence 22.5, 22.6): a participant's file, kept at its relative path and read by the Engine as the
/// participant states it (<see cref="UserUploads"/>); read back off the Native substrate. The text and code routes are
/// one operation: the recipe the file's name matches reads it. The participant is the authenticated user, else the
/// user the request names, else the tenant; a file is read back only by the participant that stated it.
/// </summary>
internal static class UserContentEndpointMappings
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static IEndpointRouteBuilder MapUserContentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/content/text", (HttpContext http, UserTextArtifactWriteRequest request, ITenantResolver tenants,
                UserUploads uploads, SubstrateClient substrate, CancellationToken ct) =>
            AdmitJsonAsync(http, request.Name, request.Path, request.Text, request.ContentBase64, request.UserId,
                request.ModifiedAt, tenants, uploads, substrate, ct));

        app.MapPost("/v1/content/code", (HttpContext http, UserCodeArtifactWriteRequest request, ITenantResolver tenants,
                UserUploads uploads, SubstrateClient substrate, CancellationToken ct) =>
            AdmitJsonAsync(http, request.Name, request.Path, request.Text, request.ContentBase64, request.UserId,
                request.ModifiedAt, tenants, uploads, substrate, ct));

        app.MapPost("/v1/content/text/raw", (HttpContext http, ITenantResolver tenants, UserUploads uploads,
                SubstrateClient substrate, CancellationToken ct) => AdmitRawAsync(http, tenants, uploads, substrate, ct));

        app.MapPost("/v1/content/code/raw", (HttpContext http, ITenantResolver tenants, UserUploads uploads,
                SubstrateClient substrate, CancellationToken ct) => AdmitRawAsync(http, tenants, uploads, substrate, ct));

        app.MapGet("/v1/content/{idHex}/raw", async (HttpContext http, string idHex, ITenantResolver tenants,
            SubstrateClient substrate, CancellationToken ct) =>
        {
            var (participant, error) = await ParticipantAsync(http, tenants, http.Request.Query["user"].ToString(), ct);
            if (error is not null) return error;
            var file = await ReadAsync(substrate, participant!, idHex, ct);
            return file is null ? Results.NotFound() : Results.File(file.Content, "application/octet-stream", file.FileId);
        });

        app.MapGet("/v1/content/{idHex}", async (HttpContext http, string idHex, bool? compact, ITenantResolver tenants,
            SubstrateClient substrate, CancellationToken ct) =>
        {
            var (participant, error) = await ParticipantAsync(http, tenants, http.Request.Query["user"].ToString(), ct);
            if (error is not null) return error;
            var file = await ReadAsync(substrate, participant!, idHex, ct);
            if (file is null) return Results.NotFound();
            string text = Encoding.UTF8.GetString(file.Content);
            const int PreviewChars = 131072;
            if (compact == true && text.Length > PreviewChars)
            {
                text = text[..PreviewChars];
                if (char.IsHighSurrogate(text[^1])) text = text[..^1];
            }
            return Results.Ok(new UserContentExportResponse(file.FileId, file.ContentId, file.MetadataId, file.WitnessId,
                file.Participant, compact == true ? "" : Convert.ToBase64String(file.Content), text, file.Content.LongLength));
        });

        return app;
    }

    private static async Task<IResult> AdmitJsonAsync(HttpContext http, string? name, string? path, string? text,
        string? contentBase64, string? userId, DateTimeOffset? modifiedAt, ITenantResolver tenants, UserUploads uploads,
        SubstrateClient substrate, CancellationToken ct)
    {
        if (modifiedAt is not null) return TimesRefused();
        if (!TryReadContent(text, contentBase64, out var bytes, out var invalid))
            return Invalid("invalid_content", invalid);
        return await AdmitAsync(http, name, path, bytes, userId, tenants, uploads, substrate, ct);
    }

    private static async Task<IResult> AdmitRawAsync(HttpContext http, ITenantResolver tenants, UserUploads uploads,
        SubstrateClient substrate, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(http.Request.Query["modified_at"].ToString())) return TimesRefused();
        byte[] bytes;
        using (var body = http.Request.ContentLength is > 0 and <= int.MaxValue
                   ? new MemoryStream((int)http.Request.ContentLength.Value) : new MemoryStream())
        {
            await http.Request.Body.CopyToAsync(body, ct);
            bytes = body.ToArray();
        }
        if (bytes.Length == 0) return Invalid("invalid_content", "content is empty");
        try { _ = StrictUtf8.GetCharCount(bytes); }
        catch (DecoderFallbackException) { return Invalid("invalid_content", "raw content must contain valid UTF-8 text bytes"); }
        return await AdmitAsync(http, http.Request.Query["name"].ToString(), http.Request.Query["path"].ToString(), bytes,
            http.Request.Query["user"].ToString(), tenants, uploads, substrate, ct);
    }

    private static async Task<IResult> AdmitAsync(HttpContext http, string? name, string? path, byte[] bytes, string? user,
        ITenantResolver tenants, UserUploads uploads, SubstrateClient substrate, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return Invalid("name_required", "name is required");
        string relative = string.IsNullOrWhiteSpace(path) ? name : path;
        if (!UserUploads.IsRelativePath(relative))
            return Invalid("invalid_path", "path must be relative and may not contain empty, '.' or '..' segments");
        var (participant, error) = await ParticipantAsync(http, tenants, user, ct);
        if (error is not null) return error;

        string fileId;
        try { fileId = await uploads.AdmitAsync(participant!, relative.Replace('\\', '/'), bytes, ct); }
        catch (InvalidOperationException failure)
        {
            return EndpointJson.ServiceUnavailable("upload_not_recorded", failure.Message);
        }
        var file = await ReadAsync(substrate, participant!, fileId, ct);
        if (file is null)
            return EndpointJson.ServiceUnavailable("upload_not_recorded",
                "The Engine named the file, but no trunk of the participant holds it.");
        return Results.Ok(new UserContentWriteResponse(file.FileId, file.ContentId, file.MetadataId, file.WitnessId,
            file.Participant, relative.Replace('\\', '/'), bytes.LongLength));
    }

    /// <summary>The participant whose file it is: the authenticated user, else the user named, else the tenant.</summary>
    private static async Task<(string? Participant, IResult? Error)> ParticipantAsync(HttpContext http,
        ITenantResolver tenants, string? user, CancellationToken ct)
    {
        var tenant = await tenants.ResolveAsync(http, ct);
        if (!ConversationContent.IsValidIdentifier(tenant.TenantId))
            return (null, Invalid("invalid_tenant", "Tenant id must match [A-Za-z0-9._@-]{1,128}."));
        if (tenant.Claims.TryGetValue("user_id", out var claimed) && Guid.TryParse(claimed, out var authenticated))
            return (authenticated.ToString("D"), null);
        string participant = string.IsNullOrWhiteSpace(user) ? tenant.TenantId : user;
        return UserUploads.IsParticipant(participant)
            ? (participant, null)
            : (null, Invalid("invalid_user", "The user must match [A-Za-z0-9._@-]{1,128}."));
    }

    private static Task<UploadedFile?> ReadAsync(SubstrateClient substrate, string participant, string idHex, CancellationToken ct)
        => idHex.Length == 32 && idHex.All(Uri.IsHexDigit)
            ? UserUploads.ReadAsync(substrate.DataSource, participant, Hash128.FromBytes(Convert.FromHexString(idHex)), ct)
            : Task.FromResult<UploadedFile?>(null);

    private static IResult Invalid(string code, string message)
        => Results.BadRequest(new { error = new { type = "invalid_request_error", code, message } });

    private static IResult TimesRefused()
        => Invalid("unsupported_parameter", "A file's times are no part of it: modified_at is not recorded.");

    private static bool TryReadContent(string? text, string? contentBase64, out byte[] bytes, out string error)
    {
        bytes = [];
        error = "";
        bool hasText = text is not null, hasBase64 = !string.IsNullOrWhiteSpace(contentBase64);
        if (hasText == hasBase64) { error = "provide exactly one of text or content_base64"; return false; }
        if (hasText)
        {
            bytes = Encoding.UTF8.GetBytes(text!);
            if (bytes.Length == 0) { error = "content is empty"; return false; }
            return true;
        }
        try
        {
            bytes = Convert.FromBase64String(contentBase64!);
            if (bytes.Length == 0) { error = "content is empty"; return false; }
            _ = StrictUtf8.GetCharCount(bytes);
            return true;
        }
        catch (FormatException) { error = "content_base64 is not valid base64"; return false; }
        catch (DecoderFallbackException) { error = "content_base64 must contain valid UTF-8 text bytes"; return false; }
    }
}
