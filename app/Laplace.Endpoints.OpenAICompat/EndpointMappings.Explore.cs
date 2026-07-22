using Laplace.Api.Contracts;

namespace Laplace.Endpoints.OpenAICompat;

internal static class ExploreEndpoints
{
    public static void MapExploreEndpoints(this WebApplication app)
    {
        MapMatchupEndpoints(app);

        app.MapGet("/v1/explore/catalog", async (ISubstrateClient substrate, CancellationToken ct) =>
        {
            try
            {
                var catalog = await substrate.ExploreCatalogAsync(ct);
                return Results.Json(catalog);
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
        })
        .WithTags("explore")
        .Produces<ExploreCatalogResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/explore/resolve", async (string? reference, ISubstrateClient substrate, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(reference))
                return EndpointJson.BadRequest("invalid_request_error", "Query parameter 'reference' is required.");

            try
            {
                var resolved = await substrate.ExploreResolveAsync(reference.Trim(), ct);
                if (resolved is null)
                    return EndpointJson.NotFound("entity_not_found", $"No entity for reference '{reference.Trim()}'.");

                return Results.Json(resolved);
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
        })
        .WithTags("explore")
        .Produces<ExploreResolveResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/explore/entities/{idHex}/preview", async (
            string idHex, ISubstrateClient substrate, CancellationToken ct) =>
        {
            try
            {
                var preview = await substrate.ExploreEntityPreviewAsync(idHex, ct);
                if (preview is null)
                    return EndpointJson.BadRequest("invalid_request_error", "Invalid entity id hex.");

                return Results.Json(preview);
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
        })
        .WithTags("explore")
        .Produces<ExploreEntityPreviewResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // Not-found explorer: keyed by the SURFACE (a content hash can't be
        // reversed to recover "conflagurate"), so the caller passes the original
        // reference. The anchor is computed in-process; neighbours come from the
        // bound-anchor KNN. Returns 200 with navigable neighbours, never a 503,
        // for a valid-but-unwitnessed word.
        app.MapGet("/v1/explore/notfound", async (
            string? reference,
            int? geodesic_k,
            int? frechet_k,
            double? frechet_max,
            ExploreDecomposeService decompose,
            ISubstrateClient substrate,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(reference))
                return EndpointJson.BadRequest("invalid_request_error", "Query parameter 'reference' is required.");

            var surface = reference.Trim();
            try
            {
                var anchor = decompose.ComputeAnchor(surface);
                var neighbors = await substrate.ExploreAnchorNeighborsAsync(
                    anchor,
                    Math.Clamp(geodesic_k ?? 12, 1, 48),
                    Math.Clamp(frechet_k ?? 12, 1, 48),
                    Math.Clamp(frechet_max ?? 0.5, 0.0, 2.0),
                    ct);

                // Did-you-mean by surface edit distance: witnessed words within one
                // edit of what was typed. Deterministic and exact -- no fuzzy index,
                // no scan -- because we generate the edit-distance-1 neighbourhood and
                // keep only the word ids that entity_exists. "conflagrate" is one
                // deletion from "conflagurate", so it surfaces directly.
                var refLower = surface.ToLowerInvariant();
                var witnessed = await substrate.WitnessedWordsAsync(
                    EditDistance1Candidates(refLower), ct);
                var suggestions = witnessed
                    .Select(w => (w, dist: Levenshtein(refLower, w.Surface.ToLowerInvariant(), 3)))
                    .Where(x => x.dist > 0)
                    .OrderBy(x => x.dist).ThenByDescending(x => x.w.Witnesses)
                    .Take(8)
                    .Select(x => new ExploreSuggestion(x.w.Surface, x.w.IdHex, x.dist))
                    .ToList();

                return Results.Json(new ExploreNotFoundResponse(
                    Reference: surface,
                    WordIdHex: anchor.WordIdHex,
                    Exists: false,
                    Coord: new[] { anchor.Cx, anchor.Cy, anchor.Cz, anchor.Cm },
                    Decomposition: anchor.Decomposition,
                    Neighbors: neighbors,
                    Suggestions: suggestions,
                    DidYouMean: suggestions.Count > 0 ? suggestions[0].Surface : null));
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
        })
        .WithTags("explore")
        .Produces<ExploreNotFoundResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/explore/entities/{idHex}", async (
            HttpRequest request,
            string idHex,
            int? consensus_limit,
            int? evidence_limit,
            ISubstrateClient substrate,
            IBillingOrchestrator billing,
            CancellationToken ct) =>
        {
            return await RunGatedExploreAsync(request, billing, "inspect", ct, async gateQuote =>
            {
                var entity = await substrate.ExploreEntityAsync(
                    idHex,
                    consensus_limit ?? 80,
                    evidence_limit ?? 40,
                    ct);
                if (entity is null)
                    return EndpointJson.BadRequest("invalid_request_error", "Invalid entity id hex.");

                if (gateQuote is not null) await billing.MarkConsumedAndRecordAsync(gateQuote, ct);

                return Results.Json(new ExploreEntityDetailResponse(
                    Id: $"entity-{Guid.NewGuid():N}",
                    Object: "laplace.explore.entity",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Entity: entity,
                    Billing: gateQuote is null ? null : QuoteGate.MakeReceipt(gateQuote)));
            });
        })
        .WithTags("explore")
        .Produces<ExploreEntityDetailResponse>()
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/v1/explore/decompose", async (
            HttpRequest request, ExploreDecomposeService decompose, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<DecomposeRequest>(request, ct);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Text))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'text' is required.");

            try
            {
                return Results.Json(decompose.Decompose(payload.Text));
            }
            catch (InvalidOperationException ex)
            {
                return EndpointJson.ServiceUnavailable("decompose_unavailable", ex.Message);
            }
        })
        .WithTags("explore")
        .Accepts<DecomposeRequest>("application/json")
        .Produces<DecomposeResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/v1/explore/entities/{idHex}/export", async (
            HttpRequest request,
            string idHex,
            ISubstrateClient substrate,
            IBillingOrchestrator billing,
            IReportQuoteCalculator quotes,
            CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<ExploreTrainingExportRequest>(request, ct)
                ?? new ExploreTrainingExportRequest(null, null, true, true);

            return await RunGatedExploreAsync(request, billing, "recipe.export", ct, async gateQuote =>
            {
                var export = await substrate.ExploreTrainingExportAsync(
                    idHex,
                    payload.ConsensusLimit ?? 120,
                    payload.EvidenceLimit ?? 80,
                    payload.IncludeMembers,
                    payload.IncludePeers,
                    ct);
                if (export is null)
                    return EndpointJson.BadRequest("invalid_request_error", "Invalid entity id hex.");

                if (gateQuote is not null) await billing.MarkConsumedAndRecordAsync(gateQuote, ct);

                return Results.Json(new ExploreTrainingExportDetailResponse(
                    Id: $"export-{Guid.NewGuid():N}",
                    Object: "laplace.explore.training_export",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Export: export,
                    Billing: gateQuote is null ? null : QuoteGate.MakeReceipt(gateQuote)));
            });
        })
        .WithTags("explore")
        .Accepts<ExploreTrainingExportRequest>("application/json")
        .Produces<ExploreTrainingExportDetailResponse>()
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/explore/entities/{idHex}/neighbors", async (
            HttpRequest request,
            string idHex,
            int? k,
            ISubstrateClient substrate,
            IBillingOrchestrator billing,
            CancellationToken ct) =>
        {
            return await RunGatedExploreAsync(request, billing, "nn", ct, async gateQuote =>
            {
                var neighbors = await substrate.ExploreNeighborsAsync(idHex, k ?? 10, ct);
                if (neighbors is null)
                    return EndpointJson.BadRequest("invalid_request_error", "Invalid entity id hex.");

                if (gateQuote is not null) await billing.MarkConsumedAndRecordAsync(gateQuote, ct);

                return Results.Json(new ExploreNeighborsDetailResponse(
                    Id: $"neighbors-{Guid.NewGuid():N}",
                    Object: "laplace.explore.neighbors",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Neighbors: neighbors,
                    Billing: gateQuote is null ? null : QuoteGate.MakeReceipt(gateQuote)));
            });
        })
        .WithTags("explore")
        .Produces<ExploreNeighborsDetailResponse>()
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/explore/entities/{idHex}/members", async (
            HttpRequest request,
            string idHex,
            int? limit,
            ISubstrateClient substrate,
            IBillingOrchestrator billing,
            CancellationToken ct) =>
        {
            return await RunGatedExploreAsync(request, billing, "visualization.deep_export", ct, async gateQuote =>
            {
                var members = await substrate.ExploreMembersAsync(idHex, limit ?? 100, ct);
                if (members is null)
                    return EndpointJson.BadRequest("invalid_request_error", "Invalid entity id hex.");

                if (gateQuote is not null) await billing.MarkConsumedAndRecordAsync(gateQuote, ct);

                return Results.Json(new ExploreMembersDetailResponse(
                    Id: $"members-{Guid.NewGuid():N}",
                    Object: "laplace.explore.members",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Members: members,
                    Billing: gateQuote is null ? null : QuoteGate.MakeReceipt(gateQuote)));
            });
        })
        .WithTags("explore")
        .Produces<ExploreMembersDetailResponse>()
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/explore/entities/{idHex}/peers", async (
            HttpRequest request,
            string idHex,
            int? limit,
            ISubstrateClient substrate,
            IBillingOrchestrator billing,
            CancellationToken ct) =>
        {
            return await RunGatedExploreAsync(request, billing, "visualization.deep_export", ct, async gateQuote =>
            {
                var peers = await substrate.ExplorePeersAsync(idHex, limit ?? 48, ct);
                if (peers is null)
                    return EndpointJson.BadRequest("invalid_request_error", "Invalid entity id hex.");

                if (gateQuote is not null) await billing.MarkConsumedAndRecordAsync(gateQuote, ct);

                return Results.Json(new ExplorePeersDetailResponse(
                    Id: $"peers-{Guid.NewGuid():N}",
                    Object: "laplace.explore.peers",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Peers: peers,
                    Billing: gateQuote is null ? null : QuoteGate.MakeReceipt(gateQuote)));
            });
        })
        .WithTags("explore")
        .Produces<ExplorePeersDetailResponse>()
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/explore/entities/{idHex}/containers", async (
            HttpRequest request,
            string idHex,
            int? max_hops,
            int? limit,
            ISubstrateClient substrate,
            IBillingOrchestrator billing,
            CancellationToken ct) =>
        {
            return await RunGatedExploreAsync(request, billing, "visualization.deep_export", ct, async gateQuote =>
            {
                var containers = await substrate.ExploreContainersAsync(
                    idHex, max_hops ?? 2, limit ?? 200, ct);
                if (containers is null)
                    return EndpointJson.BadRequest("invalid_request_error", "Invalid entity id hex.");

                if (gateQuote is not null) await billing.MarkConsumedAndRecordAsync(gateQuote, ct);

                return Results.Json(new ExploreContainersDetailResponse(
                    Id: $"containers-{Guid.NewGuid():N}",
                    Object: "laplace.explore.containers",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Containers: containers,
                    Billing: gateQuote is null ? null : QuoteGate.MakeReceipt(gateQuote)));
            });
        })
        .WithTags("explore")
        .Produces<ExploreContainersDetailResponse>()
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/explore/entities/{idHex}/graph", async (
            HttpRequest request,
            string idHex,
            int? hops,
            int? fanout,
            ISubstrateClient substrate,
            IBillingOrchestrator billing,
            CancellationToken ct) =>
        {
            return await RunGatedExploreAsync(request, billing, "visualization.deep_export", ct, async gateQuote =>
            {
                var graph = await substrate.ExploreConsensusGraphAsync(
                    idHex, hops ?? 2, fanout ?? 10, ct);
                if (graph is null)
                    return EndpointJson.BadRequest("invalid_request_error", "Invalid entity id hex.");

                if (gateQuote is not null) await billing.MarkConsumedAndRecordAsync(gateQuote, ct);

                return Results.Json(new ExploreGraphDetailResponse(
                    Id: $"graph-{Guid.NewGuid():N}",
                    Object: "laplace.explore.consensus_graph",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Graph: graph,
                    Billing: gateQuote is null ? null : QuoteGate.MakeReceipt(gateQuote)));
            });
        })
        .WithTags("explore")
        .Produces<ExploreGraphDetailResponse>()
        .Produces<PaymentRequiredResponse>(StatusCodes.Status402PaymentRequired)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
    }

    // The edit-distance-1 neighbourhood of a lowercase word: deletions,
    // substitutions, insertions, and adjacent transpositions over [a-z]. ~54n
    // strings for length n -- resolved in one batched entity_exists round trip,
    // so did-you-mean is an exact index probe, not a fuzzy scan. Capped to keep
    // a pasted paragraph from generating a runaway set.
    private static IReadOnlyList<string> EditDistance1Candidates(string word)
    {
        if (string.IsNullOrEmpty(word) || word.Length > 40)
            return Array.Empty<string>();

        const string alpha = "abcdefghijklmnopqrstuvwxyz";
        var set = new HashSet<string>(StringComparer.Ordinal);
        var n = word.Length;

        for (var i = 0; i < n; i++)
            set.Add(word.Remove(i, 1));                       // deletions
        for (var i = 0; i < n - 1; i++)                       // transpositions
            if (word[i] != word[i + 1])
            {
                var c = word.ToCharArray();
                (c[i], c[i + 1]) = (c[i + 1], c[i]);
                set.Add(new string(c));
            }
        for (var i = 0; i < n; i++)                           // substitutions
            foreach (var ch in alpha)
                if (word[i] != ch) set.Add(word.Remove(i, 1).Insert(i, ch.ToString()));
        for (var i = 0; i <= n; i++)                          // insertions
            foreach (var ch in alpha)
                set.Add(word.Insert(i, ch.ToString()));

        set.Remove(word);
        return set.ToArray();
    }

    // Bounded Levenshtein: returns -1 once every cell in a row exceeds `max`
    // (early-out for the re-rank), the true distance otherwise.
    private static int Levenshtein(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return -1;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var rowMin = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (cur[j] < rowMin) rowMin = cur[j];
            }
            if (rowMin > max) return -1;
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    private static void MapMatchupEndpoints(WebApplication app)
    {
        // The entity's verdict record — confirmed/contested/refuted/thin counts
        // from the canonical epistemic_status logic. Preview-class information,
        // served ungated like the entity preview.
        app.MapGet("/v1/explore/entities/{idHex}/mesh", async (string idHex, ISubstrateClient substrate, CancellationToken ct) =>
        {
            try
            {
                var mesh = await substrate.MeshAsync(idHex, ct);
                return mesh is null
                    ? EndpointJson.NotFound("entity_not_found", $"'{idHex}' is not a 32-hex entity id.")
                    : Results.Json(mesh);
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
        })
        .WithTags("explore")
        .Produces<MeshResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/explore/sources/{idHex}/roster", async (string idHex, int? limit, ISubstrateClient substrate, CancellationToken ct) =>
        {
            byte[] sid;
            try { sid = Convert.FromHexString(idHex); }
            catch (FormatException) { return EndpointJson.NotFound("source_not_found", $"'{idHex}' is not a hex source id."); }
            try
            {
                var rows = await substrate.SourceRosterAsync(sid, Math.Clamp(limit ?? 40, 1, 200), ct);
                return Results.Json(new SourceRosterResponse("source.roster", idHex.ToLowerInvariant(), rows));
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
        })
        .WithTags("explore")
        .Produces<SourceRosterResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/explore/entities/{idHex}/record", async (string idHex, ISubstrateClient substrate, CancellationToken ct) =>
        {
            try
            {
                var record = await substrate.EntityRecordAsync(idHex, ct);
                return record is null
                    ? EndpointJson.NotFound("entity_not_found", $"'{idHex}' is not a 32-hex entity id.")
                    : Results.Json(record);
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
        })
        .WithTags("explore")
        .Produces<EntityRecordResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // Head-to-head, fast half: both cards + the tale of the tape. Gated as
        // an inspect-class read.
        app.MapGet("/v1/explore/matchup", async (string? x, string? y, HttpRequest request, ISubstrateClient substrate, IBillingOrchestrator billing, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(x) || string.IsNullOrWhiteSpace(y))
                return EndpointJson.BadRequest("invalid_request_error", "Query parameters 'x' and 'y' are required.");
            return await RunGatedExploreAsync(request, billing, "inspect", ct, async _ =>
            {
                var matchup = await substrate.MatchupAsync(x.Trim(), y.Trim(), ct);
                return matchup is null
                    ? EndpointJson.NotFound("topic_not_witnessed", "One of the topics resolves to nothing witnessed.")
                    : Results.Json(matchup);
            });
        })
        .WithTags("explore")
        .Produces<MatchupResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // Head-to-head, slow half: the witnessed path and the verdict.
        // relation_summary runs a deep path search (measured 6-14s under an
        // active seed) — a separate fetch so the tape never waits on it.
        app.MapGet("/v1/explore/matchup/verdict", async (string? x, string? y, HttpRequest request, ISubstrateClient substrate, IBillingOrchestrator billing, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(x) || string.IsNullOrWhiteSpace(y))
                return EndpointJson.BadRequest("invalid_request_error", "Query parameters 'x' and 'y' are required.");
            return await RunGatedExploreAsync(request, billing, "inspect", ct, async _ =>
            {
                var verdict = await substrate.MatchupVerdictAsync(x.Trim(), y.Trim(), ct);
                return verdict is null
                    ? EndpointJson.NotFound("topic_not_witnessed", "One of the topics resolves to nothing witnessed.")
                    : Results.Json(verdict);
            });
        })
        .WithTags("explore")
        .Produces<MatchupVerdictResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> RunGatedExploreAsync(
        HttpRequest request,
        IBillingOrchestrator billing,
        string serviceId,
        CancellationToken ct,
        Func<BillingQuote?, Task<IResult>> produce)
    {
        var gate = await QuoteGate.RequireQuoteAsync(request, billing, serviceId, ct);
        if (!gate.Allowed)
            return EndpointJson.PaymentRequired(gate.Code, gate.Message, gate.Quote is null ? null : new QuotePendingDetail(gate.Quote.QuoteId, gate.Quote.Status, gate.Quote.StripeCheckoutUrl));

        try
        {
            return await produce(gate.Quote);
        }
        catch (SubstrateUnavailableException ex)
        {
            return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
        }
    }
}
