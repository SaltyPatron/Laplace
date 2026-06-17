




export interface paths {
    "/health": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["HealthResponse"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/models": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ModelList"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/capabilities": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["CapabilitiesResponse"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/chat/completions": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["ChatCompletionsRequest"];
                };
            };
            responses: {
                
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ChatCompletionResponse"];
                    };
                };
                
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                
                402: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["PaymentRequiredResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/completions": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["CompletionsRequest"];
                };
            };
            responses: {
                
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["CompletionResponse"];
                    };
                };
                
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                
                402: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["PaymentRequiredResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/embeddings": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["EmbeddingsRequest"];
                };
            };
            responses: {
                
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                
                501: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["NotImplementedResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/evidence/{target}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    limit?: number | string;
                };
                header?: never;
                path: {
                    target: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["EvidenceResponse"];
                    };
                };
                
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                
                503: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/audit/report": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["AuditReportRequest"];
                };
            };
            responses: {
                
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["AuditReportResponse"];
                    };
                };
                
                402: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["PaymentRequiredResponse"];
                    };
                };
                
                503: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/visualizations/substrate": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["VisualizationExecuteRequest"];
                };
            };
            responses: {
                
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["VisualizationGraphResponse"];
                    };
                };
                
                402: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["PaymentRequiredResponse"];
                    };
                };
                
                503: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/explain/report": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["ExplainReportRequest"];
                };
            };
            responses: {
                
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ExplainReportResponse"];
                    };
                };
                
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                
                402: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["PaymentRequiredResponse"];
                    };
                };
                
                503: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/billing/catalog": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BillingCatalogResponse"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/billing/products": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BillingProductsResponse"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/billing/plans": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BillingPlansResponse"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/billing/plans/{planId}/subscribe": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    planId: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["PlanSubscribeRequest"];
                };
            };
            responses: {
                
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["PlanSubscribeResponse"];
                    };
                };
                
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/billing/entitlements": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["EntitlementsResponse"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/billing/entitlements/consume": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["CreditConsumeRequest"];
                };
            };
            responses: {
                
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["CreditConsumeResponse"];
                    };
                };
                
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                
                402: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["CreditConsumeResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/billing/webhooks/stripe": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["WebhookResponse"];
                    };
                };
                
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["WebhookResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/billing/catalog/sync": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["CatalogSyncResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/billing/preflight": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["BillingPreflightRequest"];
                };
            };
            responses: {
                
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["PreflightQuoteResponse"];
                    };
                };
                
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/billing/synthesis/quote": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["SynthesisQuoteRequest"];
                };
            };
            responses: {
                
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SynthesisQuoteResponse"];
                    };
                };
                
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/billing/explain/quote": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["ExplainQuoteRequest"];
                };
            };
            responses: {
                
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ExplainQuoteResponse"];
                    };
                };
                
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/billing/audit/quote": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["AuditQuoteRequest"];
                };
            };
            responses: {
                
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["AuditQuoteResponse"];
                    };
                };
                
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/billing/visualization/quote": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["VisualizationQuoteRequest"];
                };
            };
            responses: {
                
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["VisualizationQuoteResponse"];
                    };
                };
                
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/billing/recipe/quote": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["RecipeQuoteRequest"];
                };
            };
            responses: {
                
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["RecipeQuoteResponse"];
                    };
                };
                
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/billing/quotes/{quoteId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    quoteId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["QuoteStatusResponse"];
                    };
                };
                
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/billing/usage": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["UsageResponse"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
}
export type webhooks = Record<string, never>;
export interface components {
    schemas: {
        AuditQuoteRequest: {
            tenant: null | string;
            scope?: null | string;
            
            include_evidence: boolean;
            
            include_consensus: boolean;
            
            include_convergence: boolean;
            
            academic: boolean;
        };
        AuditQuoteResponse: {
            quote_id: string;
            tenant: string;
            service_id: string;
            scope: string;
            academic: boolean;
            
            metered_items: number | string;
            
            billable_units: number | string;
            unit: string;
            
            items_per_unit: number | string;
            
            amount_cents: number | string;
            currency: string;
            status: string;
            
            expires_at: string;
            stripe_checkout_url: null | string;
            next: components["schemas"]["QuoteNextStep"];
        };
        AuditReportRequest: {
            scope?: null | string;
            
            include_evidence: boolean;
            
            include_consensus: boolean;
            
            include_convergence: boolean;
            
            academic: boolean;
        };
        AuditReportResponse: {
            id: string;
            object: string;
            
            created: number | string;
            scope: string;
            academic: boolean;
            include_evidence: boolean;
            include_consensus: boolean;
            include_convergence: boolean;
            report: components["schemas"]["SubstrateAuditReport"];
            billing: null | components["schemas"]["BillingReceipt"];
        };
        BillingCatalogResponse: {
            object: string;
            data: components["schemas"]["CatalogServiceView"][];
        };
        BillingPlansResponse: {
            object: string;
            data: components["schemas"]["PlanView"][];
        };
        BillingPreflightRequest: {
            service_id: null | string;
            
            units: number | string;
            tenant: null | string;
        };
        BillingProductsResponse: {
            object: string;
            data: components["schemas"]["ProductView"][];
        };
        BillingReceipt: {
            quote_id: string;
            
            amount_cents: number | string;
            currency: string;
            tenant: string;
            service_id: string;
        };
        CapabilitiesResponse: {
            stream: string;
            endpoints: components["schemas"]["CapabilityEndpoints"];
        };
        CapabilityEndpoints: {
            chat_completions: components["schemas"]["CapabilityStatus"];
            completions: components["schemas"]["CapabilityStatus"];
            embeddings: components["schemas"]["CapabilityStatus"];
            audit_reports: components["schemas"]["CapabilityStatus"];
            visualizations: components["schemas"]["CapabilityStatus"];
            explainability_reports: components["schemas"]["CapabilityStatus"];
            billing: components["schemas"]["CapabilityStatus"];
            models: components["schemas"]["CapabilityStatus"];
        };
        CapabilityStatus: {
            status: string;
            backend?: null | string;
            billing?: null | string;
            reason?: null | string;
            provider?: null | string;
        };
        CatalogServiceView: {
            service_id: string;
            product_id: string;
            display_name: string;
            unit: string;
            
            unit_price_cents: number | string;
            
            base_fee_cents: number | string;
            currency: string;
            lookup_key: string;
            active: boolean;
            metered: boolean;
            recurring_interval: null | string;
            stripe_price_id: null | string;
        };
        CatalogSyncEntryView: {
            service_id: string;
            lookup_key: string;
            stripe_price_id: null | string;
            stripe_product_id: null | string;
            status: string;
        };
        CatalogSyncResponse: {
            stripe_configured: boolean;
            entries: components["schemas"]["CatalogSyncEntryView"][];
        };
        ChatChoice: {
            
            index: number | string;
            message: components["schemas"]["ChatResponseMessage"];
            finish_reason: string;
        };
        ChatCompletionResponse: {
            id: string;
            object: string;
            
            created: number | string;
            model: string;
            choices: components["schemas"]["ChatChoice"][];
            billing: null | components["schemas"]["BillingReceipt"];
            metadata: components["schemas"]["ChatMetadata"];
        };
        ChatCompletionsRequest: {
            model: null | string;
            messages: null | components["schemas"]["ChatMessage"][];
            
            stream: boolean;
            
            max_tokens?: null | number | string;
            
            max_completion_tokens?: null | number | string;
            
            temperature?: null | number | string;
            
            top_p?: null | number | string;
            
            top_k?: null | number | string;
            
            window?: null | number | string;
            
            topic_boost?: null | number | string;
            stop?: unknown;
            
            web_search: boolean;
            
            web_search_results?: null | number | string;
        };
        ChatMessage: {
            role: null | string;
            content: null | string;
        };
        ChatMetadata: {
            
            witnesses?: null | number | string;
            
            reply_rows?: null | number | string;
            
            generated_tokens?: null | number | string;
            laplace?: null | components["schemas"]["LaplaceChatMetadata"];
        };
        ChatResponseMessage: {
            role: string;
            content: string;
        };
        CompletionChoice: {
            text: string;
            
            index: number | string;
            finish_reason: null | string;
            logprobs: null | components["schemas"]["CompletionLogprobs"];
        };
        CompletionLogprobs: {
            token_logprobs: (number | string)[];
        };
        CompletionResponse: {
            id: string;
            object: string;
            
            created: number | string;
            model: string;
            choices: components["schemas"]["CompletionChoice"][];
            billing: null | components["schemas"]["CompletionsReceipt"];
        };
        CompletionsReceipt: {
            quote_id: string;
            
            amount_cents: number | string;
            currency: string;
            tenant: string;
        };
        CompletionsRequest: {
            model: null | string;
            prompt: null | string;
            
            stream: boolean;
            
            max_tokens?: null | number | string;
            
            temperature?: null | number | string;
            
            top_p?: null | number | string;
            
            top_k?: null | number | string;
            
            window?: null | number | string;
            
            topic_boost?: null | number | string;
            stop?: unknown;
            
            echo: boolean;
            
            logprobs?: null | number | string;
        };
        ConsensusHealth: {
            
            evidenceRows: number | string;
            
            consensusRows: number | string;
            
            dedupRatio: null | number | string;
            
            avgWitnesses: null | number | string;
            
            maxWitnesses: null | number | string;
        };
        CreditConsumeRequest: {
            tenant: null | string;
            service_id: null | string;
            



            units: number | string;
        };
        CreditConsumeResponse: {
            accepted: boolean;
            tenant: string;
            plan_id: string;
            service_id: string;
            
            units: number | string;
            
            remaining: number | string;
            
            period_end: string;
            status: string;
        };
        EmbeddingsRequest: {
            model: null | string;
            input: null | components["schemas"]["JsonElement"];
        };
        EntitlementsResponse: {
            tenant: string;
            data: components["schemas"]["EntitlementView"][];
        };
        EntitlementView: {
            tenant: string;
            plan_id: string;
            status: string;
            
            period_start: string;
            
            period_end: string;
            monthly_credits: {
                [key: string]: number | string;
            };
            used_credits: {
                [key: string]: number | string;
            };
            stripe_customer_id: null | string;
            stripe_subscription_id: null | string;
            
            updated_at: string;
        };
        ErrorBody: {
            type: string;
            code: string;
            message: string;
        };
        ErrorResponse: {
            error: components["schemas"]["ErrorBody"];
        };
        EvidenceResponse: {
            entity_id: string;
            entity_label: string;
            evidence: components["schemas"]["LabeledEvidenceItem"][];
        };
        EvidenceSample: {
            typeIdHex: string;
            objectIdHex: string;
            sourceIdHex: string;
            contextIdHex: null | string;
            
            outcome: number | string;
            
            observationCount: number | string;
        };
        ExecuteHeader: {
            name: string;
            value: string;
        };
        ExplainQuoteRequest: {
            tenant: null | string;
            prompt: null | string;
            
            depth: number | string;
            
            beam: number | string;
            
            academic: boolean;
        };
        ExplainQuoteResponse: {
            quote_id: string;
            tenant: string;
            service_id: string;
            
            depth: number | string;
            
            beam: number | string;
            academic: boolean;
            
            estimated_trace_nodes: number | string;
            
            billable_units: number | string;
            unit: string;
            
            amount_cents: number | string;
            currency: string;
            status: string;
            
            expires_at: string;
            stripe_checkout_url: null | string;
            next: components["schemas"]["QuoteNextStep"];
        };
        ExplainReportRequest: {
            prompt: null | string;
            
            depth: number | string;
            
            beam: number | string;
            
            academic: boolean;
        };
        ExplainReportResponse: {
            id: string;
            object: string;
            
            created: number | string;
            prompt: string;
            
            depth: number | string;
            
            beam: number | string;
            academic: boolean;
            trace: components["schemas"]["ExplainTraceStep"][];
            billing: null | components["schemas"]["BillingReceipt"];
        };
        ExplainTraceStep: {
            
            depth: number | string;
            pathHex: string[];
            typePathHex: string[];
            entityIdHex: string;
            entityLabel: string;
            
            effectiveMu: number | string;
            
            pathMu: number | string;
            
            witnesses: number | string;
            evidence: components["schemas"]["EvidenceSample"][];
        };
        HealthResponse: {
            status: string;
            stream: string;
        };
        JsonElement: unknown;
        LabeledEvidenceItem: {
            type_id: string;
            type_label: string;
            object_id: string;
            object_label: string;
            source_id: string;
            source_label: string;
            context_id: null | string;
            
            outcome: number | string;
            
            observation_count: number | string;
        };
        LaplaceChatMetadata: {
            provenance: components["schemas"]["ProvenanceLine"][];
        };
        ModelInfo: {
            id: string;
            object: string;
            
            created: number | string;
            owned_by: string;
            status?: null | string;
        };
        ModelList: {
            object: string;
            data: components["schemas"]["ModelInfo"][];
        };
        NotImplementedBody: {
            type: string;
            code: string;
            endpoint: string;
            message: string;
        };
        NotImplementedResponse: {
            error: components["schemas"]["NotImplementedBody"];
        };
        PaymentRequiredBody: {
            type: string;
            code: string;
            message: string;
            detail: unknown;
        };
        PaymentRequiredResponse: {
            error: components["schemas"]["PaymentRequiredBody"];
        };
        PlanNextStep: {
            checkout_url: null | string;
            note: string;
        };
        PlanSubscribeRequest: {
            tenant: null | string;
        };
        PlanSubscribeResponse: {
            quote_id: string;
            tenant: string;
            plan_id: string;
            service_id: string;
            
            monthly_price_cents: number | string;
            
            amount_cents: number | string;
            currency: string;
            status: string;
            stripe_checkout_url: null | string;
            monthly_credits: {
                [key: string]: number | string;
            };
            next: components["schemas"]["PlanNextStep"];
        };
        PlanView: {
            plan_id: string;
            service_id: string;
            name: string;
            description: string;
            
            monthly_price_cents: number | string;
            currency: string;
            monthly_credits: {
                [key: string]: number | string;
            };
            included_product_ids: string[];
            support_tier: string;
            active: boolean;
        };
        PreflightQuoteResponse: {
            quote_id: string;
            tenant: string;
            service_id: string;
            
            units: number | string;
            
            amount_cents: number | string;
            currency: string;
            status: string;
            
            expires_at: string;
            stripe_checkout_url: null | string;
            next: components["schemas"]["QuoteNextStep"];
        };
        ProductPriceView: {
            service_id: string;
            unit: string;
            
            unit_price_cents: number | string;
            
            base_fee_cents: number | string;
            currency: string;
            lookup_key: string;
            metered: boolean;
            recurring_interval: null | string;
        };
        ProductView: {
            product_id: string;
            name: string;
            description: string;
            category: string;
            prices: components["schemas"]["ProductPriceView"][];
        };
        ProvenanceLine: {
            reply: string;
            
            eff_mu: number | string;
            
            witnesses: number | string;
        };
        QuoteNextStep: {
            execute_header: components["schemas"]["ExecuteHeader"];
            note: string;
        };
        QuoteStatusResponse: {
            quote_id: string;
            tenant: string;
            service_id: string;
            
            units: number | string;
            
            amount_cents: number | string;
            currency: string;
            status: string;
            consumed: boolean;
            stripe_checkout_url: null | string;
            
            expires_at: string;
        };
        RecipeQuoteRequest: {
            tenant: null | string;
            action: null | string;
            



            content_items: number | string;
            
            commercial: boolean;
            
            private_export: boolean;
        };
        RecipeQuoteResponse: {
            quote_id: string;
            tenant: string;
            service_id: string;
            action: string;
            
            content_items: number | string;
            commercial: boolean;
            private_export: boolean;
            
            metered_items: number | string;
            
            billable_units: number | string;
            unit: string;
            
            items_per_unit: number | string;
            
            amount_cents: number | string;
            currency: string;
            status: string;
            
            expires_at: string;
            stripe_checkout_url: null | string;
            next: components["schemas"]["QuoteNextStep"];
        };
        SubstrateAuditReport: {
            counts: components["schemas"]["SubstrateCount"][];
            consensus: null | components["schemas"]["ConsensusHealth"];
            
            multiSourceEntityCount: null | number | string;
            topRelations: components["schemas"]["VisualizationEdge"][];
        };
        SubstrateCount: {
            metric: string;
            
            value: number | string;
        };
        SubstrateVisualizationGraph: {
            nodes: components["schemas"]["VisualizationNode"][];
            edges: components["schemas"]["VisualizationEdge"][];
        };
        SynthesisQuoteRequest: {
            tenant: null | string;
            
            vocab_size: number | string;
            
            hidden_size: number | string;
            
            num_layers: number | string;
            
            num_heads: number | string;
            
            num_kv_heads?: null | number | string;
            



            intermediate_size: number | string;
            
            tied_embeddings: boolean;
            format?: null | string;
        };
        SynthesisQuoteResponse: {
            quote_id: string;
            tenant: string;
            service_id: string;
            
            estimated_parameters: number | string;
            
            billable_units: number | string;
            unit: string;
            
            amount_cents: number | string;
            currency: string;
            status: string;
            
            expires_at: string;
            stripe_checkout_url: null | string;
            format: string;
            next: components["schemas"]["QuoteNextStep"];
        };
        UsageEntry: {
            quoteId: string;
            tenant: string;
            serviceId: string;
            
            units: number | string;
            
            amountCents: number | string;
            
            executedAt: string;
        };
        UsageResponse: {
            tenant: string;
            
            total_amount_cents: number | string;
            entries: components["schemas"]["UsageEntry"][];
        };
        VisualizationEdge: {
            subjectIdHex: string;
            subject: string;
            typeIdHex: string;
            type: string;
            objectIdHex: string;
            object: string;
            
            effectiveMu: number | string;
            
            witnesses: number | string;
        };
        VisualizationExecuteRequest: {
            
            limit?: null | number | string;
            
            include_geometry: boolean;
            
            include_evidence: boolean;
            format?: null | string;
        };
        VisualizationGraphResponse: {
            id: string;
            object: string;
            
            created: number | string;
            format: string;
            include_geometry: boolean;
            include_evidence: boolean;
            graph: components["schemas"]["SubstrateVisualizationGraph"];
            billing: null | components["schemas"]["BillingReceipt"];
        };
        VisualizationNode: {
            idHex: string;
            label: string;
            
            x: null | number | string;
            
            y: null | number | string;
            
            z: null | number | string;
            
            m: null | number | string;
            
            radius: null | number | string;
            
            constituents: null | number | string;
            
            evidenceRows: null | number | string;
        };
        VisualizationQuoteRequest: {
            tenant: null | string;
            
            nodes: number | string;
            



            edges: number | string;
            
            include_geometry: boolean;
            
            include_evidence: boolean;
            
            interactive: boolean;
            format?: null | string;
        };
        VisualizationQuoteResponse: {
            quote_id: string;
            tenant: string;
            service_id: string;
            
            nodes: number | string;
            
            edges: number | string;
            include_geometry: boolean;
            include_evidence: boolean;
            interactive: boolean;
            format: string;
            
            metered_items: number | string;
            
            billable_units: number | string;
            unit: string;
            
            items_per_unit: number | string;
            
            amount_cents: number | string;
            currency: string;
            status: string;
            
            expires_at: string;
            stripe_checkout_url: null | string;
            next: components["schemas"]["QuoteNextStep"];
        };
        WebhookResponse: {
            accepted: boolean;
            verified: boolean;
            duplicate: boolean;
            event_id: null | string;
            event_type: null | string;
            status: string;
            tenant: null | string;
            service_id: null | string;
            quote_id: null | string;
            plan_id: null | string;
        };
    };
    responses: never;
    parameters: never;
    requestBodies: never;
    headers: never;
    pathItems: never;
}
export type $defs = Record<string, never>;
export type operations = Record<string, never>;
