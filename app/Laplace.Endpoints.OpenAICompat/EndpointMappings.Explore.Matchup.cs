using Laplace.Api.Contracts;
using Laplace.SubstrateCRUD;

namespace Laplace.Endpoints.OpenAICompat;

internal static partial class ExploreEndpoints
{
    private static int? DeriveGraphNodeCapacity(int hops, int fanout)
    {
        if (hops <= 0 || fanout <= 0) return 1;
        if (fanout == 1) return hops == int.MaxValue ? null : hops + 1;

        long total = 1;
        long width = 1;
        for (var hop = 0; hop < hops; hop++)
        {
            if (width > (int.MaxValue - total) / fanout) return null;
            width *= fanout;
            total += width;
        }
        return (int)total;
    }

    // The edit-distance-1 neighbourhood of a lowercase surface: deletions,
    // substitutions, insertions, and adjacent transpositions over [a-z], about 54n
    // strings for length n, resolved in one batched existence probe.
    private static IReadOnlyList<string> EditDistance1Candidates(string word)
    {
        if (string.IsNullOrEmpty(word))
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
        // Mesh, source roster, taxonomy, and verdict record are served without the
        // billing gate, as the entity preview is.
        app.MapGet("/v1/explore/entities/{idHex}/mesh", async (
            string idHex, int? relations, int? members,
            ISubstrateClient substrate, CancellationToken ct) =>
        {
            var mesh = await substrate.MeshAsync(
                idHex, Math.Max(0, relations ?? 40), Math.Max(0, members ?? 60), ct);
            return mesh is null
                ? EndpointJson.NotFound("entity_not_found", $"'{idHex}' is not a 32-hex entity id.")
                : Results.Json(mesh);
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

            var rows = await substrate.SourceRosterAsync(sid, Math.Max(0, limit ?? 40), ct);
            return Results.Json(new SourceRosterResponse("source.roster", idHex.ToLowerInvariant(), rows));
        })
        .WithTags("explore")
        .Produces<SourceRosterResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/explore/entities/{idHex}/taxonomy", async (
            string idHex, int? depth, int? children,
            ISubstrateClient substrate, CancellationToken ct) =>
        {
            var tax = await substrate.TaxonomyAsync(
                idHex, Math.Max(0, depth ?? 10), Math.Max(0, children ?? 24), ct);
            return tax is null
                ? EndpointJson.NotFound("entity_not_found", $"'{idHex}' is not a 32-hex entity id.")
                : Results.Json(tax);
        })
        .WithTags("explore")
        .Produces<TaxonomyResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/explore/entities/{idHex}/record", async (string idHex, ISubstrateClient substrate, CancellationToken ct) =>
        {
            var record = await substrate.EntityRecordAsync(idHex, ct);
            return record is null
                ? EndpointJson.NotFound("entity_not_found", $"'{idHex}' is not a 32-hex entity id.")
                : Results.Json(record);
        })
        .WithTags("explore")
        .Produces<EntityRecordResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // Head-to-head cards and tape, gated as an inspect-class read.
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

        app.MapGet("/v1/explore/lexicon", async (string? x, string? y,
            HttpRequest request, ISubstrateClient substrate, IBillingOrchestrator billing, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(x))
                return EndpointJson.BadRequest("invalid_request_error", "Query parameter 'x' is required.");
            return await RunGatedExploreAsync(request, billing, "inspect", ct, async _ =>
            {
                var response = await substrate.LexiconAsync(x.Trim(), string.IsNullOrWhiteSpace(y) ? null : y.Trim(), ct);
                return response is null
                    ? EndpointJson.NotFound("topic_not_witnessed", "One of the topics resolves to nothing witnessed.")
                    : Results.Json(response);
            });
        }).WithTags("explore").Produces<ResponseFieldResponse>();

        // The response field is an inspection of all Native coupling routes. It
        // remains separate from the fast cards and the direct-claim summary.
        app.MapGet("/v1/explore/response-field", async (string? x, string? y, int? fanout, int? nearest,
            HttpRequest request, ISubstrateClient substrate, IBillingOrchestrator billing, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(x) || string.IsNullOrWhiteSpace(y))
                return EndpointJson.BadRequest("invalid_request_error", "Query parameters 'x' and 'y' are required.");
            int selectedFanout = fanout ?? 512, selectedNearest = nearest ?? 16;
            if (selectedFanout is < 1 or > 4096 || selectedNearest is < 0 or > 256)
                return EndpointJson.BadRequest("invalid_request_error", "fanout must be 1–4096; nearest must be 0–256.");
            return await RunGatedExploreAsync(request, billing, "inspect", ct, async _ =>
            {
                var response = await substrate.ResponseFieldAsync(x.Trim(), y.Trim(), selectedFanout, selectedNearest, ct);
                return response is null
                    ? EndpointJson.NotFound("topic_not_witnessed", "One of the topics resolves to nothing witnessed.")
                    : Results.Json(response);
            });
        }).WithTags("explore").Produces<ResponseFieldResponse>();

        // Direct-claim summary; it does not decide whether an indirect route exists.
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

        return await produce(gate.Quote);
    }
}
