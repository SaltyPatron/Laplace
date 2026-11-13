using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Category", "RequiresDatabase")]
[Trait("Tier", "db")]
public sealed class NativeSqlBatchTests(LocalPgFixture pg)
{
    [Fact]
    public async Task WitnessScopesExcludeCrossProductsButRetainConflictingObjects()
    {
        var prefix = "witness-scope/" + Guid.NewGuid().ToString("N");
        Hash128 Id(string name) => Hash128.Text(prefix + "/" + name);
        var source = Id("source"); var otherSource = Id("other-source");
        var playing = Id("playing"); var line = Id("line"); var context = Id("context");
        var hasEvent = Id("has-event"); var hasResult = Id("has-result");
        var eventId = Id("event"); var resultId = Id("result"); var conflicting = Id("conflicting");
        AttestationRow Row(Hash128 subject, Hash128 type, Hash128 obj, Hash128 src, Hash128? ctx)
            => NativeAttestation.CategoricalResolved(subject, type, obj, src, ctx, SourceTrust.StructuredCorpus);
        var selectedEvent = Row(playing, hasEvent, eventId, source, null);
        var selectedResult = Row(line, hasResult, resultId, source, playing);
        var conflictingResult = Row(line, hasResult, conflicting, source, playing);
        var rows = new[] { selectedEvent, selectedResult, conflictingResult,
            Row(line, hasEvent, eventId, source, playing), // actual PGN header cross-product
            Row(line, hasResult, resultId, otherSource, playing),
            Row(line, hasResult, resultId, source, context),
            Row(line, hasResult, resultId, source, null) };
        var builder = new SubstrateChangeBuilder(source, prefix).DeclareSourcePrior(SourceTrust.StructuredCorpus);
        foreach (var id in new[] { source, otherSource, playing, line, context, hasEvent, hasResult, eventId, resultId, conflicting })
            builder.AddEntity(id, 0, id);
        foreach (var row in rows) builder.AddAttestation(row);
        var writer = new NpgsqlSubstrateWriter(pg.DataSource);
        await writer.ApplyAsync(builder.Build());
        NpgsqlAttestationReads.WitnessScope[] scopes =
        [new(playing, hasEvent, source, null), new(line, hasResult, source, playing), new(playing, hasEvent, source, null)];
        var actual = await NpgsqlAttestationReads.WitnessesAsync(pg.DataSource, scopes, CancellationToken.None);
        Assert.Equal(3, actual.Count);
        Assert.Equal<Hash128>(new[] { selectedEvent.Id, selectedResult.Id, conflictingResult.Id }.OrderBy(id => id.Hi).ThenBy(id => id.Lo),
            actual.Select(a => Hash128.FromBytes(a.Id)).OrderBy(id => id.Hi).ThenBy(id => id.Lo));
        Assert.Contains(actual, a => a.ContextId is null);
        Assert.Empty(await NpgsqlAttestationReads.WitnessesAsync(pg.DataSource, [], CancellationToken.None));
    }

    [Fact]
    public async Task ChessTransitionStandingsBindAnIdArrayThroughTheNativeCatalog()
    {
        // A [position, move, position] claim no recorded game attested has no standing.
        var claim = Hash128.Blake3("catalog-no-such-transition"u8);
        Assert.Empty(await NpgsqlChessTransitionStandings.ReadAsync(pg.DataSource, [claim], CancellationToken.None));
        Assert.Empty(NpgsqlChessTransitionStandings.Read(pg.DataSource, [claim]));
    }

    [Fact]
    public async Task IndependentCatalogReadsKeepParameterAndResultPositions()
    {
        await using var conn = await pg.DataSource.OpenConnectionAsync();
        await using var transaction = await conn.BeginTransactionAsync();
        byte[] first = Hash128.Text("catalog-batch/first").ToBytes();
        byte[] second = Hash128.Text("catalog-batch/second").ToBytes();
        await using (var setup = conn.CreateCommand())
        {
            setup.CommandText = """
                INSERT INTO laplace.entities(id,tier,type_id)
                VALUES($1,0,laplace.entity_type_id('Type')),($2,0,laplace.entity_type_id('Type'));
                """;
            setup.Parameters.AddWithValue(NpgsqlDbType.Bytea, first);
            setup.Parameters.AddWithValue(NpgsqlDbType.Bytea, second);
            await setup.ExecuteNonQueryAsync();
            setup.CommandText = "INSERT INTO laplace.canonical_names(id,name) VALUES($1,'First'),($2,'Second')";
            await setup.ExecuteNonQueryAsync();
        }
        var result = await NpgsqlRead.ReadBatchRowsAsync(conn,
            SqlCatalog.Get("entity.facets"),
            p => p.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Bytea, new[] { first }),
            r => r.GetFieldValue<byte[]>(0),
            SqlCatalog.Get("display.labels"),
            p => p.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Bytea, new[] { second, first, second }),
            r => r.GetString(1));
        Assert.Equal(first, Assert.Single(result.First));
        Assert.Equal(new[] { "Second", "First", "Second" }, result.Second);
        // The single-command route shares the same catalog contract, including
        // callers that still supply descriptive parameter names.
        var single = await NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("display.labels"),
            r => r.GetString(1),
            p => p.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Bytea, new[] { first, second, first }));
        Assert.Equal(new[] { "First", "Second", "First" }, single);
        await transaction.RollbackAsync();
    }
}
