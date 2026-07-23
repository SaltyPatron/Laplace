using Laplace.Api.Contracts;
using Laplace.Endpoints.OpenAICompat.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Laplace.Endpoints.OpenAICompat;

internal static class QuoteGate
{
    public static async Task<QuoteExecutionGate> RequireQuoteAsync(
        HttpRequest request, IBillingOrchestrator billing, string serviceId, CancellationToken ct)
    {
        var quoteId = AppComposition.ResolveQuoteId(request) ?? "";
        var resolver = request.HttpContext.RequestServices.GetRequiredService<ITenantResolver>();
        var tenant = (await resolver.ResolveAsync(request.HttpContext, ct)).TenantId;
        return await billing.EnsureExecutableAsync(quoteId, tenant, serviceId, ct);
    }

    public static BillingReceipt MakeReceipt(BillingQuote quote) =>
        new(quote.QuoteId, quote.AmountCents, quote.Currency, quote.Tenant, quote.ServiceId);
}
