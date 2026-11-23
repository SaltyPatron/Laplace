using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Laplace.Engine.Core;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed class PatreonOptions
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? WebhookSecret { get; set; }
    public string? CampaignId { get; set; }
    public string? RedirectUri { get; set; }

    public bool OAuthConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
    public bool WebhookConfigured => !string.IsNullOrWhiteSpace(WebhookSecret);

    public static PatreonOptions LoadFromEnvironment() => new()
    {
        ClientId = LaplaceInstall.TryReadConfig("LAPLACE_PATREON_CLIENT_ID", "patreon.env"),
        ClientSecret = LaplaceInstall.TryReadConfig("LAPLACE_PATREON_CLIENT_SECRET", "patreon.env"),
        WebhookSecret = LaplaceInstall.TryReadConfig("LAPLACE_PATREON_WEBHOOK_SECRET", "patreon.env"),
        CampaignId = LaplaceInstall.TryReadConfig("LAPLACE_PATREON_CAMPAIGN_ID", "patreon.env"),
        RedirectUri = LaplaceInstall.TryReadConfig("LAPLACE_PATREON_REDIRECT_URI", "patreon.env"),
    };
}

internal sealed record PatreonLinkRecord(
    string TenantId,
    string PatreonUserId,
    string? PatreonMemberId,
    string? Email,
    string? Tier,
    long PledgeCents,
    string Status,
    string? MappedPlanId,
    IReadOnlyList<string> GrantedPackages,
    DateTimeOffset LinkedAt,
    DateTimeOffset UpdatedAt);

internal sealed record PatreonStatusView(
    [property: JsonPropertyName("oauth_configured")] bool OAuthConfigured,
    [property: JsonPropertyName("webhook_configured")] bool WebhookConfigured,
    [property: JsonPropertyName("campaign_id")] string? CampaignId,
    [property: JsonPropertyName("tenant")] string Tenant,
    [property: JsonPropertyName("linked")] bool Linked,
    [property: JsonPropertyName("patreon_user_id")] string? PatreonUserId,
    [property: JsonPropertyName("patreon_email")] string? PatreonEmail,
    [property: JsonPropertyName("tier")] string? Tier,
    [property: JsonPropertyName("pledge_cents")] long PledgeCents,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("mapped_plan_id")] string? MappedPlanId,
    [property: JsonPropertyName("granted_packages")] IReadOnlyList<string> GrantedPackages,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt);

internal sealed record PatreonConnectResult(
    [property: JsonPropertyName("configured")] bool Configured,
    [property: JsonPropertyName("authorize_url")] string? AuthorizeUrl,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("redirect_uri")] string? RedirectUri,
    [property: JsonPropertyName("campaign_id")] string? CampaignId,
    [property: JsonPropertyName("tenant")] string Tenant);

internal sealed record PatreonCallbackResult(
    [property: JsonPropertyName("linked")] bool Success,
    [property: JsonPropertyName("error_code"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorCode,
    [property: JsonPropertyName("error_message"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorMessage,
    [property: JsonIgnore] int StatusCode,
    [property: JsonPropertyName("tenant")] string? Tenant,
    [property: JsonPropertyName("patreon_user_id")] string? PatreonUserId,
    [property: JsonPropertyName("tier")] string? Tier,
    [property: JsonPropertyName("pledge_cents")] long PledgeCents,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("mapped_plan_id")] string? MappedPlanId,
    [property: JsonPropertyName("granted_packages")] IReadOnlyList<string> GrantedPackages);

internal sealed record PatreonWebhookResult(
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("verified")] bool Verified,
    [property: JsonPropertyName("duplicate")] bool Duplicate,
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("tenant")] string? Tenant,
    [property: JsonPropertyName("patreon_user_id")] string? PatreonUserId,
    [property: JsonPropertyName("mapped_plan_id")] string? MappedPlanId,
    [property: JsonPropertyName("pledge_cents")] long PledgeCents,
    [property: JsonPropertyName("granted_packages")] IReadOnlyList<string> GrantedPackages);

internal interface IPatreonEntitlementService
{
    bool VerifySignature(string payload, string? signatureHeader);
    Task<PatreonStatusView> GetStatusAsync(string tenantId, CancellationToken ct = default);
    PatreonConnectResult CreateConnectUrl(string tenantId, string? defaultRedirectBase = null);
    Task<PatreonCallbackResult> HandleCallbackAsync(
        string? code, string? state, string? defaultRedirectBase = null, CancellationToken ct = default);
    Task<PatreonWebhookResult> HandleWebhookAsync(
        string payload, string? signatureHeader, string? eventTypeHeader, string? deliveryIdHeader = null,
        CancellationToken ct = default);
    (BillingPlan? Plan, IReadOnlyList<string> Packages) MapPledgeToPlanAndPackages(long pledgeCents, string? tierTitle);
}

internal sealed class PatreonEntitlementService : IPatreonEntitlementService
{
    private const string AuthorizeEndpoint = "https://www.patreon.com/oauth2/authorize";
    private const string TokenEndpoint = "https://www.patreon.com/api/oauth2/token";
    private const string IdentityEndpoint = "https://www.patreon.com/api/oauth2/v2/identity"
        + "?include=memberships,memberships.currently_entitled_tiers,memberships.campaign"
        + "&fields%5Buser%5D=email,full_name"
        + "&fields%5Bmember%5D=patron_status,currently_entitled_amount_cents,next_charge_date,pledge_relationship_start,email"
        + "&fields%5Btier%5D=title,amount_cents";

    private readonly IBillingCatalog _catalog;
    private readonly IBillingEntitlementStore _entitlements;
    private readonly IBillingWebhookEventStore _webhookEvents;
    private readonly IKnowledgePackageStore _packages;
    private readonly PatreonOptions _options;
    private readonly NpgsqlDataSource? _dataSource;
    private readonly HttpMessageHandler? _httpHandler;
    private readonly ConcurrentDictionary<string, PatreonLinkRecord> _linksByTenant = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _usedStates = new(StringComparer.Ordinal);
    private long _lastEventCreated;

    public PatreonEntitlementService(
        IBillingCatalog catalog,
        IBillingEntitlementStore entitlements,
        IBillingWebhookEventStore webhookEvents,
        IKnowledgePackageStore? packages = null,
        PatreonOptions? options = null,
        NpgsqlDataSource? dataSource = null,
        HttpMessageHandler? httpHandler = null)
    {
        _catalog = catalog;
        _entitlements = entitlements;
        _webhookEvents = webhookEvents;
        _packages = packages ?? new KnowledgePackageService();
        _options = options ?? PatreonOptions.LoadFromEnvironment();
        _dataSource = dataSource;
        _httpHandler = httpHandler;
    }

    [ActivatorUtilitiesConstructor]
    public PatreonEntitlementService(
        IServiceProvider services,
        IBillingCatalog catalog,
        IBillingEntitlementStore entitlements,
        IBillingWebhookEventStore webhookEvents,
        IKnowledgePackageStore? packages = null,
        IOptions<PatreonOptions>? options = null)
        : this(
            catalog,
            entitlements,
            webhookEvents,
            packages,
            ResolveOptionsFromDi(services, options),
            KnowledgePackageService.ResolvePostgresDataSource(services),
            httpHandler: null)
    { }

    private static PatreonOptions ResolveOptionsFromDi(IServiceProvider services, IOptions<PatreonOptions>? options)
    {
        bool hasDiConfig = services.GetService<IConfigureOptions<PatreonOptions>>() is not null
            || services.GetService<IPostConfigureOptions<PatreonOptions>>() is not null;
        if (hasDiConfig && options is not null)
            return options.Value;
        return PatreonOptions.LoadFromEnvironment();
    }

    public bool VerifySignature(string payload, string? signatureHeader)
    {
        var secret = _options.WebhookSecret;
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(signatureHeader))
            return false;
        var sigHex = signatureHeader.Trim();
        if (sigHex.Length != 32)
            return false;

        Span<byte> provided = stackalloc byte[16];
        if (Convert.FromHexString(sigHex, provided, out _, out _) != OperationStatus.Done)
            return false;

        Span<byte> expected = stackalloc byte[16];
        HMACMD5.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(payload ?? string.Empty),
            expected);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    public async Task<PatreonStatusView> GetStatusAsync(string tenantId, CancellationToken ct = default)
    {
        var link = await FindLinkByTenantAsync(tenantId, ct).ConfigureAwait(false);
        return new PatreonStatusView(
            OAuthConfigured: _options.OAuthConfigured,
            WebhookConfigured: _options.WebhookConfigured,
            CampaignId: _options.CampaignId,
            Tenant: tenantId,
            Linked: link is not null,
            PatreonUserId: link?.PatreonUserId,
            PatreonEmail: link?.Email,
            Tier: link?.Tier,
            PledgeCents: link?.PledgeCents ?? 0,
            Status: link?.Status ?? "unlinked",
            MappedPlanId: link?.MappedPlanId,
            GrantedPackages: link?.GrantedPackages ?? Array.Empty<string>(),
            UpdatedAt: link?.UpdatedAt);
    }

    public PatreonConnectResult CreateConnectUrl(string tenantId, string? defaultRedirectBase = null)
    {
        if (!_options.OAuthConfigured)
            return new PatreonConnectResult(false, null, null, null, _options.CampaignId, tenantId);

        var redirectUri = ResolveRedirectUri(defaultRedirectBase);
        var state = CreateSignedState(tenantId);
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = _options.ClientId!,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "identity identity[email] identity.memberships",
            ["state"] = state,
        };
        var url = QueryHelpers.AddQueryString(AuthorizeEndpoint, query);
        return new PatreonConnectResult(true, url, state, redirectUri, _options.CampaignId, tenantId);
    }

    public async Task<PatreonCallbackResult> HandleCallbackAsync(
        string? code, string? state, string? defaultRedirectBase = null, CancellationToken ct = default)
    {
        if (!_options.OAuthConfigured)
        {
            return CallbackError(StatusCodes.Status503ServiceUnavailable, "patreon_not_configured",
                "Patreon OAuth is not configured on this host (set LAPLACE_PATREON_CLIENT_ID and LAPLACE_PATREON_CLIENT_SECRET or deploy/secrets/patreon.env).");
        }
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            return CallbackError(StatusCodes.Status400BadRequest, "invalid_request_error",
                "Query parameters 'code' and 'state' are required.");
        }
        if (!TryValidateState(state, out var tenantId))
        {
            return CallbackError(StatusCodes.Status400BadRequest, "invalid_oauth_state",
                "The Patreon OAuth state token is invalid, expired, or has already been redeemed.");
        }

        string? accessToken;
        try
        {
            accessToken = await ExchangeCodeForAccessTokenAsync(code.Trim(), ResolveRedirectUri(defaultRedirectBase), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return CallbackError(StatusCodes.Status502BadGateway, "patreon_exchange_failed",
                $"Failed to exchange Patreon OAuth authorization code: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return CallbackError(StatusCodes.Status502BadGateway, "patreon_exchange_failed",
                "Patreon OAuth token response did not include an access token.");
        }

        JsonDocument identityDoc;
        try
        {
            identityDoc = await FetchPatreonIdentityAsync(accessToken, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return CallbackError(StatusCodes.Status502BadGateway, "patreon_identity_failed",
                $"Failed to fetch Patreon identity/memberships: {ex.Message}");
        }

        using (identityDoc)
        {
            var extracted = ExtractIdentityMembership(identityDoc.RootElement, _options.CampaignId);
            if (string.IsNullOrWhiteSpace(extracted.PatreonUserId))
            {
                return CallbackError(StatusCodes.Status502BadGateway, "patreon_identity_invalid",
                    "Patreon identity payload did not contain a user id.");
            }

            var link = await ApplyPatreonMembershipAsync(
                tenantId,
                extracted.PatreonUserId,
                extracted.MemberId,
                extracted.Email,
                extracted.TierTitle,
                extracted.PledgeCents,
                extracted.PatronStatus,
                extracted.PeriodStart,
                extracted.PeriodEnd,
                ct).ConfigureAwait(false);

            return new PatreonCallbackResult(
                Success: true,
                ErrorCode: null,
                ErrorMessage: null,
                StatusCode: StatusCodes.Status200OK,
                Tenant: link.TenantId,
                PatreonUserId: link.PatreonUserId,
                Tier: link.Tier,
                PledgeCents: link.PledgeCents,
                Status: link.Status,
                MappedPlanId: link.MappedPlanId,
                GrantedPackages: link.GrantedPackages);
        }
    }

    public async Task<PatreonWebhookResult> HandleWebhookAsync(
        string payload, string? signatureHeader, string? eventTypeHeader, string? deliveryIdHeader = null,
        CancellationToken ct = default)
    {
        var eventType = string.IsNullOrWhiteSpace(eventTypeHeader) ? "unknown" : eventTypeHeader.Trim();
        var eventId = ComputeEventId(eventType, deliveryIdHeader, payload);

        if (!_options.WebhookConfigured)
            return WebhookFailure(false, eventId, eventType, "webhook_secret_unconfigured");

        if (!VerifySignature(payload, signatureHeader))
            return WebhookFailure(false, eventId, eventType, "invalid_signature");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return WebhookFailure(true, eventId, eventType, "invalid_json");
        }

        using (doc)
        {
            if (eventType == "unknown")
                eventType = Text(doc.RootElement, "event") ?? "members:pledge:update";

            if (!await _webhookEvents.TryBeginAsync(eventId, eventType, ct).ConfigureAwait(false))
            {
                var status = await _webhookEvents.GetStatusAsync(eventId, ct).ConfigureAwait(false);
                var complete = status is not null && status != "retryable"
                    && !status.StartsWith("processing:", StringComparison.Ordinal);
                return new PatreonWebhookResult(
                    Accepted: complete,
                    Verified: true,
                    Duplicate: true,
                    EventId: eventId,
                    EventType: eventType,
                    Status: complete ? "duplicate" : "processing",
                    Tenant: null,
                    PatreonUserId: null,
                    MappedPlanId: null,
                    PledgeCents: 0,
                    GrantedPackages: Array.Empty<string>());
            }

            try
            {
                var parsed = ExtractWebhookMember(doc.RootElement, eventType);
                if (string.IsNullOrWhiteSpace(parsed.PatreonUserId))
                {
                    await _webhookEvents.CompleteAsync(eventId, "invalid_event", ct).ConfigureAwait(false);
                    return WebhookFailure(true, eventId, eventType, "invalid_event");
                }

                var tenantId = await ResolveWebhookTenantAsync(parsed, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(tenantId))
                {
                    await _webhookEvents.CompleteAsync(eventId, "unlinked_patron", ct).ConfigureAwait(false);
                    return new PatreonWebhookResult(
                        Accepted: true,
                        Verified: true,
                        Duplicate: false,
                        EventId: eventId,
                        EventType: eventType,
                        Status: "unlinked_patron",
                        Tenant: null,
                        PatreonUserId: parsed.PatreonUserId,
                        MappedPlanId: null,
                        PledgeCents: parsed.PledgeCents,
                        GrantedPackages: Array.Empty<string>());
                }

                var link = await ApplyPatreonMembershipAsync(
                    tenantId,
                    parsed.PatreonUserId,
                    parsed.MemberId,
                    parsed.Email,
                    parsed.TierTitle,
                    parsed.PledgeCents,
                    parsed.PatronStatus,
                    parsed.PeriodStart,
                    parsed.PeriodEnd,
                    ct).ConfigureAwait(false);

                await _webhookEvents.CompleteAsync(eventId, link.Status, ct).ConfigureAwait(false);
                return new PatreonWebhookResult(
                    Accepted: true,
                    Verified: true,
                    Duplicate: false,
                    EventId: eventId,
                    EventType: eventType,
                    Status: link.Status,
                    Tenant: link.TenantId,
                    PatreonUserId: link.PatreonUserId,
                    MappedPlanId: link.MappedPlanId,
                    PledgeCents: link.PledgeCents,
                    GrantedPackages: link.GrantedPackages);
            }
            catch
            {
                await _webhookEvents.CompleteAsync(eventId, "retryable", CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
    }

    public (BillingPlan? Plan, IReadOnlyList<string> Packages) MapPledgeToPlanAndPackages(
        long pledgeCents, string? tierTitle)
    {
        if (pledgeCents <= 0)
            return (null, Array.Empty<string>());

        var plans = _catalog.ListPlans();
        string targetPlanId = ResolvePlanId(pledgeCents, tierTitle);
        var plan = plans.FirstOrDefault(p => string.Equals(p.PlanId, targetPlanId, StringComparison.OrdinalIgnoreCase))
            ?? plans.FirstOrDefault(p => string.Equals(p.PlanId, "developer", StringComparison.OrdinalIgnoreCase));

        IReadOnlyList<string> granted = targetPlanId switch
        {
            "enterprise" => ["core-open", "domain-chess", "domain-code"],
            "studio" => ["core-open", "domain-chess", "domain-code"],
            "developer" when pledgeCents >= 2900 => ["core-open", "domain-chess"],
            "league" => ["core-open", "domain-chess"],
            _ => ["core-open"],
        };
        return (plan, granted);
    }

    private static string ResolvePlanId(long pledgeCents, string? tierTitle)
    {
        var tier = tierTitle?.Trim().ToLowerInvariant() ?? string.Empty;
        if (tier.Contains("enterprise", StringComparison.Ordinal))
            return "enterprise";
        if (tier.Contains("studio", StringComparison.Ordinal) || tier.Contains("pro", StringComparison.Ordinal))
            return "studio";
        if (tier.Contains("league", StringComparison.Ordinal))
            return "league";
        if (tier.Contains("developer", StringComparison.Ordinal) || tier.Contains("builder", StringComparison.Ordinal))
            return "developer";

        if (pledgeCents >= 49900) return "enterprise";
        if (pledgeCents >= 9900) return "studio";
        return "developer";
    }

    private async Task<PatreonLinkRecord> ApplyPatreonMembershipAsync(
        string tenantId,
        string patreonUserId,
        string? memberId,
        string? email,
        string? tierTitle,
        long pledgeCents,
        string? patronStatus,
        DateTimeOffset? periodStart,
        DateTimeOffset? periodEnd,
        CancellationToken ct)
    {
        var previous = await FindLinkByTenantAsync(tenantId, ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var patreonSubId = $"patreon:{patreonUserId}";

        bool active = pledgeCents > 0
            && !string.Equals(patronStatus, "former_patron", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(patronStatus, "declined_patron", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(patronStatus, "canceled", StringComparison.OrdinalIgnoreCase);

        if (!active)
        {
            await _entitlements.DeactivateSubscriptionAsync(patreonSubId, "canceled", ct).ConfigureAwait(false);
            if (previous is not null)
            {
                foreach (var oldPkg in previous.GrantedPackages)
                {
                    if (!string.Equals(oldPkg, "core-open", StringComparison.OrdinalIgnoreCase))
                        await _packages.RevokeGrantAsync(oldPkg, tenantId, ct).ConfigureAwait(false);
                }
            }
            var canceled = new PatreonLinkRecord(
                TenantId: tenantId,
                PatreonUserId: patreonUserId,
                PatreonMemberId: memberId ?? previous?.PatreonMemberId,
                Email: email ?? previous?.Email,
                Tier: tierTitle ?? previous?.Tier,
                PledgeCents: 0,
                Status: "canceled",
                MappedPlanId: null,
                GrantedPackages: Array.Empty<string>(),
                LinkedAt: previous?.LinkedAt ?? now,
                UpdatedAt: now);
            await UpsertLinkAsync(canceled, ct).ConfigureAwait(false);
            return canceled;
        }

        var (plan, mappedPackages) = MapPledgeToPlanAndPackages(pledgeCents, tierTitle);
        var start = periodStart ?? now;
        var end = periodEnd is { } pe && pe > start ? pe : start.AddMonths(1);
        long monotonicCreated = Interlocked.Increment(ref _lastEventCreated);
        long timestampMs = now.ToUnixTimeMilliseconds();
        if (timestampMs > monotonicCreated)
            Interlocked.Exchange(ref _lastEventCreated, timestampMs);
        long version = Math.Max(monotonicCreated, timestampMs);

        if (plan is not null)
        {
            await _entitlements.ApplySubscriptionAsync(plan, new BillingSubscriptionState(
                Tenant: tenantId,
                SubscriptionId: patreonSubId,
                CustomerId: null,
                Status: "active",
                PeriodStart: start,
                PeriodEnd: end,
                EventCreated: version,
                CancelAtPeriodEnd: false), ct).ConfigureAwait(false);
        }

        var grantedPackages = new List<string>(mappedPackages.Count);
        foreach (var pkgId in mappedPackages)
        {
            var pkg = await _packages.TryGetAsync(pkgId, ct).ConfigureAwait(false);
            if (pkg is null)
                continue;
            await _packages.GrantPackageAsync(pkg.PackageId, tenantId, "patreon", ct).ConfigureAwait(false);
            grantedPackages.Add(pkg.PackageId);
        }

        var activeRecord = new PatreonLinkRecord(
            TenantId: tenantId,
            PatreonUserId: patreonUserId,
            PatreonMemberId: memberId ?? previous?.PatreonMemberId,
            Email: email ?? previous?.Email,
            Tier: tierTitle ?? plan?.Name ?? "Patron",
            PledgeCents: pledgeCents,
            Status: "active_patron",
            MappedPlanId: plan?.PlanId,
            GrantedPackages: grantedPackages,
            LinkedAt: previous?.LinkedAt ?? now,
            UpdatedAt: now);
        await UpsertLinkAsync(activeRecord, ct).ConfigureAwait(false);
        return activeRecord;
    }

    private string ResolveRedirectUri(string? defaultRedirectBase)
    {
        if (!string.IsNullOrWhiteSpace(_options.RedirectUri))
            return _options.RedirectUri.Trim();
        var baseUrl = LaplaceInstall.TryReadConfig("LAPLACE_PUBLIC_BASE_URL")
            ?? defaultRedirectBase
            ?? LaplaceInstall.EndpointBaseUrl;
        return $"{baseUrl.TrimEnd('/')}/v1/billing/patreon/callback";
    }

    private string CreateSignedState(string tenantId)
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds();
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        var payload = $"{tenantId}\n{expires}\n{nonce}";
        var payloadB64 = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
        var sig = HMACSHA256.HashData(Encoding.UTF8.GetBytes(_options.ClientSecret!), Encoding.UTF8.GetBytes(payloadB64));
        return $"{payloadB64}.{WebEncoders.Base64UrlEncode(sig)}";
    }

    private bool TryValidateState(string state, out string tenantId)
    {
        tenantId = string.Empty;
        if (string.IsNullOrWhiteSpace(_options.ClientSecret))
            return false;

        int dot = state.IndexOf('.');
        if (dot <= 0 || dot == state.Length - 1)
            return false;

        string payloadB64 = state[..dot];
        string sigB64 = state[(dot + 1)..];
        byte[] providedSig;
        try
        {
            providedSig = WebEncoders.Base64UrlDecode(sigB64);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] expectedSig = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(_options.ClientSecret),
            Encoding.UTF8.GetBytes(payloadB64));
        if (providedSig.Length != expectedSig.Length || !CryptographicOperations.FixedTimeEquals(expectedSig, providedSig))
            return false;

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(payloadB64));
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = decoded.Split('\n');
        if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[0]) || !long.TryParse(parts[1], out var expiresUnix))
            return false;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresUnix)
            return false;
        if (!_usedStates.TryAdd(parts[2], DateTimeOffset.FromUnixTimeSeconds(expiresUnix)))
            return false;

        tenantId = parts[0];
        return true;
    }

    private async Task<string?> ExchangeCodeForAccessTokenAsync(string code, string redirectUri, CancellationToken ct)
    {
        using var client = CreateHttpClient();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["client_id"] = _options.ClientId!,
            ["client_secret"] = _options.ClientSecret!,
            ["redirect_uri"] = redirectUri,
        });
        using var response = await client.PostAsync(TokenEndpoint, form, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Patreon token endpoint returned {(int)response.StatusCode}.");
        using var doc = JsonDocument.Parse(json);
        return Text(doc.RootElement, "access_token");
    }

    private async Task<JsonDocument> FetchPatreonIdentityAsync(string accessToken, CancellationToken ct)
    {
        using var client = CreateHttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, IdentityEndpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(req, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Patreon identity endpoint returned {(int)response.StatusCode}.");
        return JsonDocument.Parse(json);
    }

    private HttpClient CreateHttpClient() =>
        _httpHandler is not null ? new HttpClient(_httpHandler, disposeHandler: false) : new HttpClient();

    private static string ComputeEventId(string eventType, string? deliveryIdHeader, string payload)
    {
        if (!string.IsNullOrWhiteSpace(deliveryIdHeader))
            return $"patreon:{deliveryIdHeader.Trim()}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{eventType}\n{payload}"));
        return $"patreon:{Convert.ToHexStringLower(digest)}";
    }

    private sealed record ParsedMemberEvent(
        string? PatreonUserId,
        string? MemberId,
        string? Email,
        string? TierTitle,
        long PledgeCents,
        string? PatronStatus,
        string? ExplicitTenant,
        DateTimeOffset? PeriodStart,
        DateTimeOffset? PeriodEnd);

    private static ParsedMemberEvent ExtractWebhookMember(JsonElement root, string eventType)
    {
        var data = Child(root, "data");
        var attrs = Child(data, "attributes");
        var rels = Child(data, "relationships");
        var included = Child(root, "included");
        var meta = Child(root, "meta");

        string? memberId = Text(data, "id");
        string? patreonUserId = Text(Child(Child(rels, "user"), "data"), "id")
            ?? Text(attrs, "patreon_user_id")
            ?? Text(root, "patreon_user_id")
            ?? memberId;

        string? email = Text(attrs, "email") ?? Text(root, "email");
        string? patronStatus = Text(attrs, "patron_status") ?? Text(root, "patron_status");
        if (eventType.EndsWith(":delete", StringComparison.OrdinalIgnoreCase))
            patronStatus = "former_patron";

        long pledgeCents = Long(attrs, "currently_entitled_amount_cents")
            ?? Long(attrs, "pledge_cents")
            ?? Long(root, "pledge_cents")
            ?? 0;
        if (eventType.EndsWith(":delete", StringComparison.OrdinalIgnoreCase))
            pledgeCents = 0;

        string? tierTitle = Text(attrs, "tier") ?? Text(root, "tier");
        var entitledTierIds = ExtractRelationshipIds(Child(rels, "currently_entitled_tiers"));
        if (included.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in included.EnumerateArray())
            {
                var type = Text(item, "type");
                var id = Text(item, "id");
                var incAttrs = Child(item, "attributes");
                if (type == "user" && id == patreonUserId)
                    email ??= Text(incAttrs, "email");
                if (type == "tier" && (id is null || entitledTierIds.Count == 0 || entitledTierIds.Contains(id)))
                    tierTitle ??= Text(incAttrs, "title");
            }
        }

        string? explicitTenant = Text(attrs, "tenant")
            ?? Text(attrs, "laplace_tenant")
            ?? Text(meta, "tenant")
            ?? Text(root, "tenant");

        return new ParsedMemberEvent(
            patreonUserId,
            memberId,
            email,
            tierTitle,
            pledgeCents,
            patronStatus,
            explicitTenant,
            ParseTime(Text(attrs, "pledge_relationship_start")),
            ParseTime(Text(attrs, "next_charge_date")));
    }

    private static ParsedMemberEvent ExtractIdentityMembership(JsonElement root, string? campaignId)
    {
        var data = Child(root, "data");
        var userAttrs = Child(data, "attributes");
        string? patreonUserId = Text(data, "id");
        string? email = Text(userAttrs, "email");

        var included = Child(root, "included");
        var tiersById = new Dictionary<string, (string? Title, long Amount)>(StringComparer.Ordinal);
        JsonElement bestMember = default;
        long bestPledge = -1;

        if (included.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in included.EnumerateArray())
            {
                if (Text(item, "type") == "tier" && Text(item, "id") is { } tid)
                {
                    var ta = Child(item, "attributes");
                    tiersById[tid] = (Text(ta, "title"), Long(ta, "amount_cents") ?? 0);
                }
            }
            foreach (var item in included.EnumerateArray())
            {
                if (Text(item, "type") != "member") continue;
                var relCampaign = Text(Child(Child(Child(item, "relationships"), "campaign"), "data"), "id");
                if (!string.IsNullOrWhiteSpace(campaignId) && !string.IsNullOrWhiteSpace(relCampaign)
                    && !string.Equals(campaignId, relCampaign, StringComparison.Ordinal))
                    continue;
                var ma = Child(item, "attributes");
                long cents = Long(ma, "currently_entitled_amount_cents") ?? 0;
                if (cents > bestPledge)
                {
                    bestPledge = cents;
                    bestMember = item;
                }
            }
        }

        if (bestMember.ValueKind != JsonValueKind.Object)
            return new ParsedMemberEvent(patreonUserId, null, email, null, 0, "unlinked", null, null, null);

        var attrs = Child(bestMember, "attributes");
        var rels = Child(bestMember, "relationships");
        var tierIds = ExtractRelationshipIds(Child(rels, "currently_entitled_tiers"));
        string? tierTitle = tierIds.Select(id => tiersById.TryGetValue(id, out var t) ? t.Title : null)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

        return new ParsedMemberEvent(
            patreonUserId,
            Text(bestMember, "id"),
            Text(attrs, "email") ?? email,
            tierTitle,
            Long(attrs, "currently_entitled_amount_cents") ?? 0,
            Text(attrs, "patron_status"),
            null,
            ParseTime(Text(attrs, "pledge_relationship_start")),
            ParseTime(Text(attrs, "next_charge_date")));
    }

    private async Task<string?> ResolveWebhookTenantAsync(ParsedMemberEvent parsed, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(parsed.PatreonUserId))
        {
            var byUser = await FindLinkByPatreonUserAsync(parsed.PatreonUserId, ct).ConfigureAwait(false);
            if (byUser is not null) return byUser.TenantId;
        }
        if (!string.IsNullOrWhiteSpace(parsed.Email))
        {
            var byEmail = await FindLinkByEmailAsync(parsed.Email, ct).ConfigureAwait(false);
            if (byEmail is not null) return byEmail.TenantId;
        }
        return string.IsNullOrWhiteSpace(parsed.ExplicitTenant) ? null : parsed.ExplicitTenant.Trim();
    }

    private async Task<PatreonLinkRecord?> FindLinkByTenantAsync(string tenantId, CancellationToken ct)
    {
        if (_dataSource is null)
            return _linksByTenant.TryGetValue(tenantId, out var link) ? link : null;

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            SELECT tenant_id, patreon_user_id, patreon_member_id, email, tier, pledge_cents,
                   status, mapped_plan_id, granted_packages, linked_at, updated_at
            FROM app.patreon_links
            WHERE tenant_id = $1
            LIMIT 1
            """, conn);
        cmd.Parameters.AddWithValue(tenantId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadLink(reader) : null;
    }

    private async Task<PatreonLinkRecord?> FindLinkByPatreonUserAsync(string patreonUserId, CancellationToken ct)
    {
        if (_dataSource is null)
            return _linksByTenant.Values.FirstOrDefault(l =>
                string.Equals(l.PatreonUserId, patreonUserId, StringComparison.Ordinal));

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            SELECT tenant_id, patreon_user_id, patreon_member_id, email, tier, pledge_cents,
                   status, mapped_plan_id, granted_packages, linked_at, updated_at
            FROM app.patreon_links
            WHERE patreon_user_id = $1
            LIMIT 1
            """, conn);
        cmd.Parameters.AddWithValue(patreonUserId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadLink(reader) : null;
    }

    private async Task<PatreonLinkRecord?> FindLinkByEmailAsync(string email, CancellationToken ct)
    {
        if (_dataSource is null)
            return _linksByTenant.Values.FirstOrDefault(l =>
                !string.IsNullOrWhiteSpace(l.Email)
                && string.Equals(l.Email, email, StringComparison.OrdinalIgnoreCase));

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            SELECT tenant_id, patreon_user_id, patreon_member_id, email, tier, pledge_cents,
                   status, mapped_plan_id, granted_packages, linked_at, updated_at
            FROM app.patreon_links
            WHERE lower(email) = lower($1)
            LIMIT 1
            """, conn);
        cmd.Parameters.AddWithValue(email);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadLink(reader) : null;
    }

    private async Task UpsertLinkAsync(PatreonLinkRecord link, CancellationToken ct)
    {
        if (_dataSource is null)
        {
            _linksByTenant[link.TenantId] = link;
            return;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO app.patreon_links
                (tenant_id, patreon_user_id, patreon_member_id, email, tier, pledge_cents,
                 status, mapped_plan_id, granted_packages, linked_at, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, now())
            ON CONFLICT (tenant_id) DO UPDATE SET
                patreon_user_id = EXCLUDED.patreon_user_id,
                patreon_member_id = EXCLUDED.patreon_member_id,
                email = EXCLUDED.email,
                tier = EXCLUDED.tier,
                pledge_cents = EXCLUDED.pledge_cents,
                status = EXCLUDED.status,
                mapped_plan_id = EXCLUDED.mapped_plan_id,
                granted_packages = EXCLUDED.granted_packages,
                updated_at = now()
            """, conn);
        cmd.Parameters.AddWithValue(link.TenantId);
        cmd.Parameters.AddWithValue(link.PatreonUserId);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = (object?)link.PatreonMemberId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = (object?)link.Email ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = (object?)link.Tier ?? DBNull.Value });
        cmd.Parameters.AddWithValue(link.PledgeCents);
        cmd.Parameters.AddWithValue(link.Status);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = (object?)link.MappedPlanId ?? DBNull.Value });
        cmd.Parameters.AddWithValue(link.GrantedPackages.ToArray());
        cmd.Parameters.AddWithValue(link.LinkedAt);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static PatreonLinkRecord ReadLink(NpgsqlDataReader reader) => new(
        TenantId: reader.GetString(0),
        PatreonUserId: reader.GetString(1),
        PatreonMemberId: reader.IsDBNull(2) ? null : reader.GetString(2),
        Email: reader.IsDBNull(3) ? null : reader.GetString(3),
        Tier: reader.IsDBNull(4) ? null : reader.GetString(4),
        PledgeCents: reader.GetInt64(5),
        Status: reader.GetString(6),
        MappedPlanId: reader.IsDBNull(7) ? null : reader.GetString(7),
        GrantedPackages: reader.GetFieldValue<string[]>(8),
        LinkedAt: reader.GetFieldValue<DateTimeOffset>(9),
        UpdatedAt: reader.GetFieldValue<DateTimeOffset>(10));

    private static HashSet<string> ExtractRelationshipIds(JsonElement relationship)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var data = Child(relationship, "data");
        if (data.ValueKind != JsonValueKind.Array)
            return set;
        foreach (var item in data.EnumerateArray())
        {
            if (Text(item, "id") is { } id)
                set.Add(id);
        }
        return set;
    }

    private static PatreonCallbackResult CallbackError(int statusCode, string code, string message) =>
        new(false, code, message, statusCode, null, null, null, 0, null, null, Array.Empty<string>());

    private static PatreonWebhookResult WebhookFailure(bool verified, string eventId, string eventType, string status) =>
        new(false, verified, false, eventId, eventType, status, null, null, null, 0, Array.Empty<string>());

    private static JsonElement Child(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) ? child : default;

    private static string? Text(JsonElement value, string name)
    {
        var child = Child(value, name);
        return child.ValueKind == JsonValueKind.String ? child.GetString() : null;
    }

    private static long? Long(JsonElement value, string name)
    {
        var child = Child(value, name);
        return child.ValueKind == JsonValueKind.Number && child.TryGetInt64(out var n) ? n : null;
    }

    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, out var dto) ? dto : null;
}
