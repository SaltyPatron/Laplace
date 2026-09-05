using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Code;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class CodeSourceFileIngestTests(LocalPgFixture pg)
{
    [Fact]
    public async Task SelectedFlorenceSource_GenericWorkerPersistsFileTrunkAndObservation()
    {
        const string path = "/vault/models/Florence-2-base/configuration_florence2.py";
        byte[] source = await File.ReadAllBytesAsync(path);
        var observed = GrammarSourceFileSupport.MetadataFromPath(
            path, "configuration_florence2.py", "python");

        CodepointPerfcache.LoadDefault();
        var writer = new NpgsqlSubstrateWriter(pg.DataSource);
        var reader = new NpgsqlSubstrateReader(pg.DataSource);
        var observability = new NpgsqlIngestObservability(pg.DataSource);
        var runner = new IngestRunner(writer, reader, NullLoggerFactory.Instance, observability);

        IngestRunResult result = await runner.RunAsync(
            new CodeDecomposer(),
            IngestRunOptions.Default with
            {
                EcosystemPath = path,
                SkipLayerOrderingCheck = true,
                SkipSourceCompletion = true,
            });

        Assert.Equal(1, result.InputUnitsDone);
        Assert.Equal(1, result.FilesDone);
        Assert.Equal(0, result.UnitsFailed);
        Assert.Empty(result.Failures);

        using var ast = GrammarDecomposer.Parse(source, "python");
        using var composer = new GrammarRowComposer(
            source, ast, CodeSource.SourceId, "python", GrammarCompositionMode.FullSource);
        OrderedCompositionComponent content = composer.RootComponent();
        FileIdentity file = FileEntity.Resolve(content, observed);

        await using var conn = await pg.DataSource.OpenConnectionAsync();
        var children = await NpgsqlSubstrateReads.PackedTrajectoryVerticesAsync(
            conn, file.FileId.ToBytes(), default);
        Assert.Collection(
            children.OrderBy(static child => child.Ordinal),
            child => Assert.Equal(
                Convert.ToHexStringLower(content.Id.ToBytes()), child.ChildIdHex),
            child => Assert.Equal(
                Convert.ToHexStringLower(file.MetadataRootId.ToBytes()), child.ChildIdHex));

        byte[] reconstructed = await NpgsqlContentReconstructor.ReconstructUtf8Async(
            pg.DataSource, content.Id, "python");
        Assert.Equal(source, reconstructed);

        await using var journal = new NpgsqlCommand(
            """
            SELECT relative_path, bytes, modified_at, status
            FROM laplace.ingest_file_journal
            WHERE source_name = @source AND file_label = @label
            ORDER BY ended_at DESC, run_id DESC
            LIMIT 1
            """, conn);
        journal.Parameters.AddWithValue("source", CodeSource.SourceName);
        journal.Parameters.AddWithValue("label", "code/configuration_florence2.py");
        await using var row = await journal.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        Assert.Equal("configuration_florence2.py", row.GetString(0));
        Assert.Equal(source.LongLength, row.GetInt64(1));
        DateTime modified = row.GetFieldValue<DateTime>(2);
        Assert.InRange(
            Math.Abs((modified.ToUniversalTime() - observed.ModifiedUtc).TotalMilliseconds),
            0,
            1);
        Assert.Equal("ok", row.GetString(3));
    }
}
