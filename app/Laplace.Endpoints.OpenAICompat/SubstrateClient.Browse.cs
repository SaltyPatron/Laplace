using System.Diagnostics;
using Laplace.Api.Contracts;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed partial class SubstrateClient
{
    public async Task<ExploreBrowseResponse> ExploreBrowseAsync(
        string query,
        string queryRootIdHex,
        IReadOnlyList<string> queryMemberIdsHex,
        int offset,
        int limit,
        int candidateCapacity,
        CancellationToken ct)
    {
        // A canonical id is already resolved. Do not decompose its hexadecimal spelling
        // into text and then search the name lane for those characters: that would turn a
        // direct address into an unrelated content query. The endpoint still uses the same
        // Browse result shape, but the candidate member set is empty and only the direct
        // canonical entity arm can match.
        var directId = LooksLikeEntityHex(query);
        var rootHex = directId ? query.ToLowerInvariant() : queryRootIdHex.ToLowerInvariant();
        var root = TryParseIdHex(rootHex)
            ?? throw new ArgumentException("Browse root id must be a 128-bit entity id.", nameof(queryRootIdHex));

        List<string> normalizedMemberHex = directId
            ? new List<string>()
            : queryMemberIdsHex
                .Where(static h => !string.IsNullOrWhiteSpace(h))
                .Select(static h => h.ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        var members = normalizedMemberHex
            .Select(TryParseIdHex)
            .Where(static id => id is not null)
            .Select(static id => id!)
            .ToArray();

        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 0, 200);
        candidateCapacity = Math.Clamp(candidateCapacity, 0, 32768);

        var clock = Stopwatch.StartNew();
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            var rows = await NpgsqlBrowseReads.NamedEntitiesAsync(
                conn, members, root, offset, limit, candidateCapacity, ct, TranslateReadError);
            clock.Stop();

            var hits = rows.Select(static r => new ExploreBrowseHit(
                r.IdHex,
                r.Label,
                r.Tier,
                r.Type,
                r.MatchedNameIdHex,
                r.MatchKind,
                r.Rating,
                r.Rd,
                r.EffMu,
                r.Witnesses)).ToList();

            var first = rows.FirstOrDefault();
            return new ExploreBrowseResponse(
                Object: "laplace.explore.browse",
                Query: query,
                Hits: hits,
                Receipt: new ExploreBrowseReceipt(
                    QueryRootIdHex: rootHex,
                    QueryMemberIdsHex: normalizedMemberHex,
                    CandidateNames: rows.Count == 0 ? 0 : first.CandidateNames,
                    CandidateCapacity: candidateCapacity,
                    CandidateTruncated: rows.Count != 0 && first.CandidateTruncated,
                    MatchedEntities: rows.Count == 0 ? 0 : first.MatchedEntities,
                    Returned: hits.Count,
                    Offset: offset,
                    Limit: limit,
                    ElapsedUs: Math.Max(0, clock.ElapsedTicks * 1_000_000L / Stopwatch.Frequency)));
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Browse query failed.", ex);
        }
    }
}
