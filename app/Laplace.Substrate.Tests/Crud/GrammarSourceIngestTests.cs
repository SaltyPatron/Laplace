using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Code;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Logging.Abstractions;
using Parquet;
using Parquet.Schema;
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

    [Fact]
    public async Task ObservedPromptResponse_PreservesCompleteTextsOrderAndDistinctInstructions()
    {
        CodepointPerfcache.LoadDefault();
        const string prompt = "Do not sort x.\nKeep e\u0301 and all spaces.\n";
        byte[] code = "def keep(x):\n    return x\n"u8.ToArray();
        Hash128 source = Hash128.OfCanonical("observed-code-pair-test");
        var handler = new GrammarComposeHandler(source, 1, null);
        var record = new GrammarComposeRecord(code, "python",
            ObservedPromptUtf8: Encoding.UTF8.GetBytes(prompt));
        var builder = new SubstrateChangeBuilder(source, "test/code-pair");
        using var unit = handler.CreateDeferredUnit(record);
        Hash128 root = unit.DrainInto(builder, 1, null);
        var change = builder.Build();
        var link = Assert.Single(change.Attestations.Where(a =>
            a.TypeId == RelationTypeRegistry.Resolve("HAS_EXAMPLE").Id));
        Assert.Equal(root, link.ContextId);
        Assert.NotEqual(link.SubjectId, link.ObjectId);
        await new NpgsqlSubstrateWriter(pg.DataSource).ApplyAsync(change);

        await using var connection = await pg.DataSource.OpenConnectionAsync();
        var children = await NpgsqlSubstrateReads.PackedTrajectoryVerticesAsync(
            connection, root.ToBytes(), default);
        var ordered = children.OrderBy(c => c.Ordinal).ToArray();
        Assert.Equal(2, ordered.Length);
        Assert.Equal(Convert.ToHexStringLower(link.ObjectId!.Value.ToBytes()), ordered[1].ChildIdHex);
        Hash128 observedPrompt = Hash128.FromBytes(Convert.FromHexString(ordered[0].ChildIdHex));
        Assert.Equal(ContentTierSpine.ResolveRoot(record.ObservedPromptUtf8), link.SubjectId);
        Assert.Equal(record.ObservedPromptUtf8,
            await NpgsqlContentReconstructor.ReconstructUtf8Async(pg.DataSource, observedPrompt, "markdown"));
        Assert.Equal(code,
            await NpgsqlContentReconstructor.ReconstructUtf8Async(pg.DataSource, link.ObjectId.Value, "python"));

        using var different = handler.CreateDeferredUnit(record with
        {
            ObservedPromptUtf8 = Encoding.UTF8.GetBytes(prompt.Replace("Do not", "Do", StringComparison.Ordinal)),
        });
        var otherBuilder = new SubstrateChangeBuilder(source, "test/different-code-pair");
        Assert.NotEqual(root, different.DrainInto(otherBuilder, 1, null));
        foreach (var stage in otherBuilder.Build().IntentStages) stage.Dispose();
    }

    [Fact]
    public async Task TinyCodesPhysicalShard_PreservesResponsesWithoutALanguageGrammar()
    {
        var dir = Directory.CreateTempSubdirectory("tiny-codes-coverage");
        const string prompt = "Find nodes in Neo4j, keeping x and not y.\n";
        const string response = "Use this query:\n```cypher\nMATCH (x) RETURN x\n```\n";
        try
        {
            string path = Path.Combine(dir.FullName, "observations.parquet");
            var p = new DataField<string>("prompt");
            var r = new DataField<string>("response");
            var l = new DataField<string>("programming_language");
            await using (var fs = File.Create(path))
            await using (var writer = await ParquetWriter.CreateAsync(new ParquetSchema(p, r, l), fs))
            {
                using var group = writer.CreateRowGroup();
                await group.WriteAsync(p, new[] { prompt });
                await group.WriteAsync(r, new[] { response });
                await group.WriteAsync(l, new[] { "Neo4j database and Cypher" });
            }
            await foreach (var observed in SharedParquetRecordStream.ReadTinyCodesRowsAsync(path, default))
                Assert.Null(observed.ConceptKey);
            CodepointPerfcache.LoadDefault();
            var runner = new IngestRunner(new NpgsqlSubstrateWriter(pg.DataSource),
                new NpgsqlSubstrateReader(pg.DataSource), NullLoggerFactory.Instance,
                new NpgsqlIngestObservability(pg.DataSource));
            var result = await runner.RunAsync(new TinyCodesDecomposer(), IngestRunOptions.Default with
            {
                EcosystemPath = path,
                SkipLayerOrderingCheck = true,
                SkipSourceCompletion = true,
            });
            Assert.Equal(1, result.InputUnitsDone);
            Assert.Equal(1, result.FilesDone);
            Assert.Equal(0, result.UnitsFailed);
            Assert.Empty(result.Failures);

            using var promptAst = GrammarDecomposer.Parse(Encoding.UTF8.GetBytes(prompt), "markdown");
            using var promptComposer = new GrammarRowComposer(Encoding.UTF8.GetBytes(prompt),
                promptAst, TinyCodesSource.SourceId, "markdown", GrammarCompositionMode.FullSource);
            using var responseAst = GrammarDecomposer.Parse(Encoding.UTF8.GetBytes(response), "markdown");
            using var responseComposer = new GrammarRowComposer(Encoding.UTF8.GetBytes(response),
                responseAst, TinyCodesSource.SourceId, "markdown", GrammarCompositionMode.FullSource);
            Assert.Equal(Encoding.UTF8.GetBytes(prompt),
                await NpgsqlContentReconstructor.ReconstructUtf8Async(pg.DataSource,
                    promptComposer.RootComponent().Id, "markdown"));
            Assert.Equal(Encoding.UTF8.GetBytes(response),
                await NpgsqlContentReconstructor.ReconstructUtf8Async(pg.DataSource,
                    responseComposer.RootComponent().Id, "markdown"));
            // Use the installed SQL resolver that chat calls, not the source
            // grammar composer, to retrieve this exact witnessed response.
            await using var connection = await pg.DataSource.OpenConnectionAsync();
            await using var lookup = new global::Npgsql.NpgsqlCommand(
                """
                SELECT EXISTS (
                    SELECT 1 FROM laplace.attestations
                    WHERE subject_id = (SELECT root_id FROM converse.prompt_tree(@prompt) LIMIT 1)
                      AND type_id = @relation AND source_id = @source
                      AND object_id = @response AND context_id IS NOT NULL)
                """, connection);
            lookup.Parameters.AddWithValue("prompt", prompt);
            lookup.Parameters.AddWithValue("relation", RelationTypeRegistry.Resolve("HAS_EXAMPLE").Id.ToBytes());
            lookup.Parameters.AddWithValue("source", TinyCodesSource.SourceId.ToBytes());
            lookup.Parameters.AddWithValue("response", responseComposer.RootComponent().Id.ToBytes());
            Assert.Equal(true, await lookup.ExecuteScalarAsync());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
