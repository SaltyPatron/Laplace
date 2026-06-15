using Laplace.Api.Contracts;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// The billing quote gate shared by the inference and report endpoint groups: resolve the request's
/// quote and check it is executable, and build the receipt for a consumed quote.
/// </summary>
internal static class QuoteGate
{
    public static async Task<QuoteExecutionGate> RequireQuoteAsync(
        HttpRequest request, IBillingOrchestrator billing, string serviceId, CancellationToken ct)
    {
        var quoteId = AppComposition.ResolveQuoteId(request) ?? "";
        return await billing.EnsureExecutableAsync(quoteId, serviceId, ct);
    }

    public static BillingReceipt MakeReceipt(BillingQuote quote) =>
        new(quote.QuoteId, quote.AmountCents, quote.Currency, quote.Tenant, quote.ServiceId);
}
