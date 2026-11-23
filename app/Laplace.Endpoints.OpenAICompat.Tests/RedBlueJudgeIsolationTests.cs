using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Engine.Core;
using Laplace.Native;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class RedBlueJudgeIsolationTests : IClassFixture<KeyModeFactory>
{
    private readonly KeyModeFactory _factory;

    public RedBlueJudgeIsolationTests(KeyModeFactory factory) => _factory = factory;

    private static string HexFor(string label) =>
        Hash128.Blake3(Encoding.UTF8.GetBytes(label)).ToHex();

    private static string SignPatreon(string secret, string payload)
    {
        var mac = HMACMD5.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(mac);
    }

    // ---- #1709 Stripe commercial catalog default & manual override ----------

    [Fact]
    public async Task StripeBillingOptions_DefaultsCommercialCatalogApprovedToTrue_AndAllowsManualDisable()
    {
        var defaults = new StripeBillingOptions();
        Assert.True(defaults.CommercialCatalogApproved);

        var disabled = new StripeBillingOptions
        {
            ApiKey = "sk_live_test_disabled",
            CommercialCatalogApproved = false,
        };
        var sync = new StripeCatalogSync(
            new StaticBillingCatalog(Options.Create(disabled)),
            new InMemoryStripePriceMap(),
            Options.Create(disabled));

        var result = await sync.EnsureAllAsync(CancellationToken.None);
        Assert.True(result.StripeConfigured);
        Assert.NotEmpty(result.Entries);
        Assert.All(result.Entries, e => Assert.Equal("commercial_catalog_not_approved", e.Status));
    }

    // ---- #1708 Authority package isolation (Red Spear / Blue Shield / White Judge) ----

    [Fact]
    public async Task AuthorityScopePolicy_PrivatePackage_GrantsBlueTenant_RefusesRedTenant()
    {
        var store = new KnowledgePackageService();
        var secretWitnessHex = HexFor("blue-vault-secret-witness");
        await store.UpsertPackageAsync(
            packageId: "blue-vault",
            displayName: "Blue Tenant Private Vault",
            visibility: "private",
            witnessIds: [secretWitnessHex],
            minTrust: 0.72,
            ct: CancellationToken.None);

        var ctx = new DefaultHttpContext();
        var blue = new TenantContext("tenant-blue", "api_key", TenantContext.NoClaims);
        var red = new TenantContext("tenant-red", "api_key", TenantContext.NoClaims);

        // Before grant: even Blue is refused fail-closed.
        var beforeGrant = AuthorityScopePolicy.Resolve(
            ctx.Request, blue, (uint)LaplaceNative.LP_CAP_READ, requestWrite: false,
            explicitScope: "blue-vault", packages: store);
        Assert.False(beforeGrant.Allowed);
        Assert.Equal(StatusCodes.Status403Forbidden, beforeGrant.StatusCode);
        Assert.Equal("authority_package_denied", beforeGrant.ErrorCode);
        Assert.Equal((int)LaplaceNative.LP_DISPOSE_REFUSE, beforeGrant.Scope.Disposition);

        // Grant only to tenant-blue.
        await store.GrantPackageAsync("blue-vault", "tenant-blue", "operator", CancellationToken.None);

        var blueAllowed = AuthorityScopePolicy.Resolve(
            ctx.Request, blue, (uint)LaplaceNative.LP_CAP_READ, requestWrite: false,
            explicitScope: "blue-vault", packages: store);
        Assert.True(blueAllowed.Allowed);
        Assert.True(blueAllowed.Scope.Scoped);
        Assert.Equal(0.72, blueAllowed.Scope.MinTrust, 6);
        var witness = Assert.Single(blueAllowed.Scope.Witnesses!);
        Assert.Equal(secretWitnessHex, witness.ToHex());

        // Red Spear attempting to query blue-vault is refused 403 fail-closed.
        var redDenied = AuthorityScopePolicy.Resolve(
            ctx.Request, red, (uint)LaplaceNative.LP_CAP_READ, requestWrite: false,
            explicitScope: "blue-vault", packages: store);
        Assert.False(redDenied.Allowed);
        Assert.Equal(StatusCodes.Status403Forbidden, redDenied.StatusCode);
        Assert.Equal("authority_package_denied", redDenied.ErrorCode);
        Assert.Equal((int)LaplaceNative.LP_DISPOSE_REFUSE, redDenied.Scope.Disposition);
        Assert.Empty(redDenied.Scope.Witnesses!);

        // Multi-package scope mixing public core-open and private blue-vault: Blue succeeds, Red fails closed.
        var blueMulti = AuthorityScopePolicy.Resolve(
            ctx.Request, blue, (uint)LaplaceNative.LP_CAP_READ, requestWrite: false,
            explicitScope: "core-open,blue-vault", packages: store);
        Assert.True(blueMulti.Allowed);
        Assert.Contains(Hash128.ParseHex(secretWitnessHex), blueMulti.Scope.Witnesses!);
        Assert.Equal(0.72, blueMulti.Scope.MinTrust, 6);

        var redMulti = AuthorityScopePolicy.Resolve(
            ctx.Request, red, (uint)LaplaceNative.LP_CAP_READ, requestWrite: false,
            explicitScope: "core-open,blue-vault", packages: store);
        Assert.False(redMulti.Allowed);
        Assert.Equal(StatusCodes.Status403Forbidden, redMulti.StatusCode);
        Assert.Equal("authority_package_denied", redMulti.ErrorCode);
    }

    [Fact]
    public async Task AuthorityScopePolicy_PublicCoreOpen_UnknownAndEmptyScopes_FailClosed()
    {
        var store = new KnowledgePackageService();
        await store.UpsertPackageAsync(
            packageId: "empty-public",
            displayName: "Empty Public Package",
            visibility: "public",
            witnessIds: [],
            minTrust: 0.0,
            ct: CancellationToken.None);

        var ctx = new DefaultHttpContext();
        var tenant = new TenantContext("tenant-any", "api_key", TenantContext.NoClaims);

        // Public seeded core-open resolves deterministic witnesses.
        var openRes = AuthorityScopePolicy.Resolve(
            ctx.Request, tenant, (uint)LaplaceNative.LP_CAP_READ, requestWrite: false,
            explicitScope: "core-open", packages: store);
        Assert.True(openRes.Allowed);
        Assert.True(openRes.Scope.Scoped);
        Assert.NotEmpty(openRes.Scope.Witnesses!);

        // Unknown package fails closed 403.
        var unknown = AuthorityScopePolicy.Resolve(
            ctx.Request, tenant, (uint)LaplaceNative.LP_CAP_READ, requestWrite: false,
            explicitScope: "nonexistent-package", packages: store);
        Assert.False(unknown.Allowed);
        Assert.Equal(StatusCodes.Status403Forbidden, unknown.StatusCode);
        Assert.Equal("authority_package_denied", unknown.ErrorCode);

        // Package with zero witnesses and explicit empty/none scopes fail closed 403.
        foreach (var emptyScope in new[] { "empty-public", "empty", "none" })
        {
            var emptyRes = AuthorityScopePolicy.Resolve(
                ctx.Request, tenant, (uint)LaplaceNative.LP_CAP_READ, requestWrite: false,
                explicitScope: emptyScope, packages: store);
            Assert.False(emptyRes.Allowed);
            Assert.Equal(StatusCodes.Status403Forbidden, emptyRes.StatusCode);
            Assert.Equal("authority_empty_scope", emptyRes.ErrorCode);
        }
    }

    [Fact]
    public async Task AuthorityPackageEndpoints_EnforceOperatorCreateGrantAndTenantVisibility()
    {
        using var client = _factory.CreateClient();
        var blueKey = await IssueApiKeyAsync(client, "tenant-blue-http");
        var redKey = await IssueApiKeyAsync(client, "tenant-red-http");
        var secretWitness = HexFor("http-blue-secret");

        // Non-operator cannot create package.
        using (var forbiddenReq = new HttpRequestMessage(HttpMethod.Post, "/v1/authority/packages")
        {
            Content = JsonContent.Create(new
            {
                package_id = "http-vault",
                display_name = "HTTP Vault",
                visibility = "private",
                witness_ids = new[] { secretWitness },
                min_trust = 0.5,
            })
        })
        {
            forbiddenReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", blueKey);
            using var forbiddenResp = await client.SendAsync(forbiddenReq);
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenResp.StatusCode);
        }

        // Operator creates private package and grants only to tenant-blue-http.
        using (var createReq = new HttpRequestMessage(HttpMethod.Post, "/v1/authority/packages")
        {
            Content = JsonContent.Create(new
            {
                package_id = "http-vault",
                display_name = "HTTP Vault",
                visibility = "private",
                witness_ids = new[] { secretWitness },
                min_trust = 0.5,
            })
        })
        {
            createReq.Headers.Add(OperatorAuth.TokenHeader, KeyModeFactory.OperatorToken);
            createReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", blueKey);
            using var createResp = await client.SendAsync(createReq);
            Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        }

        using (var grantReq = new HttpRequestMessage(HttpMethod.Post, "/v1/authority/packages/http-vault/grant")
        {
            Content = JsonContent.Create(new { tenant_id = "tenant-blue-http" })
        })
        {
            grantReq.Headers.Add(OperatorAuth.TokenHeader, KeyModeFactory.OperatorToken);
            grantReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", blueKey);
            using var grantResp = await client.SendAsync(grantReq);
            Assert.Equal(HttpStatusCode.OK, grantResp.StatusCode);
        }

        // Blue sees http-vault + core-open; Red only sees public packages.
        using (var listBlue = new HttpRequestMessage(HttpMethod.Get, "/v1/authority/packages"))
        {
            listBlue.Headers.Authorization = new AuthenticationHeaderValue("Bearer", blueKey);
            using var blueResp = await client.SendAsync(listBlue);
            Assert.Equal(HttpStatusCode.OK, blueResp.StatusCode);
            using var doc = JsonDocument.Parse(await blueResp.Content.ReadAsStringAsync());
            var ids = doc.RootElement.GetProperty("packages").EnumerateArray()
                .Select(p => p.GetProperty("package_id").GetString()).ToArray();
            Assert.Contains("core-open", ids);
            Assert.Contains("http-vault", ids);
        }

        using (var listRed = new HttpRequestMessage(HttpMethod.Get, "/v1/authority/packages"))
        {
            listRed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", redKey);
            using var redResp = await client.SendAsync(listRed);
            Assert.Equal(HttpStatusCode.OK, redResp.StatusCode);
            using var doc = JsonDocument.Parse(await redResp.Content.ReadAsStringAsync());
            var ids = doc.RootElement.GetProperty("packages").EnumerateArray()
                .Select(p => p.GetProperty("package_id").GetString()).ToArray();
            Assert.Contains("core-open", ids);
            Assert.DoesNotContain("http-vault", ids);
        }
    }

    // ---- #1709 Witness payees & two-sided share_bp = 10000 royalties ------

    [Fact]
    public async Task ComputeRoyaltyShares_WithWitnessPayees_Conserves10000BpAndExactCents()
    {
        var payeeStore = new WitnessPayeeService();
        var wRoyaltyA = HexFor("witness-royalty-a");
        var wRoyaltyB = HexFor("witness-royalty-b");
        var wOpen = HexFor("witness-open-cc0");

        await payeeStore.UpsertAsync(wRoyaltyA, "seeder-a", "royalty_bearing", "Seeder A", CancellationToken.None);
        await payeeStore.UpsertAsync(wRoyaltyB, "seeder-b", "royalty_bearing", "Seeder B", CancellationToken.None);
        await payeeStore.UpsertAsync(wOpen, null, "open_license", "Open CC0", CancellationToken.None);

        var payees = await payeeStore.GetByWitnessIdsAsync([wRoyaltyA, wRoyaltyB, wOpen], CancellationToken.None);

        var quote = new BillingQuote(
            QuoteId: "q_payee_mix",
            Tenant: "buyer-1",
            ServiceId: "synthesis",
            Units: 10,
            AmountCents: 1000, // 70% seeder pool = 700 cents
            Currency: "usd",
            Status: "consumed",
            StripeSessionId: null,
            StripeCheckoutUrl: null,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            Consumed: true);

        // Mix of royalty_bearing and open_license with null SourceOwnerTenant on inputs.
        var mixed = new[]
        {
            new RoyaltyAttributionShare(wRoyaltyA, 0, 10, 30.0, SourceOwnerTenant: null),
            new RoyaltyAttributionShare(wRoyaltyB, 0, 10, 30.0, SourceOwnerTenant: null),
            new RoyaltyAttributionShare(wOpen, 0, 20, 40.0, SourceOwnerTenant: null),
        };
        var settled = BillingOrchestrator.ComputeRoyaltyShares(quote, mixed, DateTimeOffset.UtcNow, payees);
        Assert.Equal(3, settled.Count);
        Assert.Equal(10000, settled.Sum(r => r.ShareBp));
        long totalCents = settled.Sum(r => r.RoyaltyCents);
        Assert.True(totalCents <= 700);
        Assert.Equal(420, totalCents);

        var openRow = settled.Single(r => r.SourceIdHex == wOpen);
        Assert.Equal(4000, openRow.ShareBp);
        Assert.Equal(0, openRow.RoyaltyCents);
        Assert.Null(openRow.SourceOwnerTenant);

        var aRow = settled.Single(r => r.SourceIdHex == wRoyaltyA);
        Assert.Equal("seeder-a", aRow.SourceOwnerTenant);
        Assert.Equal(3000, aRow.ShareBp);
        Assert.Equal(210, aRow.RoyaltyCents);

        // Odd remainder distribution across 3 royalty-bearing payees: exact cent conservation.
        var wRoyaltyC = HexFor("witness-royalty-c");
        await payeeStore.UpsertAsync(wRoyaltyC, "seeder-c", "royalty_bearing", "Seeder C", CancellationToken.None);
        var threePayees = await payeeStore.GetByWitnessIdsAsync([wRoyaltyA, wRoyaltyB, wRoyaltyC], CancellationToken.None);
        var oddQuote = quote with { AmountCents = 100 }; // 70% pool = 70 cents
        var threeWay = new[]
        {
            new RoyaltyAttributionShare(wRoyaltyA, 0, 1, 1.0),
            new RoyaltyAttributionShare(wRoyaltyB, 0, 1, 1.0),
            new RoyaltyAttributionShare(wRoyaltyC, 0, 1, 1.0),
        };
        var oddSettled = BillingOrchestrator.ComputeRoyaltyShares(oddQuote, threeWay, DateTimeOffset.UtcNow, threePayees);
        Assert.Equal(10000, oddSettled.Sum(r => r.ShareBp));
        Assert.Equal(70, oddSettled.Sum(r => r.RoyaltyCents));
    }

    [Fact]
    public async Task WitnessPayeesEndpoint_RequiresAuthentication_AndEnforcesOwnerTenant()
    {
        // Header-mode host without credentials cannot register payees.
        await using var headerFactory = new GoldenFactory();
        using var headerClient = headerFactory.CreateClient();
        var witnessHex = HexFor("header-unauth-witness");
        using var unauthResp = await headerClient.PostAsJsonAsync("/v1/billing/payees", new
        {
            witness_id = witnessHex,
            license_kind = "royalty_bearing",
            display_name = "Unauth"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthResp.StatusCode);

        // Key-mode host with valid API key registers payee for own workspace, rejects cross-tenant hijack.
        using var keyClient = _factory.CreateClient();
        var alphaKey = await IssueApiKeyAsync(keyClient, "payee-alpha");

        using (var crossReq = new HttpRequestMessage(HttpMethod.Post, "/v1/billing/payees")
        {
            Content = JsonContent.Create(new
            {
                witness_id = witnessHex,
                owner_tenant = "payee-victim",
                license_kind = "royalty_bearing",
                display_name = "Hijack"
            })
        })
        {
            crossReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", alphaKey);
            using var crossResp = await keyClient.SendAsync(crossReq);
            Assert.Equal(HttpStatusCode.Forbidden, crossResp.StatusCode);
        }

        using (var okReq = new HttpRequestMessage(HttpMethod.Post, "/v1/billing/payees")
        {
            Content = JsonContent.Create(new
            {
                witness_id = witnessHex,
                license_kind = "royalty_bearing",
                display_name = "Alpha Corpus"
            })
        })
        {
            okReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", alphaKey);
            using var okResp = await keyClient.SendAsync(okReq);
            Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
        }

        using (var getReq = new HttpRequestMessage(HttpMethod.Get, "/v1/billing/payees"))
        {
            getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", alphaKey);
            using var getResp = await keyClient.SendAsync(getReq);
            Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
            using var doc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync());
            var payees = doc.RootElement.GetProperty("payees").EnumerateArray().ToArray();
            Assert.Contains(payees, p => p.GetProperty("witness_id").GetString() == witnessHex);
        }
    }

    // ---- #1709 Patreon HMAC-MD5 verification & entitlement witnessing -------

    [Fact]
    public async Task PatreonWebhook_ValidatesHmacMd5_ActivatesPlanAndPackages_AndHandlesDeleteIdempotently()
    {
        const string webhookSecret = "patreon_whsec_test_secret";
        var options = new PatreonOptions
        {
            ClientId = "patreon-client-id",
            ClientSecret = "patreon-client-secret",
            WebhookSecret = webhookSecret,
            CampaignId = "camp-99",
        };
        var catalog = new StaticBillingCatalog(Options.Create(new StripeBillingOptions()));
        var entitlements = new InMemoryBillingEntitlementStore();
        var events = new InMemoryBillingWebhookEventStore();
        var packages = new KnowledgePackageService();
        var patreon = new PatreonEntitlementService(catalog, entitlements, events, packages, options);

        var createPayload = JsonSerializer.Serialize(new
        {
            data = new
            {
                id = "mem-101",
                type = "member",
                attributes = new
                {
                    email = "patron@example.test",
                    patron_status = "active_patron",
                    currently_entitled_amount_cents = 9900,
                    tenant = "patreon-tenant-1",
                },
                relationships = new
                {
                    user = new { data = new { id = "pu-101", type = "user" } },
                    currently_entitled_tiers = new { data = new[] { new { id = "tier-studio", type = "tier" } } }
                }
            },
            included = new[]
            {
                new { id = "tier-studio", type = "tier", attributes = new { title = "Studio" } }
            }
        });

        var validSig = SignPatreon(webhookSecret, createPayload);
        Assert.True(patreon.VerifySignature(createPayload, validSig));
        Assert.False(patreon.VerifySignature(createPayload + " ", validSig));
        Assert.False(patreon.VerifySignature(createPayload, "00000000000000000000000000000000"));

        // Tampered request is rejected before touching entitlements.
        var tampered = await patreon.HandleWebhookAsync(
            createPayload.Replace("9900", "49900", StringComparison.Ordinal),
            validSig,
            "members:pledge:create",
            ct: CancellationToken.None);
        Assert.False(tampered.Accepted);
        Assert.False(tampered.Verified);
        Assert.Equal("invalid_signature", tampered.Status);
        Assert.Empty(await entitlements.GetByTenantAsync("patreon-tenant-1", CancellationToken.None));

        // Valid signed create activates studio plan + packages.
        var created = await patreon.HandleWebhookAsync(
            createPayload, validSig, "members:pledge:create", ct: CancellationToken.None);
        Assert.True(created.Accepted);
        Assert.True(created.Verified);
        Assert.False(created.Duplicate);
        Assert.Equal("studio", created.MappedPlanId);
        Assert.Contains("core-open", created.GrantedPackages);
        Assert.Contains("domain-code", created.GrantedPackages);

        var activeEntitlement = Assert.Single(await entitlements.GetByTenantAsync("patreon-tenant-1", CancellationToken.None));
        Assert.Equal("studio", activeEntitlement.PlanId);
        Assert.Equal("active", activeEntitlement.Status);

        // Duplicate delivery is accepted idempotently as duplicate.
        var dup = await patreon.HandleWebhookAsync(
            createPayload, validSig, "members:pledge:create", ct: CancellationToken.None);
        Assert.True(dup.Accepted);
        Assert.True(dup.Duplicate);
        Assert.Equal("duplicate", dup.Status);

        // Pledge delete deactivates the subscription cleanly.
        var deletePayload = JsonSerializer.Serialize(new
        {
            data = new
            {
                id = "mem-101",
                type = "member",
                attributes = new
                {
                    patron_status = "former_patron",
                    currently_entitled_amount_cents = 0,
                },
                relationships = new
                {
                    user = new { data = new { id = "pu-101", type = "user" } }
                }
            }
        });
        var deleteSig = SignPatreon(webhookSecret, deletePayload);
        var deleted = await patreon.HandleWebhookAsync(
            deletePayload, deleteSig, "members:pledge:delete", ct: CancellationToken.None);
        Assert.True(deleted.Accepted);
        Assert.Equal("canceled", deleted.Status);

        var afterDelete = Assert.Single(await entitlements.GetByTenantAsync("patreon-tenant-1", CancellationToken.None));
        Assert.Equal("canceled", afterDelete.Status);

        var statusView = await patreon.GetStatusAsync("patreon-tenant-1", CancellationToken.None);
        Assert.True(statusView.Linked);
        Assert.Equal("canceled", statusView.Status);
        Assert.Equal(0, statusView.PledgeCents);
    }

    [Fact]
    public async Task PatreonConnectAndCallback_Return503WhenUnconfigured_AndCompleteOAuthWhenConfigured()
    {
        await using var unconfiguredFactory = new GoldenFactory();
        using var unconfiguredClient = unconfiguredFactory.CreateClient();
        using (var connect503 = await unconfiguredClient.GetAsync("/v1/billing/patreon/connect"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, connect503.StatusCode);
            Assert.Contains("patreon_not_configured", await connect503.Content.ReadAsStringAsync());
        }

        // Stubbed OAuth token + identity exchange verifies signed state + activates Developer plan.
        var stubHandler = new StubPatreonHttpHandler();
        var options = new PatreonOptions
        {
            ClientId = "cid-1",
            ClientSecret = "csec-1",
            CampaignId = "camp-1",
        };
        var entitlements = new InMemoryBillingEntitlementStore();
        var patreon = new PatreonEntitlementService(
            new StaticBillingCatalog(Options.Create(new StripeBillingOptions())),
            entitlements,
            new InMemoryBillingWebhookEventStore(),
            new KnowledgePackageService(),
            options,
            httpHandler: stubHandler);

        var connect = patreon.CreateConnectUrl("oauth-tenant", "https://localhost");
        Assert.True(connect.Configured);
        Assert.Contains("https://www.patreon.com/oauth2/authorize", connect.AuthorizeUrl);
        Assert.NotNull(connect.State);

        var callback = await patreon.HandleCallbackAsync("auth-code-123", connect.State, "https://localhost", CancellationToken.None);
        Assert.True(callback.Success);
        Assert.Equal("oauth-tenant", callback.Tenant);
        Assert.Equal("developer", callback.MappedPlanId);
        Assert.Equal(2900, callback.PledgeCents);

        // Replay of the same OAuth state nonce is refused.
        var replay = await patreon.HandleCallbackAsync("auth-code-123", connect.State, "https://localhost", CancellationToken.None);
        Assert.False(replay.Success);
        Assert.Equal("invalid_oauth_state", replay.ErrorCode);

        // HTTP webhook endpoint verifies X-Patreon-Signature and updates tenant status/entitlements.
        const string httpWebhookSecret = "patreon_http_whsec";
        await using var patreonHttpFactory = new GoldenFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.PostConfigure<PatreonOptions>(o => o.WebhookSecret = httpWebhookSecret)));
        using var patreonClient = patreonHttpFactory.CreateClient();
        var body = JsonSerializer.Serialize(new
        {
            data = new
            {
                id = "mem-http-1",
                type = "member",
                attributes = new
                {
                    email = "http-patron@example.test",
                    patron_status = "active_patron",
                    currently_entitled_amount_cents = 49900,
                    tenant = "http-patreon-tenant"
                },
                relationships = new
                {
                    user = new { data = new { id = "pu-http-1", type = "user" } }
                }
            }
        });
        using (var badReq = new HttpRequestMessage(HttpMethod.Post, "/v1/billing/webhooks/patreon")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        })
        {
            badReq.Headers.Add("X-Patreon-Event", "members:pledge:create");
            badReq.Headers.Add("X-Patreon-Signature", "deadbeefdeadbeefdeadbeefdeadbeef");
            using var badResp = await patreonClient.SendAsync(badReq);
            Assert.Equal(HttpStatusCode.BadRequest, badResp.StatusCode);
        }

        using (var okReq = new HttpRequestMessage(HttpMethod.Post, "/v1/billing/webhooks/patreon")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        })
        {
            okReq.Headers.Add("X-Patreon-Event", "members:pledge:create");
            okReq.Headers.Add("X-Patreon-Signature", SignPatreon(httpWebhookSecret, body));
            using var okResp = await patreonClient.SendAsync(okReq);
            Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
            using var doc = JsonDocument.Parse(await okResp.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("accepted").GetBoolean());
            Assert.Equal("enterprise", doc.RootElement.GetProperty("mapped_plan_id").GetString());
        }

        using var statusReq = new HttpRequestMessage(HttpMethod.Get, "/v1/billing/patreon/status");
        statusReq.Headers.Add(HeaderTenantResolver.TenantHeader, "http-patreon-tenant");
        using var statusResp = await patreonClient.SendAsync(statusReq);
        Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);
        using var statusDoc = JsonDocument.Parse(await statusResp.Content.ReadAsStringAsync());
        Assert.True(statusDoc.RootElement.GetProperty("linked").GetBoolean());
        Assert.Equal("enterprise", statusDoc.RootElement.GetProperty("mapped_plan_id").GetString());
    }

    // ---- #1708 Feedback write governance & actor dependence root -----------

    [Fact]
    public async Task FeedbackEndpoint_RequiresAuthenticatedActorInKeyMode_AndUsesSingleActorLineageRoot()
    {
        using var client = _factory.CreateClient();
        using var unauth = await client.PostAsJsonAsync("/v1/feedback", new
        {
            verdict = "confirm",
            subject = "water",
            relation = "IS_A",
            @object = "liquid"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

        Assert.Equal("feedback-actor:tenant-alpha", FeedbackEndpoints.ActorLineageName("tenant-alpha"));
    }

    private static async Task<string> IssueApiKeyAsync(HttpClient client, string tenant)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/billing/operator/keys")
        {
            Content = JsonContent.Create(new { tenant, label = "test" })
        };
        req.Headers.Add(OperatorAuth.TokenHeader, KeyModeFactory.OperatorToken);
        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("api_key").GetString()!;
    }

    private sealed class StubPatreonHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.AbsoluteUri ?? "";
            string body = uri.Contains("/oauth2/token", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(new { access_token = "tok_test_1", token_type = "Bearer" })
                : JsonSerializer.Serialize(new
                {
                    data = new
                    {
                        id = "pu-oauth-77",
                        type = "user",
                        attributes = new { email = "oauth@example.test", full_name = "OAuth Patron" }
                    },
                    included = new object[]
                    {
                        new { id = "tier-dev", type = "tier", attributes = new { title = "Developer", amount_cents = 2900 } },
                        new
                        {
                            id = "mem-oauth-77",
                            type = "member",
                            attributes = new
                            {
                                patron_status = "active_patron",
                                currently_entitled_amount_cents = 2900,
                                email = "oauth@example.test"
                            },
                            relationships = new
                            {
                                campaign = new { data = new { id = "camp-1", type = "campaign" } },
                                currently_entitled_tiers = new { data = new[] { new { id = "tier-dev", type = "tier" } } }
                            }
                        }
                    }
                });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
