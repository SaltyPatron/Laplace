using System.Text;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Engine.Core;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Uploads (Wiki Sequence 22.5, 22.6): a participant's file held at its relative path,
/// witnessed by the C Engine (<c>laplace witness</c> at <c>UserPromptContent</c>) and
/// read back via <c>Laplace.Data</c> SQL over the content substrate.
/// </summary>
internal static class UserContentEndpointMappings
{
    private const string TrustClass = "UserPromptContent";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal sealed record UploadedFile(
        string FileId,
        string ContentId,
        string MetadataId,
        string WitnessId,
        string Participant,
        byte[] Content);

    public static IEndpointRouteBuilder MapUserContentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/content/text", (HttpContext http, UserTextArtifactWriteRequest request, ITenantResolver tenants,
                SubstrateClient substrate, CancellationToken ct) =>
            AdmitJsonAsync(http, request.Name, request.Path, request.Text, request.ContentBase64, request.UserId,
                request.ModifiedAt, tenants, substrate, ct));

        app.MapPost("/v1/content/code", (HttpContext http, UserCodeArtifactWriteRequest request, ITenantResolver tenants,
                SubstrateClient substrate, CancellationToken ct) =>
            AdmitJsonAsync(http, request.Name, request.Path, request.Text, request.ContentBase64, request.UserId,
                request.ModifiedAt, tenants, substrate, ct));

        app.MapPost("/v1/content/text/raw", (HttpContext http, ITenantResolver tenants,
                SubstrateClient substrate, CancellationToken ct) => AdmitRawAsync(http, tenants, substrate, ct));

        app.MapPost("/v1/content/code/raw", (HttpContext http, ITenantResolver tenants,
                SubstrateClient substrate, CancellationToken ct) => AdmitRawAsync(http, tenants, substrate, ct));

        app.MapPost("/v1/content/media/raw", (HttpContext http, ITenantResolver tenants,
                SubstrateClient substrate, CancellationToken ct) => AdmitRawAsync(http, tenants, substrate, ct, requireUtf8: false));

        app.MapGet("/v1/content", async (HttpContext http, int? limit, ITenantResolver tenants,
            SubstrateClient substrate, CancellationToken ct) =>
        {
            var (participant, error) = await ParticipantAsync(http, tenants, http.Request.Query["user"].ToString(), ct);
            if (error is not null) return error;
            var rows = await Laplace.Data.NpgsqlSubstrateReads.ListWitnessedContentAsync(
                substrate.DataSource, Hash128.Text(participant!).ToBytes(), Math.Clamp(limit ?? 50, 1, 200), ct);
            return Results.Ok(new UserContentListResponse(
                participant!,
                rows.Select(static r => new UserContentListItem(r.FileIdHex, r.Label, r.Tier, r.Bytes)).ToArray()));
        })
        .WithTags("content")
        .Produces<UserContentListResponse>();

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
            string text;
            try { text = StrictUtf8.GetString(file.Content); }
            catch (DecoderFallbackException) { text = ""; }
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
        string? contentBase64, string? userId, DateTimeOffset? modifiedAt, ITenantResolver tenants,
        SubstrateClient substrate, CancellationToken ct)
    {
        if (modifiedAt is not null) return TimesRefused();
        if (!TryReadContent(text, contentBase64, out var bytes, out var invalid))
            return Invalid("invalid_content", invalid);
        return await AdmitAsync(http, name, path, bytes, userId, tenants, substrate, ct);
    }

    private static async Task<IResult> AdmitRawAsync(HttpContext http, ITenantResolver tenants,
        SubstrateClient substrate, CancellationToken ct, bool requireUtf8 = true)
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
        if (requireUtf8)
        {
            try { _ = StrictUtf8.GetCharCount(bytes); }
            catch (DecoderFallbackException) { return Invalid("invalid_content", "raw content must contain valid UTF-8 text bytes"); }
        }
        return await AdmitAsync(http, http.Request.Query["name"].ToString(), http.Request.Query["path"].ToString(), bytes,
            http.Request.Query["user"].ToString(), tenants, substrate, ct);
    }

    private static async Task<IResult> AdmitAsync(HttpContext http, string? name, string? path, byte[] bytes, string? user,
        ITenantResolver tenants, SubstrateClient substrate, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return Invalid("name_required", "name is required");
        string relative = (string.IsNullOrWhiteSpace(path) ? name : path).Replace('\\', '/');
        if (!IsRelativePath(relative))
            return Invalid("invalid_path", "path must be relative and may not contain empty, '.' or '..' segments");

        var (participant, error) = await ParticipantAsync(http, tenants, user, ct);
        if (error is not null) return error;

        var workDir = Path.Combine(Path.GetTempPath(), $"laplace-upload-{Guid.NewGuid():N}");
        try
        {
            var destPath = Path.GetFullPath(Path.Combine(workDir, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destPath.StartsWith(Path.GetFullPath(workDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return Invalid("invalid_path", "path resolves outside upload root");

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            await File.WriteAllBytesAsync(destPath, bytes, ct);

            await NativeEngineCli.RunWitnessAsync(
                participant!, TrustClass, workDir, [destPath], participant!, ct);

            string contentIdHex = Hash128.Blake3(bytes).ToHex();
            string metadataIdHex = Hash128.Text(relative).ToHex();
            string witnessIdHex = Hash128.Text(participant!).ToHex();
            string fileIdHex = HashComposer.ComputeMerkleId(
                3, [Hash128.FromBytes(Convert.FromHexString(contentIdHex)), Hash128.FromBytes(Convert.FromHexString(metadataIdHex))]).ToHex();

            return Results.Ok(new UserContentWriteResponse(
                fileIdHex, contentIdHex, metadataIdHex, witnessIdHex,
                participant!, relative, bytes.LongLength));
        }
        catch (InvalidOperationException failure)
        {
            return EndpointJson.ServiceUnavailable("upload_not_recorded", failure.Message);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
        }
    }

    internal static bool IsRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains(':'))
            return false;
        var segments = path.Split('/');
        return segments.Length > 0 && segments.All(static s => s.Length > 0 && s != "." && s != "..");
    }

    private static async Task<(string? Participant, IResult? Error)> ParticipantAsync(HttpContext http,
        ITenantResolver tenants, string? user, CancellationToken ct)
    {
        var tenant = await tenants.ResolveAsync(http, ct);
        if (!ConversationContent.IsValidIdentifier(tenant.TenantId))
            return (null, Invalid("invalid_tenant", "Tenant id must match [A-Za-z0-9._@-]{1,128}."));
        if (tenant.Claims.TryGetValue("user_id", out var claimed) && Guid.TryParse(claimed, out var authenticated))
            return (authenticated.ToString("D"), null);
        string participant = string.IsNullOrWhiteSpace(user) ? tenant.TenantId : user;
        return ConversationContent.IsValidIdentifier(participant)
            ? (participant, null)
            : (null, Invalid("invalid_user", "The user must match [A-Za-z0-9._@-]{1,128}."));
    }

    private static async Task<UploadedFile?> ReadAsync(
        SubstrateClient substrate, string participant, string idHex, CancellationToken ct)
    {
        if (idHex.Length != 32 || !idHex.All(Uri.IsHexDigit))
            return null;

        byte[] fileBytes = Convert.FromHexString(idHex);
        Hash128 witnessId = Hash128.Text(participant);

        await using var conn = await substrate.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT public.laplace_label_text($1::bytea, true),
                   (SELECT count(*) FROM public.laplace_held_closure($2::public.blake3[]) WHERE entity = $1::public.blake3)
            """,
            conn);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, fileBytes);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bytea,
            new[] { witnessId.ToBytes() });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        string? label = reader.IsDBNull(0) ? null : reader.GetString(0);
        long heldCount = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
        if (label is null || heldCount == 0)
            return null;

        byte[] contentBytes = Encoding.UTF8.GetBytes(label);
        return new UploadedFile(
            FileId: idHex.ToLowerInvariant(),
            ContentId: Hash128.Blake3(contentBytes).ToHex(),
            MetadataId: idHex.ToLowerInvariant(),
            WitnessId: witnessId.ToHex(),
            Participant: participant,
            Content: contentBytes);
    }

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
