using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Laplace.Endpoints.OpenAICompat.Auth;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed record LichessControlClientConfiguration(string Url, string TokenFile, Guid[] Operators);
internal sealed record LichessProfileSelectionRequest(string? Profile, long ExpectedRevision,
    string? ReadSource = null, string? RecordingTarget = null);

internal sealed class LichessControlClient(IHttpClientFactory clients, ILogger<LichessControlClient> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static LichessControlClientConfiguration? Configuration()
    {
        var path = Environment.GetEnvironmentVariable("LAPLACE_LICHESS_CONTROL_CLIENT");
        if (string.IsNullOrWhiteSpace(path)) return null;
        var c = JsonSerializer.Deserialize<LichessControlClientConfiguration>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Empty Lichess control client configuration.");
        var uri = new Uri(c.Url);
        bool tailnet = IPAddress.TryParse(uri.Host, out var address) && address.GetAddressBytes() is [100, >= 64 and <= 127, _, _];
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && (uri.IsLoopback || tailnet)))
            throw new InvalidDataException("Lichess control requires HTTPS, loopback or an explicit Tailscale address.");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !Path.IsPathFullyQualified(c.TokenFile) || c.Operators is null)
            throw new InvalidDataException("Invalid Lichess control client credential or operator declarations.");
        return c;
    }

    private static Guid? User(ClaimsPrincipal principal) => principal.Identity?.IsAuthenticated == true
        && Guid.TryParse(principal.FindFirstValue(LaplaceClaimTypes.User), out var user) ? user : null;

    public async Task<IResult> StatusAsync(HttpContext context, CancellationToken ct)
        => await GuardAsync(() => ReadStatusAsync(context, ct), ct);

    private async Task<IResult> ReadStatusAsync(HttpContext context, CancellationToken ct)
    {
        var c = Configuration();
        if (c is null) return Results.Json(new { available = false, canControl = false, error = "The shared Lichess controller has not been installed for this environment." });
        using var response = await SendAsync(c, HttpMethod.Get, "status", null, ct);
        if (!response.IsSuccessStatusCode) return Failure(response.StatusCode);
        var control = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        return Results.Json(new { available = true, canControl = User(context.User) is { } user && c.Operators.Contains(user), control });
    }

    public async Task<IResult> SelectAsync(HttpContext context, LichessProfileSelectionRequest request, CancellationToken ct)
        => await GuardAsync(() => ApplySelectionAsync(context, request, ct), ct);

    private async Task<IResult> ApplySelectionAsync(HttpContext context, LichessProfileSelectionRequest request, CancellationToken ct)
    {
        var c = Configuration();
        if (c is null) return Results.Json(new { error = "Lichess controller is not configured." }, statusCode: 503);
        if (User(context.User) is not { } user || !c.Operators.Contains(user))
            return Results.Json(new { error = "Your signed-in account does not have the lichess.configure permission." }, statusCode: 403);
        using var response = await SendAsync(c, HttpMethod.Post, "selection",
            new { request.Profile, request.ExpectedRevision, request.ReadSource, request.RecordingTarget,
                actor = $"{Environment.MachineName}/{user:D}" }, ct);
        if (!response.IsSuccessStatusCode && response.StatusCode is not (HttpStatusCode.Conflict or HttpStatusCode.BadRequest))
            return Failure(response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        return Results.Json(result, statusCode: (int)response.StatusCode);
    }

    private async Task<HttpResponseMessage> SendAsync(LichessControlClientConfiguration c, HttpMethod method,
        string path, object? body, CancellationToken ct)
    {
        var token = (await File.ReadAllTextAsync(c.TokenFile, ct)).Trim();
        using var request = new HttpRequestMessage(method, new Uri(new Uri(c.Url.TrimEnd('/') + "/"), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await clients.CreateClient("lichess-control").SendAsync(request, ct);
    }

    private static IResult Failure(HttpStatusCode status) => Results.Json(new
    { error = "The installed Lichess controller rejected the request.", upstreamStatus = (int)status }, statusCode: 503);

    private async Task<IResult> GuardAsync(Func<Task<IResult>> action, CancellationToken ct)
    {
        try { return await action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException
            or JsonException or UriFormatException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            log.LogError(ex, "The configured Lichess controller could not be reached or its installation could not be read");
            return Results.Json(new { error = "The configured Lichess controller is unavailable. Check the chess credential service and controller journal on the declared host." }, statusCode: 503);
        }
    }
}
