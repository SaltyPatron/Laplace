namespace Laplace.Endpoints.OpenAICompat;

internal static class EndpointMappings
{
    public static void MapCoreEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok", stream = "F-scaffold" }));

        app.MapGet("/v1/models", () => Results.Json(new
        {
            @object = "list",
            data = new object[]
            {
                new { id = "laplace-converse-001", @object = "model", created = 0, owned_by = "laplace" },
                new { id = "laplace-completions-001", @object = "model", created = 0, owned_by = "laplace" },
                new { id = "laplace-embeddings-pending", @object = "model", created = 0, owned_by = "laplace", status = "pending" }
            }
        }));

        app.MapGet("/v1/capabilities", () => Results.Json(new
        {
            stream = "F-scaffold",
            endpoints = new
            {
                chat_completions = new { status = "live", backend = "laplace.converse", billing = "preflight_quote_required" },
                completions = new { status = "live", backend = "laplace.completions", billing = "preflight_quote_required" },
                embeddings = new { status = "pending", reason = "requires Stream E physicality lookup path" },
                billing = new { status = "live", provider = "stripe_or_manual" },
                models = new { status = "live" }
            }
        }));
    }

    public static void MapOpenAiCompatEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/chat/completions", async (HttpRequest request, SubstrateClient substrate, IBillingOrchestrator billing, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<ChatCompletionsRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.Model))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'model' is required.");
            if (payload.Messages is null || payload.Messages.Count == 0)
                return EndpointJson.BadRequest("invalid_request_error", "Field 'messages' must contain at least one message.");

            var quoteId = AppComposition.ResolveQuoteId(request);
            if (string.IsNullOrWhiteSpace(quoteId))
                return EndpointJson.PaymentRequired("quote_required", "Billing quote is required before execution.", new { service_id = "chat.completions" });

            var gate = await billing.EnsureExecutableAsync(quoteId, "chat.completions", ct);
            if (!gate.Allowed || gate.Quote is null)
                return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null ? null : new { gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl });

            var prompt = payload.Messages
                .Where(m => !string.IsNullOrWhiteSpace(m.Content))
                .Select(m => m.Content!.Trim())
                .LastOrDefault();
            if (string.IsNullOrWhiteSpace(prompt))
                return EndpointJson.BadRequest("invalid_request_error", "At least one message must include non-empty 'content'.");

            var response = await substrate.ConverseAsync(prompt, ct);
            billing.MarkConsumedAndRecord(gate.Quote);

            return Results.Json(new
            {
                id = $"chatcmpl-{Guid.NewGuid():N}",
                @object = "chat.completion",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                model = payload.Model,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new { role = "assistant", content = response?.Reply ?? string.Empty },
                        finish_reason = "stop"
                    }
                },
                billing = new
                {
                    quote_id = gate.Quote.QuoteId,
                    amount_cents = gate.Quote.AmountCents,
                    currency = gate.Quote.Currency,
                    tenant = gate.Quote.Tenant
                },
                metadata = response is null
                    ? null
                    : new { eff_mu = response.EffectiveMu, witnesses = response.Witnesses }
            });
        });

        app.MapPost("/v1/completions", async (HttpRequest request, SubstrateClient substrate, IBillingOrchestrator billing, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<CompletionsRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.Model))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'model' is required.");
            if (string.IsNullOrWhiteSpace(payload.Prompt))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'prompt' is required.");

            var quoteId = AppComposition.ResolveQuoteId(request);
            if (string.IsNullOrWhiteSpace(quoteId))
                return EndpointJson.PaymentRequired("quote_required", "Billing quote is required before execution.", new { service_id = "completions" });

            var gate = await billing.EnsureExecutableAsync(quoteId, "completions", ct);
            if (!gate.Allowed || gate.Quote is null)
                return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null ? null : new { gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl });

            var rows = await substrate.CompletionsAsync(payload.Prompt.Trim(), 8, ct);
            billing.MarkConsumedAndRecord(gate.Quote);

            var choices = rows
                .Select((row, idx) => new
                {
                    text = row.ObjectLabel,
                    index = idx,
                    finish_reason = "stop",
                    metadata = new
                    {
                        object_id = row.ObjectIdHex,
                        kind_id = row.KindIdHex,
                        eff_mu = row.EffectiveMu,
                        witnesses = row.Witnesses
                    }
                })
                .ToArray();

            return Results.Json(new
            {
                id = $"cmpl-{Guid.NewGuid():N}",
                @object = "text_completion",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                model = payload.Model,
                choices,
                billing = new
                {
                    quote_id = gate.Quote.QuoteId,
                    amount_cents = gate.Quote.AmountCents,
                    currency = gate.Quote.Currency,
                    tenant = gate.Quote.Tenant
                }
            });
        });

        app.MapPost("/v1/embeddings", async (HttpRequest request, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<EmbeddingsRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.Model))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'model' is required.");
            if (string.IsNullOrWhiteSpace(payload.Input))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'input' is required.");
            return EndpointJson.NotImplemented("embeddings", "Pending Stream E physicality lookup path.");
        });
    }

    public static void MapBillingEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/billing/catalog", (IBillingOrchestrator billing) =>
            Results.Json(new
            {
                @object = "list",
                data = billing.ListCatalog().Select(s => new
                {
                    service_id = s.ServiceId,
                    display_name = s.DisplayName,
                    unit = s.UnitName,
                    unit_price_cents = s.UnitPriceCents,
                    currency = "usd"
                })
            }));

        app.MapPost("/v1/billing/preflight", async (HttpRequest request, IBillingOrchestrator billing, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<BillingPreflightRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.ServiceId))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'service_id' is required.");
            if (payload.Units < 1)
                return EndpointJson.BadRequest("invalid_request_error", "Field 'units' must be >= 1.");

            var tenant = string.IsNullOrWhiteSpace(payload.Tenant)
                ? AppComposition.ResolveTenant(request)
                : payload.Tenant.Trim();

            BillingQuote quote;
            try
            {
                quote = await billing.CreatePreflightQuoteAsync(tenant, payload.ServiceId.Trim(), payload.Units, ct);
            }
            catch (ArgumentException ex)
            {
                return EndpointJson.BadRequest("invalid_request_error", ex.Message);
            }

            return Results.Json(new
            {
                quote_id = quote.QuoteId,
                tenant = quote.Tenant,
                service_id = quote.ServiceId,
                units = quote.Units,
                amount_cents = quote.AmountCents,
                currency = quote.Currency,
                status = quote.Status,
                expires_at = quote.ExpiresAt,
                stripe_checkout_url = quote.StripeCheckoutUrl,
                next = new
                {
                    execute_header = new { name = "X-Laplace-Quote-Id", value = quote.QuoteId },
                    note = "Execution endpoints require an approved quote before execution."
                }
            });
        });

        app.MapGet("/v1/billing/quotes/{quoteId}", (string quoteId, IBillingOrchestrator billing) =>
        {
            if (!billing.TryGetQuote(quoteId, out var quote))
                return EndpointJson.BadRequest("quote_not_found", "Quote does not exist.");

            return Results.Json(new
            {
                quote_id = quote.QuoteId,
                tenant = quote.Tenant,
                service_id = quote.ServiceId,
                units = quote.Units,
                amount_cents = quote.AmountCents,
                currency = quote.Currency,
                status = quote.Status,
                consumed = quote.Consumed,
                stripe_checkout_url = quote.StripeCheckoutUrl,
                expires_at = quote.ExpiresAt
            });
        });

        app.MapGet("/v1/billing/usage", (HttpRequest request, IBillingOrchestrator billing) =>
        {
            var tenant = AppComposition.ResolveTenant(request);
            var usage = billing.GetUsage(tenant);
            return Results.Json(new
            {
                tenant,
                total_amount_cents = usage.Sum(x => x.AmountCents),
                entries = usage
            });
        });
    }
}
