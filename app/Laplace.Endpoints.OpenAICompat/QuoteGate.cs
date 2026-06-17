using Laplace.Api.Contracts;

namespace Laplace.Endpoints.OpenAICompat;





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
