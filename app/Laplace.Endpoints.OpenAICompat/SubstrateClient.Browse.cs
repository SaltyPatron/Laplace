using System.Diagnostics;
using Laplace.Api.Contracts;
using Laplace.Data;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed partial class SubstrateClient
{
    public Task<ExploreBrowseResponse> ExploreBrowseAsync(
        string query,
        string queryRootIdHex,
        IReadOnlyList<string> queryMemberIdsHex,
        int offset,
        int limit,
        int candidateCapacity,
        CancellationToken ct) =>
        ExploreBrowseAsync(
            query,
            queryRootIdHex,
            queryMemberIdsHex,
            Array.Empty<string>(),
            discover: true,
            offset,
            limit,
            candidateCapacity,
            ct);

    public async Task<ExploreBrowseResponse> ExploreBrowseAsync(
        string query,
        string queryRootIdHex,
        IReadOnlyList<string> queryMemberIdsHex,
        IReadOnlyList<string> variantRootIdsHex,
        bool discover,
        int offset,
        int limit,
        int candidateCapacity,
        CancellationToken ct)
    {
        // A 32-hex query is the entity id itself: its spelling is not decomposed into
        // members, so only the direct-id arm can match.
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

        var variantRoots = directId
            ? Array.Empty<byte[]>()
            : variantRootIdsHex
                .Where(static h => !string.IsNullOrWhiteSpace(h))
                .Select(static h => h.ToLowerInvariant())
                .Where(h => !string.Equals(h, rootHex, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
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
                conn, members, root, variantRoots, discover, offset, limit, candidateCapacity, ct, TranslateReadError);
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

    public async Task<ExploreLeaderboardResponse> ExploreLeaderboardAsync(
        int offset,
        int limit,
        string sort,
        short? tier,
        CancellationToken ct)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            var page = await NpgsqlBrowseReads.LeaderboardAsync(
                conn, offset, limit, sort, tier, ct, TranslateReadError);
            var hits = page.Rows.Select(static r => new ExploreLeaderboardHit(
                r.IdHex,
                r.Label,
                r.Tier,
                r.Confidence,
                r.Rating,
                r.Deviation,
                r.Matches,
                r.PartsJson)).ToList();
            return new ExploreLeaderboardResponse(
                Object: "laplace.explore.leaderboard",
                Offset: page.Offset,
                Limit: page.Limit,
                Sort: page.Sort,
                Tier: page.Tier,
                ElapsedUs: page.ElapsedUs,
                Hits: hits);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Leaderboard query failed.", ex);
        }
    }
}
