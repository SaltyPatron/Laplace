using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Trait("Tier", "db")]
public sealed class GutenbergRetainedDbTests
{
    private const string GutenbergFile =
        "/vault/Data/ProjectGutenberg/text/sc-elementary-science-series.txt";

    [SkippableFact]
    public async Task SelectedPhysicalEdition_RoundTripsNativeFileMetadataAndSharedWork()
    {
        string? connectionString = Environment.GetEnvironmentVariable("LAPLACE_GUTENBERG_RETAINED_DB");
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set LAPLACE_GUTENBERG_RETAINED_DB to an explicitly retained isolated database.");
        Skip.IfNot(File.Exists(GutenbergFile), "selected complete Gutenberg edition is not mounted");

        CodepointPerfcache.LoadDefault();
        byte[] sourceBytes = await File.ReadAllBytesAsync(GutenbergFile);
        string relativePath = Path.GetFileName(GutenbergFile);
        ContentIngestRecord extracted = await SingleAsync(
            DocumentFileExtract.OpenAsync(GutenbergFile, relativePath, default));
        FileMetadata metadata = Assert.IsType<FileMetadata>(extracted.Metadata);
        DocumentFormatMetadata format = Assert.IsType<DocumentFormatMetadata>(metadata.FormatMetadata);
        Assert.Equal("43384", format.EbookId);
        Assert.Equal("A Complete List of the Books Included in the S. & C. Series of Elementary Manuals for Mechanics and Students published by E. & F. N. Spon, Ltd., London. January 1912", format.Title);
        Assert.Equal("E. & F. N. Spon", format.Author);
        Assert.Equal("English", format.Language);
        Assert.Equal("August 2, 2013 [eBook #43384]", format.ReleaseDate);
        Assert.Equal("October 23, 2024", format.UpdatedDate);
        Assert.NotNull(format.Credits);
        Assert.NotNull(format.HeaderBoundary);
        Assert.NotNull(format.HeaderBoundaryByteOffset);

        FileIdentity file = FileEntity.Resolve(sourceBytes, metadata);
        WorkIdentity work = WorkEntity.Resolve(format.Title!, format.Author);
        IngestArtifactGraph graph = Assert.IsType<IngestArtifactGraph>(
            DocumentDecomposer.BuildArtifactGraph(GutenbergFile));
        IngestArtifact artifact = Assert.Single(graph.Artifacts);
        Assert.Equal(IngestArtifactDisposition.Admitted, artifact.Disposition);
        Assert.Equal(sourceBytes.LongLength, artifact.Bytes);

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.ConnectionStringBuilder.CommandTimeout = 0;
        await using var dataSource = dataSourceBuilder.Build();
        var process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        long rssBefore = process.WorkingSet64;
        var stopwatch = Stopwatch.StartNew();

        var inner = new NpgsqlSubstrateWriter(dataSource);
        await using var writer = new ConsensusAccumulatingWriter(inner, dataSource);
        var observer = new NpgsqlIngestObservability(dataSource, evidencePersisted: true);
        var runner = new IngestRunner(
            writer, new NpgsqlSubstrateReader(dataSource), NullLoggerFactory.Instance, observer);
        IngestRunResult result = await runner.RunAsync(
            new DocumentDecomposer(),
            IngestRunOptions.Default with
            {
                EcosystemPath = GutenbergFile,
                SkipLayerOrderingCheck = true,
                SkipSourceCompletion = true,
                BatchSize = 1,
                CommitRows = 0,
            });

        stopwatch.Stop();
        process.Refresh();
        long cpuMilliseconds = (long)(process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        long rssAfter = process.WorkingSet64;
        long peakRss = process.PeakWorkingSet64;

        Assert.Empty(result.Failures);
        Assert.Equal(0, result.UnitsFailed);
        Assert.True(result.UnitsAttempted >= result.InputUnitsDone);
        Assert.Equal(1, result.InputUnitsDone);
        Assert.Equal(1, result.FilesDone);

        Guid runId = await ScalarAsync<Guid>(dataSource,
            "SELECT run_id FROM laplace.ingest_run_journal "
            + "WHERE source_name = $1 ORDER BY started_at DESC LIMIT 1",
            DocumentSource.SourceName);
        RunJournal run = await ReadRunAsync(dataSource, runId);
        Assert.Equal("ok", run.Status);
        Assert.Equal(1, run.FilesDone);
        Assert.Equal(1, run.InputUnitsDone);
        Assert.Equal(1, run.InputUnitsTotal);
        ArtifactJournal journal = await ReadArtifactAsync(dataSource, runId);
        Assert.Equal("document/sc-elementary-science-series.txt", journal.FileLabel);
        Assert.Equal("sc-elementary-science-series.txt", journal.RelativePath);
        Assert.Equal("admitted", journal.Disposition);
        Assert.Equal("ok", journal.Status);
        Assert.Equal(sourceBytes.LongLength, journal.Bytes);
        Assert.Equal(1, journal.Records);
        Assert.Equal(file.FileId.ToBytes(), journal.FileId);
        Assert.Null(journal.ResumeFingerprint);

        await using var connection = await dataSource.OpenConnectionAsync();
        Assert.True(await ScalarAsync<bool>(connection,
            "SELECT EXISTS (SELECT 1 FROM laplace.entities WHERE id = $1)",
            DocumentSource.SourceId.ToBytes()));
        Assert.True(await ScalarAsync<bool>(connection,
            "SELECT EXISTS (SELECT 1 FROM laplace.entities "
            + "WHERE id = $1 AND first_observed_by = $2)",
            file.FileId.ToBytes(), DocumentSource.SourceId.ToBytes()));

        var children = await NpgsqlSubstrateReads.PackedTrajectoryVerticesAsync(
            connection, file.FileId.ToBytes(), default);
        Assert.Collection(
            children.OrderBy(static child => child.Ordinal),
            child => Assert.Equal(Hex(file.ContentRootId), child.ChildIdHex),
            child => Assert.Equal(Hex(file.MetadataRootId), child.ChildIdHex));

        byte[] reconstructedContent = await NpgsqlContentReconstructor.ReconstructUtf8Async(
            dataSource, file.ContentRootId);
        byte[] canonicalSource = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(sourceBytes).Normalize(NormalizationForm.FormC));
        Assert.Equal(canonicalSource, reconstructedContent);
        byte[] reconstructedMetadata = await NpgsqlContentReconstructor.ReconstructUtf8Async(
            dataSource, file.MetadataRootId);
        Assert.Equal(metadata.IdentityCanonicalUtf8(), reconstructedMetadata);
        FileMetadata durableMetadata = FileMetadata.ParseIdentityCanonicalUtf8(reconstructedMetadata);
        Assert.Equal(format, durableMetadata.FormatMetadata);

        Hash128 expresses = DocumentSource.Resolve(DocumentRelation.Expresses).Id;
        Hash128 hasTitle = DocumentSource.Resolve(DocumentRelation.HasTitle).Id;
        Hash128 authoredBy = DocumentSource.Resolve(DocumentRelation.AuthoredBy).Id;
        Assert.True(await EdgeExistsAsync(
            connection, file.FileId, expresses, work.WorkId, file.FileId));
        Assert.True(await EdgeExistsAsync(
            connection, work.WorkId, hasTitle, work.TitleId, file.FileId));
        Assert.True(await EdgeExistsAsync(
            connection, work.WorkId, authoredBy, work.AuthorId!.Value, file.FileId));
        long reverseEditions = await ScalarAsync<long>(connection,
            "SELECT count(DISTINCT subject_id) FROM laplace.attestations "
            + "WHERE type_id = $1 AND object_id = $2",
            expresses.ToBytes(), work.WorkId.ToBytes());
        Assert.True(reverseEditions >= 1);

        string proofDirectory = "/tmp/laplace-content-recovery-proof";
        Directory.CreateDirectory(proofDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(proofDirectory, "gutenberg-retained-db.json"),
            JsonSerializer.Serialize(new
            {
                Database = new NpgsqlConnectionStringBuilder(connectionString).Database,
                Source = new { DocumentSource.SourceName, Id = Hex(DocumentSource.SourceId) },
                SelectedPhysicalArtifact = new
                {
                    artifact.RelativePath,
                    artifact.FileLabel,
                    artifact.DispositionName,
                    Bytes = sourceBytes.LongLength,
                    FileId = Hex(file.FileId),
                    ContentId = Hex(file.ContentRootId),
                    MetadataId = Hex(file.MetadataRootId),
                },
                FormatMetadata = format,
                Work = new
                {
                    Id = Hex(work.WorkId),
                    TitleId = Hex(work.TitleId),
                    AuthorId = Hex(work.AuthorId!.Value),
                    work.NormalizedTitle,
                    work.NormalizedAuthor,
                    ReverseEditionCount = reverseEditions,
                    TwoEditionConvergence = reverseEditions >= 2
                        ? "observed in retained database"
                        : "single selected physical edition; two-edition convergence is covered by ProjectGutenbergMetadataTests",
                },
                Reconstruction = new
                {
                    CanonicalContentBytes = reconstructedContent.LongLength,
                    CanonicalContentExact = true,
                    MetadataBytes = reconstructedMetadata.LongLength,
                    MetadataExact = true,
                },
                Result = new
                {
                    runId,
                    result.UnitsAttempted,
                    result.UnitsApplied,
                    result.EntitiesInserted,
                    result.PhysicalitiesInserted,
                    result.AttestationsInserted,
                    RowsInserted = result.EntitiesInserted + result.PhysicalitiesInserted
                        + result.AttestationsInserted,
                    DatabaseCalls = result.TotalRoundTrips,
                    RunnerElapsedMilliseconds = (long)result.WallClock.TotalMilliseconds,
                    OperatorElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                    CpuMilliseconds = cpuMilliseconds,
                    RssBeforeBytes = rssBefore,
                    RssAfterBytes = rssAfter,
                    ProcessPeakRssBytes = peakRss,
                },
                Journal = journal,
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<ContentIngestRecord> SingleAsync(
        IAsyncEnumerable<ContentIngestRecord> records)
    {
        var result = new List<ContentIngestRecord>();
        await foreach (ContentIngestRecord record in records) result.Add(record);
        return Assert.Single(result);
    }

    private static async Task<bool> EdgeExistsAsync(
        NpgsqlConnection connection,
        Hash128 subject,
        Hash128 type,
        Hash128 obj,
        Hash128 source) =>
        await ScalarAsync<bool>(connection,
            "SELECT EXISTS (SELECT 1 FROM laplace.attestations "
            + "WHERE subject_id = $1 AND type_id = $2 AND object_id = $3 AND source_id = $4)",
            subject.ToBytes(), type.ToBytes(), obj.ToBytes(), source.ToBytes());

    private static async Task<RunJournal> ReadRunAsync(NpgsqlDataSource dataSource, Guid runId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT status, files_done, input_units_done, input_units_total "
            + "FROM laplace.ingest_run_journal WHERE run_id = $1");
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, runId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new RunJournal(
            reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private static async Task<ArtifactJournal> ReadArtifactAsync(
        NpgsqlDataSource dataSource, Guid runId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT file_label, relative_path, disposition, status, bytes, records, file_id, "
            + "resume_fingerprint "
            + "FROM laplace.ingest_file_journal WHERE run_id = $1");
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, runId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var row = new ArtifactJournal(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt64(4), reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<byte[]>(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<byte[]>(7));
        Assert.False(await reader.ReadAsync());
        return row;
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlDataSource dataSource, string sql, params object[] parameters)
    {
        await using var command = dataSource.CreateCommand(sql);
        AddParameters(command, parameters);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection, string sql, params object[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static void AddParameters(NpgsqlCommand command, object[] parameters)
    {
        for (int i = 0; i < parameters.Length; i++)
            command.Parameters.AddWithValue(
                parameters[i] is byte[] ? NpgsqlDbType.Bytea : NpgsqlDbType.Text,
                parameters[i]);
    }

    private static string Hex(Hash128 id) => Convert.ToHexStringLower(id.ToBytes());

    private sealed record RunJournal(
        string Status, long FilesDone, long InputUnitsDone, long InputUnitsTotal);

    private sealed record ArtifactJournal(
        string FileLabel,
        string RelativePath,
        string Disposition,
        string Status,
        long Bytes,
        long Records,
        byte[]? FileId,
        byte[]? ResumeFingerprint);
}
