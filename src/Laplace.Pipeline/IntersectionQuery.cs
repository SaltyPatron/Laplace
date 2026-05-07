namespace Laplace.Pipeline;

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Pipeline.Abstractions;

using Npgsql;

/// <summary>
/// Concrete <see cref="IIntersectionQuery"/>. Knowledge IS edges + intersections —
/// this surface implements the intersection counts and enumerations the
/// substrate is built to answer. Phase 2 / Track D / D8.
///
/// "How many things intersect with the number 3.14?" — count entities whose
/// composition tree contains the entity for "3.14" via entity_child traversal.
/// </summary>
public sealed class IntersectionQuery : IIntersectionQuery
{
    private readonly NpgsqlDataSource _dataSource;

    public IntersectionQuery(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<long> CountAsync(AtomId target, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // Count distinct ancestors via recursive entity_child traversal.
        await using var cmd = new NpgsqlCommand(@"
            WITH RECURSIVE ancestors AS (
                SELECT parent_hash, parent_tier
                  FROM entity_child
                 WHERE child_hash = @target
                UNION
                SELECT ec.parent_hash, ec.parent_tier
                  FROM entity_child ec
                  JOIN ancestors a ON ec.child_hash = a.parent_hash
                                  AND ec.child_tier = a.parent_tier
            )
            SELECT COUNT(DISTINCT (parent_hash, parent_tier)) FROM ancestors", conn);
        cmd.Parameters.AddWithValue("target", target.AsSpan().ToArray());
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long l ? l : 0L;
    }

    public async IAsyncEnumerable<AtomId> EnumerateAsync(
        AtomId target, int limit,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(@"
            WITH RECURSIVE ancestors AS (
                SELECT parent_hash, parent_tier
                  FROM entity_child
                 WHERE child_hash = @target
                UNION
                SELECT ec.parent_hash, ec.parent_tier
                  FROM entity_child ec
                  JOIN ancestors a ON ec.child_hash = a.parent_hash
                                  AND ec.child_tier = a.parent_tier
            )
            SELECT DISTINCT parent_hash FROM ancestors LIMIT @lim", conn);
        cmd.Parameters.AddWithValue("target", target.AsSpan().ToArray());
        cmd.Parameters.AddWithValue("lim",    limit);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var hash = (byte[]) reader.GetValue(0);
            yield return new AtomId(hash);
        }
    }
}
