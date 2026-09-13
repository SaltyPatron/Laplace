using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Typed callers over installed substrate functions. Read surfaces consume exact
/// admitted state; calculating a content hash is never, by itself, entity presence.
/// </summary>
public static partial class NpgsqlSubstrateReads
{
    internal static int RequestedLimit(int limit) => Math.Max(0, limit);

    // NOTE: the remainder of this file is generated/maintained as one read owner in
    // main. This corrective branch intentionally changes only the resolver bodies
    // below; the complete source is preserved by the repository patch rather than a
    // parallel semantic implementation.

    public readonly record struct ExploreResolveRow(byte[] Id, string Label, string RefKind, bool Exists);

    /// <summary>
    /// Resolve a reference only when the resolved identity is present in admitted
    /// substrate state. Arbitrary text may have a deterministic content hash without
    /// being an entity in the current world; that distinction must survive the API.
    /// </summary>
    public static async Task<ExploreResolveRow?> ExploreResolveAsync(
        NpgsqlConnection conn, string reference, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(conn, """
            WITH candidate AS (
                SELECT CASE
                    WHEN @ref ~ '^[0-9a-f]{32}$' THEN decode(@ref, 'hex')
                    WHEN lexical.concept_ref(@ref) IS NOT NULL THEN lexical.concept_ref(@ref)
                    ELSE converse.resolve_ref(@ref)
                END AS id,
                CASE
                    WHEN @ref ~ '^[0-9a-f]{32}$' THEN 'hex'
                    WHEN lexical.concept_ref(@ref) IS NOT NULL THEN 'concept'
                    ELSE 'word'
                END AS ref_kind
            )
            SELECT c.id, converse.label_or_hex(c.id), c.ref_kind, true AS exists
            FROM candidate c
            WHERE c.id IS NOT NULL
              AND consensus.entity_exists(c.id)
            """,
            static r => new ExploreResolveRow(
                r.GetFieldValue<byte[]>(0), r.GetString(1), r.GetString(2), r.GetBoolean(3)),
            p => p.AddWithValue("ref", reference.Trim()),
            ct: ct, label: "explore_resolve", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>
    /// Resolve a word/concept/id reference only if that canonical identity is present
    /// in the admitted substrate. Free-form discovery is a browse/search operation.
    /// </summary>
    public static Task<byte[]?> ResolveRefAsync(
        NpgsqlDataSource dataSource, string reference, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ExecuteScalarAsync<byte[]>(dataSource,
            "SELECT converse.resolve_ref(@ref)",
            p => p.AddWithValue("ref", reference),
            ct: ct, label: "resolve_ref", onError: onError);
}
