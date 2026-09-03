using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Product leaderboard read over the canonical <c>ops.band_leaders</c> operation.
/// Ranking and realization remain substrate-owned; this class only binds parameters
/// and maps the typed result.  It lives in the sanctioned read layer so HTTP/UI/MCP
/// callers never grow their own SQL or presentation policy.
/// </summary>
public static class NpgsqlLeaderboardReads
{
    public readonly record struct BandLeaderDisplayRow(
        int Band,
        string SubjectIdHex,
        string Subject,
        string SubjectRealization,
        string? SubjectTechnicalName,
        string? SubjectTypeIdHex,
        string? SubjectTypeName,
        string RelationIdHex,
        string Relation,
        string RelationRealization,
        string? RelationTechnicalName,
        string ObjectIdHex,
        string Object,
        string ObjectRealization,
        string? ObjectTechnicalName,
        string? ObjectTypeIdHex,
        string? ObjectTypeName,
        decimal Rating,
        decimal Rd,
        decimal EffMu,
        long Witnesses);

    public static Task<IReadOnlyList<BandLeaderDisplayRow>> BandLeadersAsync(
        NpgsqlDataSource dataSource,
        int[] bands,
        int perBand,
        byte[]? languageId,
        CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT band,
                   encode(subject_id, 'hex'), subject, subject_realization,
                   subject_technical_name, encode(subject_type_id, 'hex'), subject_type_name,
                   encode(relation_id, 'hex'), relation, relation_realization,
                   relation_technical_name,
                   encode(object_id, 'hex'), object, object_realization,
                   object_technical_name, encode(object_type_id, 'hex'), object_type_name,
                   rating, rd, eff_mu, witnesses
            FROM ops.band_leaders(@bands, @per, @lang)
            """,
            static r => new BandLeaderDisplayRow(
                r.GetInt32(0),
                r.GetString(1),
                r.GetString(2),
                r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.GetString(7),
                r.GetString(8),
                r.GetString(9),
                r.IsDBNull(10) ? null : r.GetString(10),
                r.GetString(11),
                r.GetString(12),
                r.GetString(13),
                r.IsDBNull(14) ? null : r.GetString(14),
                r.IsDBNull(15) ? null : r.GetString(15),
                r.IsDBNull(16) ? null : r.GetString(16),
                r.GetDecimal(17),
                r.GetDecimal(18),
                r.GetDecimal(19),
                r.GetInt64(20)),
            p =>
            {
                p.Add("bands", NpgsqlDbType.Array | NpgsqlDbType.Integer).Value = bands;
                p.AddWithValue("per", perBand);
                p.Add(new NpgsqlParameter("lang", NpgsqlDbType.Bytea)
                {
                    Value = (object?)languageId ?? DBNull.Value,
                });
            },
            ct: ct,
            label: "band_leaders_display",
            onError: onError);
}
