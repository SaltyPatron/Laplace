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
    public async Task WholeSourceComposition_ClosesAtMeasuredNativeMemoryAndDrainsRemainingFiles()
    {
        string repo = Laplace.Decomposers.Abstractions.Tests.TypeIdLawTests.FindRepoRootPublic();
        var records = new[] { "tier_tree.c", "glicko2.c" }
            .Select(name => new GrammarComposeRecord(
                File.ReadAllBytes(Path.Combine(repo, "engine", "core", "src", name)), "c"))
            .ToList();
        var expectedBytes = records.Select(r => r.Utf8.LongLength).ToArray();
        Hash128 source = Hash128.OfCanonical("native-compose-memory-admission");
        var reader = new NpgsqlSubstrateReader(pg.DataSource);
        var handler = new GrammarComposeHandler(source, 1, reader);
        var config = new IngestBatchConfig
        {
            SourceId = source,
            BatchLabelPrefix = "native-compose-memory-admission",
            BatchSize = records.Count,
            WorkingSet = true,
            ConcurrentWorkingSets = Math.Max(1, IngestTopology.Current.ComposeWorkers),
            WorkingSetProfile = IngestSourceProfile.Default,
            ContainmentReader = reader,
        };
        var builder = new SubstrateChangeBuilder(source, "native-compose-memory-admission");
        int composed = 0;
        while (records.Count > 0)
        {
            using var batch = await IngestDescentFlush.ComposeBatchAsync(
                records, handler, reader, builder, config, null, default,
                residentBudgetBytes: 1);
            var item = Assert.Single(batch.Pending);
            Assert.True(item.Unit.ResidentBytes > expectedBytes[composed],
                "Native AST, lexical trees and compose buffers must be included beyond source bytes.");
            Assert.True(batch.ResidentBytes >= item.Unit.ResidentBytes);
            Assert.Equal(expectedBytes.Length - ++composed, records.Count);
            await IngestDescentFlush.FinalizeWorkingSetAsync(
                batch, handler, reader, builder, config, null, default);
            Assert.Empty(batch.Pending);
        }
        var change = builder.Build();
        long entities = change.Entities.Length;
        long physicalities = change.Physicalities.Length;
        if (!change.IntentStages.IsDefaultOrEmpty)
            foreach (var stage in change.IntentStages)
            {
                entities += stage.EntityCount;
                physicalities += stage.PhysicalityCount;
            }
        Assert.True(entities > 0);
        Assert.True(physicalities > 0);
        await new NpgsqlSubstrateWriter(pg.DataSource).ApplyAsync(change);
        Assert.Equal(expectedBytes.Length, composed);
    }

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
