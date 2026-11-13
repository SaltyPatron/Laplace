using global::Npgsql;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// <c>feedback.claim_standing</c>: the Native standing of the claim a statement states, [subject, relation,
/// object] or [subject, relation] when the object is empty, its ID composed by the extension
/// (<c>laplace_compose</c> of <c>laplace_text_id</c>s) and its standing read by <c>laplace_standings</c>.
/// </summary>
public static class NpgsqlClaimStanding
{
    /// <summary>The claim, its rating, deviation and the games folded into it, and its conservative rating at k = 2.</summary>
    public readonly record struct Row(Hash128 Claim, double Rating, double Deviation, long Matches, double Confidence);

    /// <summary>The claim's standing, or null when no witness has attested it.</summary>
    public static async Task<Row?> ReadAsync(NpgsqlDataSource dataSource, string subject, string relation, string obj,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = NpgsqlCatalog.Command(conn, null, "feedback.claim_standing", subject, relation, obj);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new Row(Hash128.FromBytes((byte[])reader[0]), reader.GetDouble(1), reader.GetDouble(2),
            reader.GetInt64(3), reader.GetDouble(4));
    }
}

/// <summary><c>content.native_text</c>: the entity a text is (<c>laplace_text_id</c>) and that entity realized from the database alone (<c>laplace_text</c>), null when the database holds none.</summary>
public static class NpgsqlNativeText
{
    public static async Task<(Hash128 Id, string? Text)> ReadAsync(NpgsqlDataSource dataSource, string text,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = NpgsqlCatalog.Command(conn, null, "content.native_text", text);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0))
            throw new InvalidDataException("The text has no Native ID.");
        return (Hash128.FromBytes((byte[])reader[0]), reader.IsDBNull(1) ? null : reader.GetString(1));
    }
}
