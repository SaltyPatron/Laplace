using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class GrammarSourceIngestTests(LocalPgFixture pg)
{
    [Fact]
    public async Task WholeFileHandler_PersistsSourceStructureAndReconstructsCode()
    {
        CodepointPerfcache.LoadDefault();
        const string sourceText = "def greet(name):\n    # Keep source layout.\n    return name\n";
        byte[] bytes = Encoding.UTF8.GetBytes(sourceText);
        Hash128 source = Hash128.OfCanonical("whole-source-ingest-functional-test");
        var record = new GrammarComposeRecord(bytes, "python");
        var handler = new GrammarComposeHandler(source, 1, null);
        using var unit = handler.CreateDeferredUnit(record);
        var builder = new SubstrateChangeBuilder(source, "test/source/greet.py");
        Hash128 root = unit.DrainInto(builder, 1, null);
        handler.WalkWitness(record, root, builder, unit);

        var writer = new NpgsqlSubstrateWriter(pg.DataSource);
        await writer.ApplyAsync(builder.Build());
        byte[] reconstructed = await NpgsqlContentReconstructor.ReconstructUtf8Async(
            pg.DataSource, root, "python");
        Assert.Equal(bytes, reconstructed);
        await using var conn = await pg.DataSource.OpenConnectionAsync();
        var children = await NpgsqlSubstrateReads.PackedTrajectoryVerticesAsync(
            conn, root.ToBytes(), default);
        Assert.NotEmpty(children);
    }

    [Fact]
    public void WholeFileHandler_ReportsUnknownGrammarInsteadOfAnEmptySuccessfulUnit()
    {
        var handler = new GrammarComposeHandler(default, 1, null);
        Assert.Throws<InvalidOperationException>(() => handler.CreateDeferredUnit(
            new GrammarComposeRecord("code"u8.ToArray(), "unregistered-test-grammar")));
    }
}
