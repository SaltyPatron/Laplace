using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>Assertions distinguish source rows from the native descriptor graphs
/// that the same normal apply now admits. No formula predicts generated row counts.</summary>
internal static class PhysicalityWriterTestSupport
{
    internal static PhysicalityAdmissionReceipt AssertAttempts(
        ApplyResult result, int entities, int physicalities, int attestations, long sourceForms)
    {
        var receipt = Assert.IsType<PhysicalityAdmissionReceipt>(result.PhysicalityAdmission);
        Assert.Equal(sourceForms, receipt.SourceForms);
        Assert.Equal(entities + receipt.GeneratedEntityRows, result.EntitiesAttempted);
        Assert.Equal(physicalities + receipt.GeneratedPhysicalityRows, result.PhysicalitiesAttempted);
        Assert.Equal(attestations + receipt.GeneratedAttestationRows, result.AttestationsAttempted);
        return receipt;
    }

    internal static async Task AssertSelectedRowsAsync(NpgsqlDataSource dataSource,
        IEnumerable<Hash128> entities, IEnumerable<Hash128> physicalities,
        IEnumerable<Hash128> attestations)
    {
        byte[][] e = entities.Distinct().Select(id => id.ToBytes()).ToArray();
        byte[][] p = physicalities.Distinct().Select(id => id.ToBytes()).ToArray();
        byte[][] a = attestations.Distinct().Select(id => id.ToBytes()).ToArray();
        await using var command = dataSource.CreateCommand("""
            SELECT (SELECT count(DISTINCT id) FROM laplace.entities WHERE id=ANY($1::bytea[])),
                   (SELECT count(*) FROM laplace.physicalities WHERE id=ANY($2::bytea[])),
                   (SELECT count(*) FROM laplace.attestations WHERE id=ANY($3::bytea[]))
            """);
        foreach (var ids in new[] { e, p, a })
            command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, ids);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(e.LongLength, reader.GetInt64(0));
        Assert.Equal(p.LongLength, reader.GetInt64(1));
        Assert.Equal(a.LongLength, reader.GetInt64(2));
        Assert.False(await reader.ReadAsync());
    }
}
