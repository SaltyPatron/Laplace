using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

// Billing surface response shapes. Declaration order = wire order (golden-pinned).

public sealed record BillingCatalogResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("data")] IReadOnlyList<CatalogServiceView> Data);

public sealed record CatalogServiceView(
    [property: JsonPropertyName("service_id")] string ServiceId,
    [property: JsonPropertyName("product_id")] string ProductId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("unit_price_cents")] long UnitPriceCents,
    [property: JsonPropertyName("base_fee_cents")] long BaseFeeCents,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("lookup_key")] string LookupKey,
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("metered")] bool Metered,
    [property: JsonPropertyName("recurring_interval")] string? RecurringInterval,
    [property: JsonPropertyName("stripe_price_id")] string? StripePriceId);

public sealed record BillingProductsResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("data")] IReadOnlyList<ProductView> Data);

public sealed record ProductView(
    [property: JsonPropertyName("product_id")] string ProductId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("prices")] IReadOnlyList<ProductPriceView> Prices);

public sealed record ProductPriceView(
    [property: JsonPropertyName("service_id")] string ServiceId,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("unit_price_cents")] long UnitPriceCents,
    [property: JsonPropertyName("base_fee_cents")] long BaseFeeCents,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("lookup_key")] string LookupKey,
    [property: JsonPropertyName("metered")] bool Metered,
    [property: JsonPropertyName("recurring_interval")] string? RecurringInterval);

public sealed record BillingPlansResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("data")] IReadOnlyList<PlanView> Data);

public sealed record PlanView(
    [property: JsonPropertyName("plan_id")] string PlanId,
    [property: JsonPropertyName("service_id")] string ServiceId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("monthly_price_cents")] long MonthlyPriceCents,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("monthly_credits")] IReadOnlyDictionary<string, int> MonthlyCredits,
    [property: JsonPropertyName("included_product_ids")] IReadOnlyList<string> IncludedProductIds,
    [property: JsonPropertyName("support_tier")] string SupportTier,
    [property: JsonPropertyName("active")] bool Active);

public sealed record PlanSubscribeResponse(
    [property: JsonPropertyName("quote_id")] string QuoteId,
    [property: JsonPropertyName("tenant")] string Tenant,
    [property: JsonPropertyName("plan_id")] string PlanId,
    [property: JsonPropertyName("service_id")] string ServiceId,
    [property: JsonPropertyName("monthly_price_cents")] long MonthlyPriceCents,
    [property: JsonPropertyName("amount_cents")] long AmountCents,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("stripe_checkout_url")] string? StripeCheckoutUrl,
    [property: JsonPropertyName("monthly_credits")] IReadOnlyDictionary<string, int> MonthlyCredits,
    [property: JsonPropertyName("next")] PlanNextStep Next);

public sealed record PlanNextStep(
    [property: JsonPropertyName("checkout_url")] string? CheckoutUrl,
    [property: JsonPropertyName("note")] string Note);

public sealed record EntitlementsResponse(
    [property: JsonPropertyName("tenant")] string Tenant,
    [property: JsonPropertyName("data")] IReadOnlyList<EntitlementView> Data);

public sealed record EntitlementView(
    [property: JsonPropertyName("tenant")] string Tenant,
    [property: JsonPropertyName("plan_id")] string PlanId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("period_start")] DateTimeOffset PeriodStart,
    [property: JsonPropertyName("period_end")] DateTimeOffset PeriodEnd,
    [property: JsonPropertyName("monthly_credits")] IReadOnlyDictionary<string, int> MonthlyCredits,
    [property: JsonPropertyName("used_credits")] IReadOnlyDictionary<string, int> UsedCredits,
    [property: JsonPropertyName("stripe_customer_id")] string? StripeCustomerId,
    [property: JsonPropertyName("stripe_subscription_id")] string? StripeSubscriptionId,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record CreditConsumeResponse(
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("tenant")] string Tenant,
    [property: JsonPropertyName("plan_id")] string PlanId,
    [property: JsonPropertyName("service_id")] string ServiceId,
    [property: JsonPropertyName("units")] int Units,
    [property: JsonPropertyName("remaining")] int Remaining,
    [property: JsonPropertyName("period_end")] DateTimeOffset PeriodEnd,
    [property: JsonPropertyName("status")] string Status);

public sealed record WebhookResponse(
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("verified")] bool Verified,
    [property: JsonPropertyName("duplicate")] bool Duplicate,
    [property: JsonPropertyName("event_id")] string? EventId,
    [property: JsonPropertyName("event_type")] string? EventType,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("tenant")] string? Tenant,
    [property: JsonPropertyName("service_id")] string? ServiceId,
    [property: JsonPropertyName("quote_id")] string? QuoteId,
    [property: JsonPropertyName("plan_id")] string? PlanId);

public sealed record CatalogSyncResponse(
    [property: JsonPropertyName("stripe_configured")] bool StripeConfigured,
    [property: JsonPropertyName("entries")] IReadOnlyList<CatalogSyncEntryView> Entries);

public sealed record CatalogSyncEntryView(
    [property: JsonPropertyName("service_id")] string ServiceId,
    [property: JsonPropertyName("lookup_key")] string LookupKey,
    [property: JsonPropertyName("stripe_price_id")] string? StripePriceId,
    [property: JsonPropertyName("stripe_product_id")] string? StripeProductId,
    [property: JsonPropertyName("status")] string Status);

public sealed record PreflightQuoteResponse(
    [property: JsonPropertyName("quote_id")] string QuoteId,
    [property: JsonPropertyName("tenant")] string Tenant,
    [property: JsonPropertyName("service_id")] string ServiceId,
    [property: JsonPropertyName("units")] int Units,
    [property: JsonPropertyName("amount_cents")] long AmountCents,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("stripe_checkout_url")] string? StripeCheckoutUrl,
    [property: JsonPropertyName("next")] QuoteNextStep Next);

public sealed record QuoteNextStep(
    [property: JsonPropertyName("execute_header")] ExecuteHeader ExecuteHeader,
    [property: JsonPropertyName("note")] string Note);

public sealed record ExecuteHeader(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value);

public sealed record SynthesisQuoteResponse(
    [property: JsonPropertyName("quote_id")] string QuoteId,
    [property: JsonPropertyName("tenant")] string Tenant,
    [property: JsonPropertyName("service_id")] string ServiceId,
    [property: JsonPropertyName("estimated_parameters")] long EstimatedParameters,
    [property: JsonPropertyName("billable_units")] int BillableUnits,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("amount_cents")] long AmountCents,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("stripe_checkout_url")] string? StripeCheckoutUrl,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("next")] QuoteNextStep Next);

public sealed record ExplainQuoteResponse(
    [property: JsonPropertyName("quote_id")] string QuoteId,
    [property: JsonPropertyName("tenant")] string Tenant,
    [property: JsonPropertyName("service_id")] string ServiceId,
    [property: JsonPropertyName("depth")] int Depth,
    [property: JsonPropertyName("beam")] int Beam,
    [property: JsonPropertyName("academic")] bool Academic,
    [property: JsonPropertyName("estimated_trace_nodes")] long EstimatedTraceNodes,
    [property: JsonPropertyName("billable_units")] int BillableUnits,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("amount_cents")] long AmountCents,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("stripe_checkout_url")] string? StripeCheckoutUrl,
    [property: JsonPropertyName("next")] QuoteNextStep Next);

public sealed record AuditQuoteResponse(
    [property: JsonPropertyName("quote_id")] string QuoteId,
    [property: JsonPropertyName("tenant")] string Tenant,
    [property: JsonPropertyName("service_id")] string ServiceId,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("academic")] bool Academic,
    [property: JsonPropertyName("metered_items")] long MeteredItems,
    [property: JsonPropertyName("billable_units")] int BillableUnits,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("items_per_unit")] long ItemsPerUnit,
    [property: JsonPropertyName("amount_cents")] long AmountCents,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("stripe_checkout_url")] string? StripeCheckoutUrl,
    [property: JsonPropertyName("next")] QuoteNextStep Next);

public sealed record VisualizationQuoteResponse(
    [property: JsonPropertyName("quote_id")] string QuoteId,
    [property: JsonPropertyName("tenant")] string Tenant,
    [property: JsonPropertyName("service_id")] string ServiceId,
    [property: JsonPropertyName("nodes")] int Nodes,
    [property: JsonPropertyName("edges")] int Edges,
    [property: JsonPropertyName("include_geometry")] bool IncludeGeometry,
    [property: JsonPropertyName("include_evidence")] bool IncludeEvidence,
    [property: JsonPropertyName("interactive")] bool Interactive,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("metered_items")] long MeteredItems,
    [property: JsonPropertyName("billable_units")] int BillableUnits,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("items_per_unit")] long ItemsPerUnit,
    [property: JsonPropertyName("amount_cents")] long AmountCents,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("stripe_checkout_url")] string? StripeCheckoutUrl,
    [property: JsonPropertyName("next")] QuoteNextStep Next);

public sealed record RecipeQuoteResponse(
    [property: JsonPropertyName("quote_id")] string QuoteId,
    [property: JsonPropertyName("tenant")] string Tenant,
    [property: JsonPropertyName("service_id")] string ServiceId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("content_items")] int ContentItems,
    [property: JsonPropertyName("commercial")] bool Commercial,
    [property: JsonPropertyName("private_export")] bool PrivateExport,
    [property: JsonPropertyName("metered_items")] long MeteredItems,
    [property: JsonPropertyName("billable_units")] int BillableUnits,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("items_per_unit")] long ItemsPerUnit,
    [property: JsonPropertyName("amount_cents")] long AmountCents,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("stripe_checkout_url")] string? StripeCheckoutUrl,
    [property: JsonPropertyName("next")] QuoteNextStep Next);

public sealed record QuoteStatusResponse(
    [property: JsonPropertyName("quote_id")] string QuoteId,
    [property: JsonPropertyName("tenant")] string Tenant,
    [property: JsonPropertyName("service_id")] string ServiceId,
    [property: JsonPropertyName("units")] int Units,
    [property: JsonPropertyName("amount_cents")] long AmountCents,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("consumed")] bool Consumed,
    [property: JsonPropertyName("stripe_checkout_url")] string? StripeCheckoutUrl,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

public sealed record UsageResponse(
    [property: JsonPropertyName("tenant")] string Tenant,
    [property: JsonPropertyName("total_amount_cents")] long TotalAmountCents,
    [property: JsonPropertyName("entries")] IReadOnlyList<UsageEntry> Entries);

/// <summary>Ledger entry view (camelCase wire — pinned by the usage golden).</summary>
public sealed record UsageEntry(
    string QuoteId,
    string Tenant,
    string ServiceId,
    int Units,
    long AmountCents,
    DateTimeOffset ExecutedAt);
