using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

public sealed record ExploreBrowseHit(
    [property: JsonPropertyName("id_hex")] string IdHex,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("tier")] short Tier,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("matched_name_id_hex")] string MatchedNameIdHex,
    [property: JsonPropertyName("match_kind")] string MatchKind,
    [property: JsonPropertyName("rating")] decimal? Rating,
    [property: JsonPropertyName("rd")] decimal? Rd,
    [property: JsonPropertyName("eff_mu")] decimal? EffMu,
    [property: JsonPropertyName("witnesses")] long Witnesses);

public sealed record ExploreBrowseReceipt(
    [property: JsonPropertyName("query_root_id_hex")] string QueryRootIdHex,
    [property: JsonPropertyName("query_member_ids_hex")] IReadOnlyList<string> QueryMemberIdsHex,
    [property: JsonPropertyName("candidate_names")] long CandidateNames,
    [property: JsonPropertyName("candidate_capacity")] int CandidateCapacity,
    [property: JsonPropertyName("candidate_truncated")] bool CandidateTruncated,
    [property: JsonPropertyName("matched_entities")] long MatchedEntities,
    [property: JsonPropertyName("returned")] int Returned,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("elapsed_us")] long ElapsedUs);

public sealed record ExploreBrowseResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("hits")] IReadOnlyList<ExploreBrowseHit> Hits,
    [property: JsonPropertyName("receipt")] ExploreBrowseReceipt Receipt);
