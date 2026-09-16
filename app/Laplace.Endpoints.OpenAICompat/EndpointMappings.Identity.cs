using System.Security.Claims;
using Laplace.Api.Contracts;
using Laplace.Endpoints.OpenAICompat.Auth;
using Microsoft.AspNetCore.Authentication;

namespace Laplace.Endpoints.OpenAICompat;

internal static class IdentityEndpoints
{
    public static void MapIdentityEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/auth/config", (BrowserAuthSettings settings) =>
            Results.Json(new AuthConfigResponse(
                settings.Providers.Select(ProviderView).ToArray())))
            .WithTags("identity")
            .Produces<AuthConfigResponse>();

        app.MapGet("/v1/auth/me", (HttpContext context, BrowserAuthSettings settings) =>
        {
            var principal = context.User;
            if (!TryIdentity(principal, out var userId, out var tenantId))
                return Results.Json(new AuthMeResponse(
                    false, null, settings.Providers.Select(ProviderView).ToArray()));

            return Results.Json(new AuthMeResponse(
                true,
                new AuthUserView(
                    userId,
                    tenantId,
                    principal.FindFirstValue(ClaimTypes.Name),
                    principal.FindFirstValue(ClaimTypes.Email),
                    principal.FindFirstValue(LaplaceClaimTypes.Provider)),
                settings.Providers.Select(ProviderView).ToArray()));
        })
        .WithTags("identity")
        .Produces<AuthMeResponse>();

        app.MapGet("/v1/auth/login/{provider}", (
            string provider, string? returnUrl, BrowserAuthSettings settings) =>
        {
            if (!settings.TryGetProvider(provider, out var configured))
                return Results.Json(new ErrorResponse(new ErrorBody(
                    "authentication_error", "provider_not_configured",
                    $"Identity provider '{provider}' is not configured on this Laplace host.")),
                    statusCode: StatusCodes.Status404NotFound);

            var destination = LocalReturnUrl(returnUrl);
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = destination },
                [configured.Scheme]);
        })
        .WithTags("identity")
        .Produces(StatusCodes.Status302Found)
        .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        app.MapPost("/v1/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(BrowserAuthSettings.CookieScheme);
            return Results.NoContent();
        })
        .WithTags("identity")
        .Produces(StatusCodes.Status204NoContent);

        app.MapGet("/v1/auth/sessions", async (
            HttpContext context, IIdentityStore store, CancellationToken ct) =>
        {
            if (!TryIdentity(context.User, out var userId, out _)) return Unauthorized();
            var current = context.User.FindFirstValue(LaplaceClaimTypes.WebSession);
            var sessions = await store.ListWebSessionsAsync(userId, ct);
            return Results.Json(new WebSessionsResponse(sessions.Select(session =>
                new WebSessionView(
                    session.SessionId,
                    session.CreatedAt,
                    session.LastSeenAt,
                    session.ExpiresAt,
                    string.Equals(session.SessionId, current, StringComparison.Ordinal))).ToArray()));
        })
        .WithTags("identity")
        .Produces<WebSessionsResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized);

        app.MapDelete("/v1/auth/sessions/{sessionId}", async (
            string sessionId, HttpContext context, IIdentityStore store, CancellationToken ct) =>
        {
            if (!TryIdentity(context.User, out var userId, out _)) return Unauthorized();
            await store.RevokeWebSessionAsync(sessionId, userId, ct);
            if (string.Equals(
                    sessionId,
                    context.User.FindFirstValue(LaplaceClaimTypes.WebSession),
                    StringComparison.Ordinal))
                await context.SignOutAsync(BrowserAuthSettings.CookieScheme);
            return Results.NoContent();
        })
        .WithTags("identity")
        .Produces(StatusCodes.Status204NoContent)
        .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized);

        app.MapGet("/v1/auth/conversations", async (
            HttpContext context, IIdentityStore store, CancellationToken ct) =>
        {
            if (!TryIdentity(context.User, out var userId, out var tenantId)) return Unauthorized();
            var conversations = await store.ListConversationsAsync(userId, tenantId, ct);
            return Results.Json(new ConversationSessionsResponse(conversations));
        })
        .WithTags("identity")
        .Produces<ConversationSessionsResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized);
    }

    private static AuthProviderView ProviderView(ExternalOidcProvider provider) =>
        new(provider.Scheme, provider.DisplayName, $"/v1/auth/login/{provider.Scheme}");

    private static string LocalReturnUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith("/", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.Contains('\\')
            || value.Length > 2048)
            return "/";
        return value;
    }

    private static bool TryIdentity(
        ClaimsPrincipal principal, out Guid userId, out string tenantId)
    {
        userId = default;
        tenantId = principal.FindFirstValue(LaplaceClaimTypes.Tenant) ?? "";
        return principal.Identity?.IsAuthenticated == true
            && Guid.TryParse(principal.FindFirstValue(LaplaceClaimTypes.User), out userId)
            && !string.IsNullOrWhiteSpace(tenantId);
    }

    private static IResult Unauthorized() => Results.Json(
        new ErrorResponse(new ErrorBody(
            "authentication_error", "authentication_required", "Sign in to access this identity resource.")),
        statusCode: StatusCodes.Status401Unauthorized);

    internal sealed record AuthProviderView(string Id, string DisplayName, string LoginUrl);
    internal sealed record AuthConfigResponse(IReadOnlyList<AuthProviderView> Providers);
    internal sealed record AuthUserView(
        Guid Id, string TenantId, string? DisplayName, string? Email, string? Provider);
    internal sealed record AuthMeResponse(
        bool Authenticated, AuthUserView? User, IReadOnlyList<AuthProviderView> Providers);
    internal sealed record WebSessionsResponse(IReadOnlyList<WebSessionView> Sessions);
    internal sealed record ConversationSessionsResponse(IReadOnlyList<ConversationSessionView> Conversations);
}
