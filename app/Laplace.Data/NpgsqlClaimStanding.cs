using Laplace.Engine.Core;
using Npgsql;
using NpgsqlTypes;

namespace Laplace.Data;

/// <summary>
/// Canonical claim standing via <c>public.laplace_compose</c> and <c>public.laplace_standings</c>.
/// </summary>
public static class NpgsqlClaimStanding
{
    public readonly record struct Row(Hash128 Claim, double Rating, double Deviation, long Matches, double Confidence);

    public static async Task<Row?> ReadAsync(
        NpgsqlDataSource dataSource, string subject, string relation, string obj, CancellationToken ct = default)
    {
        const string sql = """
            WITH target AS (
                SELECT CASE WHEN coalesce($3, '') = ''
                    THEN public.laplace_compose(ARRAY[public.laplace_text_id($1), public.laplace_text_id($2)])
                    ELSE public.laplace_compose(ARRAY[public.laplace_text_id($1), public.laplace_text_id($2), public.laplace_text_id($3)])
                END AS claim
            )
            SELECT public.blake3_send(t.claim), s.rating, s.deviation, s.matches,
                   public.laplace_confidence(s.rating, s.deviation, 2)
            FROM target t
            JOIN public.laplace_standings(ARRAY[t.claim]) s ON s.claim = t.claim
            WHERE t.claim IS NOT NULL
            """;

        var rows = await NpgsqlRead.ReadRowsAsync(
            dataSource,
            sql,
            static r => new Row(
                Hash128.FromBytes((byte[])r[0]),
                r.GetDouble(1),
                r.GetDouble(2),
                r.GetInt64(3),
                r.GetDouble(4)),
            p =>
            {
                p.AddWithValue(NpgsqlDbType.Text, subject);
                p.AddWithValue(NpgsqlDbType.Text, relation);
                p.AddWithValue(NpgsqlDbType.Text, obj ?? string.Empty);
            },
            ct: ct,
            label: "feedback_claim_standing").ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }
}

public static class NpgsqlNativeText
{
    public static async Task<(Hash128 Id, string? Text)> ReadAsync(
        NpgsqlDataSource dataSource, string text, CancellationToken ct = default)
    {
        const string sql = """
            WITH tid AS (SELECT public.laplace_text_id($1) AS id)
            SELECT public.blake3_send(id), public.laplace_text(id)
            FROM tid
            """;
        var rows = await NpgsqlRead.ReadRowsAsync(
            dataSource,
            sql,
            static r => (
                Present: !r.IsDBNull(0),
                Id: r.IsDBNull(0) ? default : Hash128.FromBytes((byte[])r[0]),
                Text: r.IsDBNull(1) ? null : r.GetString(1)),
            p => p.AddWithValue(NpgsqlDbType.Text, text),
            ct: ct,
            label: "native_text").ConfigureAwait(false);
        if (rows.Count == 0 || !rows[0].Present)
            throw new InvalidDataException("The text has no Native ID.");
        return (rows[0].Id, rows[0].Text);
    }
}
