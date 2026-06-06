using System.Text.Json;

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
            if (!EmbeddingsInputPresent(payload.Input))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'input' must be a non-empty string or array of strings.");
            return EndpointJson.NotImplemented("embeddings", "Pending Stream E physicality lookup path.");
        });
    }

    // input may be a string or a (non-empty) array of strings per the OpenAI shape.
    private static bool EmbeddingsInputPresent(JsonElement? input)
    {
        if (input is not { } element)
            return false;
        return element.ValueKind switch
        {
            JsonValueKind.String => !string.IsNullOrWhiteSpace(element.GetString()),
            JsonValueKind.Array => element.GetArrayLength() > 0,
            _ => false
        };
    }

    public static void MapBillingEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/billing/catalog", (IBillingCatalog catalog, IStripePriceMap priceMap) =>
            Results.Json(new
            {
                @object = "list",
                data = catalog.List().Select(s => new
                {
                    service_id = s.ServiceId,
                    product_id = s.ProductId,
                    display_name = s.DisplayName,
                    unit = s.UnitName,
                    unit_price_cents = s.UnitPriceCents,
                    base_fee_cents = s.BaseFeeCents,
                    currency = s.Currency,
                    lookup_key = s.LookupKey,
                    active = s.Active,
                    metered = s.Metered,
                    recurring_interval = s.RecurringInterval,
                    stripe_price_id = priceMap.TryGet(s.LookupKey, out var pid) ? pid : null
                })
            }));

        app.MapGet("/v1/billing/products", (IBillingCatalog catalog) =>
            Results.Json(new
            {
                @object = "list",
                data = catalog.ListProducts().Select(p => new
                {
                    product_id = p.ProductId,
                    name = p.Name,
                    description = p.Description,
                    category = p.Category,
                    prices = catalog.List()
                        .Where(s => string.Equals(s.ProductId, p.ProductId, StringComparison.OrdinalIgnoreCase))
                        .Select(s => new
                        {
                            service_id = s.ServiceId,
                            unit = s.UnitName,
                            unit_price_cents = s.UnitPriceCents,
                            base_fee_cents = s.BaseFeeCents,
                            currency = s.Currency,
                            lookup_key = s.LookupKey,
                            metered = s.Metered,
                            recurring_interval = s.RecurringInterval
                        })
                })
            }));

        app.MapGet("/v1/billing/plans", (IBillingCatalog catalog) =>
            Results.Json(new
            {
                @object = "list",
                data = catalog.ListPlans().Select(p => new
                {
                    plan_id = p.PlanId,
                    service_id = p.ServiceId,
                    name = p.Name,
                    description = p.Description,
                    monthly_price_cents = p.MonthlyPriceCents,
                    currency = p.Currency,
                    monthly_credits = p.MonthlyCredits,
                    included_product_ids = p.IncludedProductIds,
                    support_tier = p.SupportTier,
                    active = p.Active
                })
            }));

        app.MapPost("/v1/billing/catalog/sync", async (IStripeCatalogSync sync, CancellationToken ct) =>
        {
            var result = await sync.EnsureAllAsync(ct);
            return Results.Json(new
            {
                stripe_configured = result.StripeConfigured,
                entries = result.Entries.Select(e => new
                {
                    service_id = e.ServiceId,
                    lookup_key = e.LookupKey,
                    stripe_price_id = e.StripePriceId,
                    stripe_product_id = e.StripeProductId,
                    status = e.Status
                })
            });
        });

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

        app.MapPost("/v1/billing/synthesis/quote", async (HttpRequest request, IBillingOrchestrator billing, ISynthesisQuoteCalculator calc, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<SynthesisQuoteRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (payload.VocabSize < 1 || payload.HiddenSize < 1 || payload.NumLayers < 1 || payload.NumHeads < 1)
                return EndpointJson.BadRequest(
                    "invalid_request_error",
                    "Fields 'vocab_size', 'hidden_size', 'num_layers', and 'num_heads' must each be >= 1.");

            var dims = new SynthesisRecipeDimensions(
                VocabSize: payload.VocabSize,
                HiddenSize: payload.HiddenSize,
                NumLayers: payload.NumLayers,
                NumHeads: payload.NumHeads,
                NumKvHeads: payload.NumKvHeads is > 0 ? payload.NumKvHeads.Value : payload.NumHeads,
                IntermediateSize: payload.IntermediateSize > 0 ? payload.IntermediateSize : payload.HiddenSize * 4,
                TiedEmbeddings: payload.TiedEmbeddings);

            var estimate = calc.Estimate(dims);
            var tenant = string.IsNullOrWhiteSpace(payload.Tenant)
                ? AppComposition.ResolveTenant(request)
                : payload.Tenant.Trim();

            BillingQuote quote;
            try
            {
                var units = (int)Math.Min(int.MaxValue, estimate.BillableUnits);
                quote = await billing.CreatePreflightQuoteAsync(tenant, "synthesis", units, ct);
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
                estimated_parameters = estimate.Parameters,
                billable_units = quote.Units,
                unit = "param_million",
                amount_cents = quote.AmountCents,
                currency = quote.Currency,
                status = quote.Status,
                expires_at = quote.ExpiresAt,
                stripe_checkout_url = quote.StripeCheckoutUrl,
                format = string.IsNullOrWhiteSpace(payload.Format) ? "gguf" : payload.Format.Trim(),
                next = new
                {
                    execute_header = new { name = "X-Laplace-Quote-Id", value = quote.QuoteId },
                    note = "Synthesis is dimensionality-metered: amount = base job fee + per-million-parameter rate."
                }
            });
        });

        app.MapPost("/v1/billing/explain/quote", async (HttpRequest request, IBillingOrchestrator billing, ITraceQuoteCalculator calc, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<ExplainQuoteRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (payload.Depth < 1 || payload.Beam < 1)
                return EndpointJson.BadRequest(
                    "invalid_request_error",
                    "Fields 'depth' and 'beam' must each be >= 1.");

            var estimate = calc.Estimate(new TraceReportRequest(payload.Depth, payload.Beam, payload.Academic));
            var tenant = string.IsNullOrWhiteSpace(payload.Tenant)
                ? AppComposition.ResolveTenant(request)
                : payload.Tenant.Trim();

            BillingQuote quote;
            try
            {
                var units = (int)Math.Min(int.MaxValue, estimate.BillableUnits);
                quote = await billing.CreatePreflightQuoteAsync(tenant, "explain.trace", units, ct);
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
                depth = payload.Depth,
                beam = payload.Beam,
                academic = payload.Academic,
                estimated_trace_nodes = estimate.TraceNodes,
                billable_units = quote.Units,
                unit = "trace_unit",
                amount_cents = quote.AmountCents,
                currency = quote.Currency,
                status = quote.Status,
                expires_at = quote.ExpiresAt,
                stripe_checkout_url = quote.StripeCheckoutUrl,
                next = new
                {
                    execute_header = new { name = "X-Laplace-Quote-Id", value = quote.QuoteId },
                    note = "Step-by-step explainability is metered by trace size (depth x beam); the academic tier expands each node with evidence provenance / citations."
                }
            });
        });

        app.MapPost("/v1/billing/audit/quote", async (HttpRequest request, IBillingOrchestrator billing, IReportQuoteCalculator calc, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<AuditQuoteRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");

            var estimate = calc.EstimateAudit(new AuditReportSpec(
                Scope: string.IsNullOrWhiteSpace(payload.Scope) ? "summary" : payload.Scope.Trim(),
                IncludeEvidence: payload.IncludeEvidence,
                IncludeConsensus: payload.IncludeConsensus,
                IncludeConvergence: payload.IncludeConvergence,
                Academic: payload.Academic));
            var tenant = string.IsNullOrWhiteSpace(payload.Tenant)
                ? AppComposition.ResolveTenant(request)
                : payload.Tenant.Trim();

            BillingQuote quote;
            try
            {
                var units = (int)Math.Min(int.MaxValue, estimate.BillableUnits);
                quote = await billing.CreatePreflightQuoteAsync(tenant, estimate.ServiceId, units, ct);
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
                scope = string.IsNullOrWhiteSpace(payload.Scope) ? "summary" : payload.Scope.Trim(),
                academic = payload.Academic,
                metered_items = estimate.MeteredItems,
                billable_units = quote.Units,
                unit = estimate.UnitName,
                items_per_unit = estimate.ItemsPerUnit,
                amount_cents = quote.AmountCents,
                currency = quote.Currency,
                status = quote.Status,
                expires_at = quote.ExpiresAt,
                stripe_checkout_url = quote.StripeCheckoutUrl,
                next = new
                {
                    execute_header = new { name = "X-Laplace-Quote-Id", value = quote.QuoteId },
                    note = "Audit reports are metered by selected sections, scope breadth, and academic provenance expansion."
                }
            });
        });

        app.MapPost("/v1/billing/visualization/quote", async (HttpRequest request, IBillingOrchestrator billing, IReportQuoteCalculator calc, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<VisualizationQuoteRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (payload.Nodes < 1 || payload.Edges < 0)
                return EndpointJson.BadRequest("invalid_request_error", "Field 'nodes' must be >= 1 and 'edges' must be >= 0.");

            var estimate = calc.EstimateVisualization(new VisualizationExportSpec(
                Nodes: payload.Nodes,
                Edges: payload.Edges,
                IncludeGeometry: payload.IncludeGeometry,
                IncludeEvidence: payload.IncludeEvidence,
                Interactive: payload.Interactive));
            var tenant = string.IsNullOrWhiteSpace(payload.Tenant)
                ? AppComposition.ResolveTenant(request)
                : payload.Tenant.Trim();

            BillingQuote quote;
            try
            {
                var units = (int)Math.Min(int.MaxValue, estimate.BillableUnits);
                quote = await billing.CreatePreflightQuoteAsync(tenant, estimate.ServiceId, units, ct);
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
                nodes = payload.Nodes,
                edges = payload.Edges,
                include_geometry = payload.IncludeGeometry,
                include_evidence = payload.IncludeEvidence,
                interactive = payload.Interactive,
                format = string.IsNullOrWhiteSpace(payload.Format) ? "json" : payload.Format.Trim(),
                metered_items = estimate.MeteredItems,
                billable_units = quote.Units,
                unit = estimate.UnitName,
                items_per_unit = estimate.ItemsPerUnit,
                amount_cents = quote.AmountCents,
                currency = quote.Currency,
                status = quote.Status,
                expires_at = quote.ExpiresAt,
                stripe_checkout_url = quote.StripeCheckoutUrl,
                next = new
                {
                    execute_header = new { name = "X-Laplace-Quote-Id", value = quote.QuoteId },
                    note = "Visualization exports are metered by graph size, geometry inclusion, evidence overlays, and interactive output."
                }
            });
        });

        app.MapPost("/v1/billing/recipe/quote", async (HttpRequest request, IBillingOrchestrator billing, IReportQuoteCalculator calc, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<RecipeQuoteRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.Action))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'action' is required.");
            if (payload.ContentItems < 0)
                return EndpointJson.BadRequest("invalid_request_error", "Field 'content_items' must be >= 0.");

            var estimate = calc.EstimateRecipe(new RecipeWorkSpec(
                Action: payload.Action,
                ContentItems: payload.ContentItems,
                Commercial: payload.Commercial,
                PrivateExport: payload.PrivateExport));
            var tenant = string.IsNullOrWhiteSpace(payload.Tenant)
                ? AppComposition.ResolveTenant(request)
                : payload.Tenant.Trim();

            BillingQuote quote;
            try
            {
                var units = (int)Math.Min(int.MaxValue, estimate.BillableUnits);
                quote = await billing.CreatePreflightQuoteAsync(tenant, estimate.ServiceId, units, ct);
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
                action = payload.Action.Trim(),
                content_items = payload.ContentItems,
                commercial = payload.Commercial,
                private_export = payload.PrivateExport,
                metered_items = estimate.MeteredItems,
                billable_units = quote.Units,
                unit = estimate.UnitName,
                items_per_unit = estimate.ItemsPerUnit,
                amount_cents = quote.AmountCents,
                currency = quote.Currency,
                status = quote.Status,
                expires_at = quote.ExpiresAt,
                stripe_checkout_url = quote.StripeCheckoutUrl,
                next = new
                {
                    execute_header = new { name = "X-Laplace-Quote-Id", value = quote.QuoteId },
                    note = "Recipe quotes cover publishing, access, compilation, commercial use, and private content export."
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
