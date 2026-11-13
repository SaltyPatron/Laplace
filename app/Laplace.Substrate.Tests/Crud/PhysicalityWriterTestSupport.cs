using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>Assertions for canonical physicality rows.</summary>
internal static class PhysicalityWriterTestSupport
{
    internal static void AssertAttempts(
        ApplyResult result, int entities, int physicalities, int attestations)
    {
        Assert.Equal(entities, result.EntitiesAttempted);
        Assert.Equal(physicalities, result.PhysicalitiesAttempted);
        Assert.Equal(attestations, result.AttestationsAttempted);
    }

    internal static async Task AssertSelectedRowsAsync(NpgsqlDataSource dataSource,
        IEnumerable<Hash128> entities, IEnumerable<Hash128> physicalities,
        IEnumerable<Hash128> attestations)
    {
        byte[][] e = entities.Distinct().Select(id => id.ToBytes()).ToArray();
        byte[][] p = physicalities.Distinct().Select(id => id.ToBytes()).ToArray();
        byte[][] a = attestations.Distinct().Select(id => id.ToBytes()).ToArray();
        Assert.Equal(e.LongLength, await CountAsync("entities", e));
        Assert.Equal(p.LongLength, await CountAsync("physicalities", p));
        Assert.Equal(a.LongLength, await CountAsync("attestations", a));

        // Counted in slices: one bound array of every id would exceed a statement's limits.
        async Task<long> CountAsync(string table, byte[][] ids)
        {
            long total = 0;
            for (int start = 0; start < ids.Length; start += 50_000)
            {
                await using var command = dataSource.CreateCommand(
                    $"SELECT count(DISTINCT id) FROM laplace.{table} WHERE id=ANY($1::bytea[])");
                command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
                    ids[start..Math.Min(ids.Length, start + 50_000)]);
                total += (long)(await command.ExecuteScalarAsync())!;
            }
            return total;
        }
    }
}
