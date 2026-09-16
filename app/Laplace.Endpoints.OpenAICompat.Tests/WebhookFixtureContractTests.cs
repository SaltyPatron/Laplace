using System.Text.Json;
using Stripe;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

// These exercise the actual selected SDK before the endpoint's fail-closed
// exception mapping can label a malformed fixture as an invalid signature.
public sealed class WebhookFixtureContractTests
{
    [Fact]
    public void PaidCheckout_IsSignedAndDeserializesToTheActualSdkSession()
    {
        var payload = WebhookTestEvents.PaidCheckout("evt_sdk_checkout", "tenant-sdk",
            "audit.report", "quote-sdk", "cs_sdk_checkout");
        using var raw = JsonDocument.Parse(payload);
        Assert.Equal(JsonValueKind.Null, raw.RootElement.GetProperty("request").ValueKind);
        Assert.Equal(StripeConfiguration.ApiVersion, raw.RootElement.GetProperty("api_version").GetString());

        var parsed = EventUtility.ConstructEvent(payload, SignedWebhookFactory.Sign(payload),
            SignedWebhookFactory.WebhookSecret);
        Assert.Equal("evt_sdk_checkout", parsed.Id);
        Assert.Equal("checkout.session.completed", parsed.Type);
        var session = Assert.IsType<Stripe.Checkout.Session>(parsed.Data.Object);
        Assert.Equal("cs_sdk_checkout", session.Id);
        Assert.Equal("paid", session.PaymentStatus);
        Assert.Equal("quote-sdk", session.Metadata["quote_id"]);
    }

    [Fact]
    public void Subscription_IsSignedAndDeserializesToTheActualSdkSubscription()
    {
        var payload = WebhookTestEvents.Subscription("evt_sdk_subscription",
            "customer.subscription.deleted", "sub_sdk", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var parsed = EventUtility.ConstructEvent(payload, SignedWebhookFactory.Sign(payload),
            SignedWebhookFactory.WebhookSecret);
        Assert.Equal("evt_sdk_subscription", parsed.Id);
        Assert.Equal("customer.subscription.deleted", parsed.Type);
        Assert.Equal("sub_sdk", Assert.IsType<Subscription>(parsed.Data.Object).Id);
    }

    [Fact]
    public void ChangedPayload_CannotReuseTheOriginalSignature()
    {
        var payload = WebhookTestEvents.PaidCheckout("evt_sdk_tamper", "tenant-sdk",
            "audit.report", "quote-sdk", "cs_sdk_original");
        var signed = SignedWebhookFactory.Sign(payload);
        var changed = payload.Replace("cs_sdk_original", "cs_sdk_changed", StringComparison.Ordinal);
        Assert.NotEqual(payload, changed);
        Assert.Throws<StripeException>(() =>
            EventUtility.ConstructEvent(changed, signed, SignedWebhookFactory.WebhookSecret));
    }
}
