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
    [Theory]
    [InlineData("/vault/models/Florence-2-base/configuration_florence2.py", "python")]
    [InlineData("engine/core/include/laplace/core/version.h", "c")]
    [InlineData("web/src/explore/components/StatCard.tsx", "typescript")]
    [InlineData("extension/laplace_substrate/sql/uninstall_laplace_substrate.sql.in", "sql")]
    public async Task SelectedSource_GenericWorkerPersistsFileTrunkAndObservation(string path, string modality)
    {
        if (!Path.IsPathRooted(path))
            path = Path.Combine(Laplace.Decomposers.Abstractions.Tests.TypeIdLawTests.FindRepoRootPublic(), path);
        string fileName = Path.GetFileName(path);
        byte[] source = await File.ReadAllBytesAsync(path);
        var observed = GrammarSourceFileSupport.MetadataFromPath(
            path, fileName, modality);

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

        using var ast = GrammarDecomposer.Parse(source, modality);
        using var composer = new GrammarRowComposer(
            source, ast, CodeSource.SourceId, modality, GrammarCompositionMode.FullSource);
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
            pg.DataSource, content.Id, modality);
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
        journal.Parameters.AddWithValue("label", $"code/{fileName}");
        await using var row = await journal.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        Assert.Equal(fileName, row.GetString(0));
        Assert.Equal(source.LongLength, row.GetInt64(1));
        DateTime modified = row.GetFieldValue<DateTime>(2);
        Assert.InRange(
            Math.Abs((modified.ToUniversalTime() - observed.ModifiedUtc).TotalMilliseconds),
            0,
            1);
        Assert.Equal("ok", row.GetString(3));
    }
}
