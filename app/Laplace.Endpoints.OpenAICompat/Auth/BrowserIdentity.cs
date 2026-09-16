using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Laplace.Endpoints.OpenAICompat.Auth;

internal static class LaplaceClaimTypes
{
    public const string Tenant = "laplace:tenant";
    public const string User = "laplace:user";
    public const string Provider = "laplace:provider";
    public const string ExternalSubject = "laplace:external_subject";
    public const string WebSession = "laplace:web_session";
}

internal sealed record ExternalOidcProvider(
    string Scheme,
    string DisplayName,
    string ClientId,
    string ClientSecret,
    string Authority,
    string CallbackPath);

internal sealed class BrowserAuthSettings
{
    public const string CookieScheme = "laplace.browser";
    private readonly Dictionary<string, ExternalOidcProvider> _providers;

    public BrowserAuthSettings(IEnumerable<ExternalOidcProvider> providers)
    {
        _providers = providers.ToDictionary(p => p.Scheme, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<ExternalOidcProvider> Providers => _providers.Values;
    public bool TryGetProvider(string scheme, out ExternalOidcProvider provider) =>
        _providers.TryGetValue(scheme, out provider!);
}

internal sealed record ExternalIdentityProfile(
    string Provider,
    string Issuer,
    string Subject,
    string? Email,
    string? DisplayName,
    string? AvatarUrl);

internal sealed record IdentityAccount(
    Guid UserId,
    string TenantId,
    string Role,
    string Provider,
    string? Email,
    string? DisplayName,
    string? AvatarUrl);

internal sealed record StoredWebSession(
    string SessionId,
    Guid UserId,
    string TenantId,
    byte[] Ticket,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt);

internal sealed record WebSessionView(
    string SessionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt,
    bool Current);

internal sealed record ConversationSessionView(
    string SessionKey,
    string? Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastTurnAt);

internal interface IIdentityStore
{
    Task<IdentityAccount> UpsertExternalIdentityAsync(ExternalIdentityProfile profile, CancellationToken ct);
    Task UpsertClientAsync(ExternalOidcProvider provider, CancellationToken ct);
    Task PutWebSessionAsync(StoredWebSession session, CancellationToken ct);
    Task<StoredWebSession?> GetWebSessionAsync(string sessionId, CancellationToken ct);
    Task RevokeWebSessionAsync(string sessionId, Guid userId, CancellationToken ct);
    Task<IReadOnlyList<StoredWebSession>> ListWebSessionsAsync(Guid userId, CancellationToken ct);
    Task UpsertConversationAsync(string tenantId, Guid userId, string sessionKey, string? title, CancellationToken ct);
    Task<IReadOnlyList<ConversationSessionView>> ListConversationsAsync(Guid userId, string tenantId, CancellationToken ct);
}

internal sealed class BrowserTicketStore : ITicketStore
{
    private readonly IIdentityStore _store;
    private readonly IDataProtector _protector;

    public BrowserTicketStore(IIdentityStore store, IDataProtectionProvider dataProtection)
    {
        _store = store;
        _protector = dataProtection.CreateProtector("Laplace.BrowserTicketStore.v1");
    }

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var sessionId = $"ws-{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
        AddSessionClaim(ticket, sessionId);
        await PutAsync(sessionId, ticket, CancellationToken.None);
        return sessionId;
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        AddSessionClaim(ticket, key);
        return PutAsync(key, ticket, CancellationToken.None);
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var session = await _store.GetWebSessionAsync(key, CancellationToken.None);
        if (session is null) return null;
        try
        {
            return TicketSerializer.Default.Deserialize(_protector.Unprotect(session.Ticket));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public async Task RemoveAsync(string key)
    {
        var session = await _store.GetWebSessionAsync(key, CancellationToken.None);
        if (session is not null)
            await _store.RevokeWebSessionAsync(key, session.UserId, CancellationToken.None);
    }

    private async Task PutAsync(string key, AuthenticationTicket ticket, CancellationToken ct)
    {
        var userId = RequiredGuid(ticket.Principal, LaplaceClaimTypes.User);
        var tenantId = ticket.Principal.FindFirstValue(LaplaceClaimTypes.Tenant)
            ?? throw new InvalidOperationException("Authenticated session has no Laplace tenant claim.");
        var now = DateTimeOffset.UtcNow;
        var expires = ticket.Properties.ExpiresUtc ?? now.AddDays(14);
        await _store.PutWebSessionAsync(new StoredWebSession(
            key, userId, tenantId,
            _protector.Protect(TicketSerializer.Default.Serialize(ticket)),
            now, now, expires, null), ct);
    }

    private static void AddSessionClaim(AuthenticationTicket ticket, string sessionId)
    {
        if (ticket.Principal.Identity is not ClaimsIdentity identity) return;
        foreach (var claim in identity.FindAll(LaplaceClaimTypes.WebSession).ToArray())
            identity.RemoveClaim(claim);
        identity.AddClaim(new Claim(LaplaceClaimTypes.WebSession, sessionId));
    }

    internal static Guid RequiredGuid(ClaimsPrincipal principal, string type) =>
        Guid.TryParse(principal.FindFirstValue(type), out var value)
            ? value
            : throw new InvalidOperationException($"Authenticated session has no valid {type} claim.");
}

internal static class ExternalIdentitySignIn
{
    public static async Task HandleAsync(TokenValidatedContext context, ExternalOidcProvider provider)
    {
        var principal = context.Principal
            ?? throw new InvalidOperationException("OIDC validation returned no principal.");
        var issuer = context.SecurityToken.Issuer;
        if (provider.Scheme == "microsoft" && !ValidMicrosoftIssuer(issuer, principal.FindFirstValue("tid")))
        {
            context.Fail("Microsoft token issuer does not match its tenant claim.");
            return;
        }

        var subject = principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(issuer))
        {
            context.Fail("The identity provider did not return a stable subject and issuer.");
            return;
        }

        var email = First(principal, "email", "preferred_username", ClaimTypes.Email);
        var name = First(principal, "name", ClaimTypes.Name) ?? email;
        var avatar = First(principal, "picture");
        var store = context.HttpContext.RequestServices.GetRequiredService<IIdentityStore>();
        var account = await store.UpsertExternalIdentityAsync(new ExternalIdentityProfile(
            provider.Scheme, issuer, subject, email, name, avatar), context.HttpContext.RequestAborted);

        if (principal.Identity is not ClaimsIdentity identity)
        {
            context.Fail("The identity provider returned an unsupported principal.");
            return;
        }

        Replace(identity, ClaimTypes.NameIdentifier, account.UserId.ToString("D"));
        Replace(identity, LaplaceClaimTypes.User, account.UserId.ToString("D"));
        Replace(identity, LaplaceClaimTypes.Tenant, account.TenantId);
        Replace(identity, LaplaceClaimTypes.Provider, provider.Scheme);
        Replace(identity, LaplaceClaimTypes.ExternalSubject, subject);
        if (!string.IsNullOrWhiteSpace(account.DisplayName)) Replace(identity, ClaimTypes.Name, account.DisplayName);
        if (!string.IsNullOrWhiteSpace(account.Email)) Replace(identity, ClaimTypes.Email, account.Email);

        context.Properties ??= new AuthenticationProperties();
        context.Properties.IsPersistent = true;
        context.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(14);
    }

    private static string? First(ClaimsPrincipal principal, params string[] types) =>
        types.Select(principal.FindFirstValue).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static void Replace(ClaimsIdentity identity, string type, string value)
    {
        foreach (var claim in identity.FindAll(type).ToArray()) identity.RemoveClaim(claim);
        identity.AddClaim(new Claim(type, value));
    }

    internal static bool ValidMicrosoftIssuer(string issuer, string? tenantClaim)
    {
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
            return false;
        var path = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return path.Length == 2
            && path[1].Equals("v2.0", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(tenantClaim)
            && path[0].Equals(tenantClaim, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class IdentityClientBootstrapService : IHostedService
{
    private readonly BrowserAuthSettings _settings;
    private readonly IIdentityStore _store;

    public IdentityClientBootstrapService(BrowserAuthSettings settings, IIdentityStore store)
    {
        _settings = settings;
        _store = store;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var provider in _settings.Providers)
            await _store.UpsertClientAsync(provider, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class BrowserAuthentication
{
    public static OpenIdConnectOptions Configure(
        OpenIdConnectOptions options, ExternalOidcProvider provider)
    {
        options.SignInScheme = BrowserAuthSettings.CookieScheme;
        options.Authority = provider.Authority;
        options.ClientId = provider.ClientId;
        options.ClientSecret = provider.ClientSecret;
        options.CallbackPath = provider.CallbackPath;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.SaveTokens = false;
        options.MapInboundClaims = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.ValidateIssuer = provider.Scheme != "microsoft";
        options.Events.OnTokenValidated = context => ExternalIdentitySignIn.HandleAsync(context, provider);
        return options;
    }
}
