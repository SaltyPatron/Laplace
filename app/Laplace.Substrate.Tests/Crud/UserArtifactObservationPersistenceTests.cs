using System.Diagnostics;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Abstractions.Tests;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class UserArtifactObservationPersistenceTests : IAsyncLifetime
{
    private readonly LocalPgFixture _pg;

    public UserArtifactObservationPersistenceTests(LocalPgFixture pg) => _pg = pg;

    public async Task InitializeAsync()
    {
        string schema = Path.Combine(
            TypeIdLawTests.FindRepoRootPublic(),
            "extension", "laplace_substrate", "sql", "schema", "tables",
            "ingest_file_journal.sql.in");
        await ApplySqlFileAsync(schema);
        string readback = Path.Combine(
            TypeIdLawTests.FindRepoRootPublic(),
            "extension", "laplace_substrate", "sql", "functions", "ops",
            "ingest_files.sql.in");
        await ApplySqlFileAsync(readback);

        await using var command = _pg.DataSource.CreateCommand("""
            INSERT INTO laplace.entities (id, tier, type_id, first_observed_by)
            VALUES (laplace.word_id('☃'), 0, laplace.entity_type_id('Codepoint'), NULL)
            ON CONFLICT DO NOTHING
            """);
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RepeatedAdmission_PreservesEachObservationWithoutChangingFileIdentity()
    {
        string tenant = $"observation-{Guid.NewGuid():N}";
        const string fileLabel = "evidence/artifact.txt";
        byte[] content = Encoding.UTF8.GetBytes("durable occurrence observation");
        DateTime firstModified = new(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        DateTime secondModified = new(2026, 7, 8, 9, 10, 11, DateTimeKind.Utc);

        UserArtifactContent.ArtifactIds first;
        await using (var closer = new ContentArtifactCloser(_pg.DataSource))
            first = Assert.IsType<UserArtifactContent.ArtifactIds>(
                await closer.CloseTextAsync(
                    tenant, "artifact.txt", fileLabel, content, modifiedUtc: firstModified));
        var firstEvidence = await EvidenceStateAsync(first.SourceId);
        UserArtifactContent.ArtifactIds second;
        await using (var closer = new ContentArtifactCloser(_pg.DataSource))
            second = Assert.IsType<UserArtifactContent.ArtifactIds>(
                await closer.CloseTextAsync(
                    tenant, "artifact.txt", fileLabel, content, modifiedUtc: secondModified));
        Assert.Equal(first.FileId, second.FileId);
        Assert.Equal(firstEvidence, await EvidenceStateAsync(first.SourceId));

        await using var command = _pg.DataSource.CreateCommand("""
            WITH matching_runs AS (
                SELECT run_id, status
                FROM laplace.ingest_run_journal
                WHERE source_name = @source
            )
            SELECT f.bytes, f.modified_at, f.file_id, f.status, r.status
            FROM matching_runs r
            CROSS JOIN LATERAL ops.ingest_files(r.run_id, 10) f
            WHERE f.file_label = @label
            ORDER BY f.modified_at
            """);
        command.Parameters.Add("source", NpgsqlDbType.Text).Value =
            UserArtifactContent.Resolve(tenant).SourceName;
        command.Parameters.Add("label", NpgsqlDbType.Text).Value = fileLabel;
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(content.LongLength, reader.GetInt64(0));
        Assert.Equal(firstModified, reader.GetDateTime(1));
        Assert.Equal(first.FileId.ToBytes(), reader.GetFieldValue<byte[]>(2));
        Assert.Equal("ok", reader.GetString(3));
        Assert.Equal("ok", reader.GetString(4));

        Assert.True(await reader.ReadAsync());
        Assert.Equal(content.LongLength, reader.GetInt64(0));
        Assert.Equal(secondModified, reader.GetDateTime(1));
        Assert.Equal(second.FileId.ToBytes(), reader.GetFieldValue<byte[]>(2));
        Assert.Equal("ok", reader.GetString(3));
        Assert.Equal("ok", reader.GetString(4));
        Assert.False(await reader.ReadAsync());
    }

    private async Task<(long Observations, long ConsensusWitnesses, long RatingSum, long RdSum)>
        EvidenceStateAsync(Hash128 sourceId)
    {
        await using var command = _pg.DataSource.CreateCommand("""
            SELECT coalesce(sum(a.observation_count), 0)::bigint,
                   coalesce(sum(c.witness_count), 0)::bigint,
                   coalesce(sum(c.rating), 0)::bigint,
                   coalesce(sum(c.rd), 0)::bigint
            FROM laplace.attestations a
            LEFT JOIN laplace.consensus c
              ON c.subject_id = a.subject_id
             AND c.type_id = a.type_id
             AND c.object_id IS NOT DISTINCT FROM a.object_id
            WHERE a.source_id = $1
            """);
        command.Parameters.AddWithValue(NpgsqlDbType.Bytea, sourceId.ToBytes());
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    [Fact]
    public async Task IngestLifecycle_PersistsSuppliedModificationTimeAndKeepsMissingTimeNull()
    {
        string sourceName = $"artifact-lifecycle-{Guid.NewGuid():N}";
        const string observedLabel = "observed.txt";
        const string unknownLabel = "unknown.txt";
        DateTimeOffset modifiedAt = new(2025, 3, 4, 5, 6, 7, TimeSpan.Zero);
        Hash128 observedId = Hash128.OfCanonical($"test/file/{sourceName}/observed");
        Hash128 unknownId = Hash128.OfCanonical($"test/file/{sourceName}/unknown");
        var observability = new NpgsqlIngestObservability(_pg.DataSource);

        observability.OnRunStart(sourceName, layerOrder: 0, inventory: null);
        observability.OnFileStarted(sourceName, observedLabel, bytes: 17, modifiedAt);
        observability.OnFileComposed(sourceName, observedLabel, observedId, records: 1);
        observability.OnFileFinished(sourceName, observedLabel, "ok");
        observability.OnFileStarted(sourceName, unknownLabel, bytes: 23, modifiedAt: null);
        observability.OnFileComposed(sourceName, unknownLabel, unknownId, records: 1);
        observability.OnFileFinished(sourceName, unknownLabel, "ok");
        observability.OnRunFinished(
            sourceName,
            new IngestRunResult(
                SourceId: Hash128.OfCanonical($"test/source/{sourceName}"),
                SourceName: sourceName,
                UnitsAttempted: 2,
                UnitsApplied: 2,
                UnitsFailed: 0,
                EntitiesInserted: 0,
                PhysicalitiesInserted: 0,
                AttestationsInserted: 0,
                TotalRoundTrips: 1,
                WallClock: TimeSpan.FromMilliseconds(1),
                Failures: Array.Empty<IngestFailure>(),
                FilesDone: 2,
                InputUnitsDone: 2,
                InputUnitsTotal: 2),
            status: "ok");

        await using var command = _pg.DataSource.CreateCommand("""
            SELECT file_label, bytes, modified_at, file_id, status
            FROM laplace.ingest_file_journal
            WHERE source_name = @source
            ORDER BY file_label
            """);
        command.Parameters.Add("source", NpgsqlDbType.Text).Value = sourceName;
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(observedLabel, reader.GetString(0));
        Assert.Equal(17, reader.GetInt64(1));
        Assert.Equal(modifiedAt, reader.GetFieldValue<DateTimeOffset>(2));
        Assert.Equal(observedId.ToBytes(), reader.GetFieldValue<byte[]>(3));
        Assert.Equal("ok", reader.GetString(4));

        Assert.True(await reader.ReadAsync());
        Assert.Equal(unknownLabel, reader.GetString(0));
        Assert.Equal(23, reader.GetInt64(1));
        Assert.True(reader.IsDBNull(2));
        Assert.Equal(unknownId.ToBytes(), reader.GetFieldValue<byte[]>(3));
        Assert.Equal("ok", reader.GetString(4));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task DocumentInventory_PersistsEveryPhysicalDispositionBesideSelectedLifecycle()
    {
        string root = Path.Combine(Path.GetTempPath(), $"laplace-artifact-journal-{Guid.NewGuid():N}");
        string sourceName = $"document-inventory-{Guid.NewGuid():N}";
        try
        {
            Write(root, "kept.txt", "selected");
            File.SetLastWriteTimeUtc(
                Path.Combine(root, "kept.txt"),
                new DateTime(2025, 5, 6, 7, 8, 9, DateTimeKind.Utc));
            Write(root, "opaque.bin", "unsupported");
            Write(root, Path.Combine("node_modules", "dependency.txt"), "excluded");
            IngestArtifactGraph graph = Assert.IsType<IngestArtifactGraph>(
                DocumentDecomposer.BuildArtifactGraph(root));
            IngestArtifact admitted = Assert.Single(graph.Selected);
            IngestInventory inventory = Assert.IsType<IngestInventory>(
                graph.ToFileInventory("documents"));
            var observability = new NpgsqlIngestObservability(_pg.DataSource);

            observability.OnRunStart(sourceName, layerOrder: 2, inventory, graph);
            foreach (IngestArtifact artifact in graph.Selected)
            {
                Hash128 fileId = Hash128.OfCanonical($"test/inventory/{sourceName}/{artifact.RelativePath}");
                // The shared multi-file pipeline uses the compatibility callback. Its
                // lifecycle transition must retain the mtime captured by the inventory snapshot.
                observability.OnFileStarted(sourceName, artifact.FileLabel, artifact.Bytes ?? 0);
                observability.OnFileComposed(sourceName, artifact.FileLabel, fileId, records: 1);
                observability.OnFileFinished(sourceName, artifact.FileLabel, "ok");
            }
            observability.OnRunFinished(
                sourceName,
                new IngestRunResult(
                    SourceId: Hash128.OfCanonical($"test/source/{sourceName}"),
                    SourceName: sourceName,
                    UnitsAttempted: 1,
                    UnitsApplied: 1,
                    UnitsFailed: 0,
                    EntitiesInserted: 1,
                    PhysicalitiesInserted: 1,
                    AttestationsInserted: 0,
                    TotalRoundTrips: 1,
                    WallClock: TimeSpan.FromMilliseconds(1),
                    Failures: Array.Empty<IngestFailure>(),
                    FilesDone: 1,
                    InputUnitsDone: 1,
                    InputUnitsTotal: 1),
                status: "ok");

            await using var command = _pg.DataSource.CreateCommand("""
                SELECT f.file_label, f.artifact_id, f.relative_path, f.disposition,
                       f.disposition_reason, f.status, f.bytes, f.modified_at
                FROM laplace.ingest_run_journal r
                CROSS JOIN LATERAL ops.ingest_files(r.run_id, 20) f
                WHERE r.source_name = @source
                ORDER BY f.file_label
                """);
            command.Parameters.Add("source", NpgsqlDbType.Text).Value = sourceName;
            await using var reader = await command.ExecuteReaderAsync();

            var rows = new List<(
                string Label,
                string ArtifactId,
                string Path,
                string Disposition,
                string? Reason,
                string Status,
                long Bytes,
                DateTimeOffset? ModifiedAt)>();
            while (await reader.ReadAsync())
            {
                rows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7)));
            }

            Assert.Equal(3, rows.Count);
            Assert.Contains(rows, row =>
                row.Label == "document/kept.txt"
                && row.ArtifactId == $"{DocumentSource.SourceName}/local/kept.txt"
                && row.Path == "kept.txt"
                && row.Disposition == "admitted"
                && row.Reason is null
                && row.Status == "ok"
                && row.Bytes == 8
                && row.ModifiedAt == admitted.ModifiedAt);
            Assert.Contains(rows, static row =>
                row.Label == "document/opaque.bin"
                && row.Disposition == "unsupported-with-why-not"
                && row.Status == "not-selected"
                && !string.IsNullOrWhiteSpace(row.Reason));
            Assert.Contains(rows, static row =>
                row.Label == "document/node_modules/dependency.txt"
                && row.Disposition == "excluded-with-reason"
                && row.Status == "not-selected"
                && !string.IsNullOrWhiteSpace(row.Reason));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private async Task ApplySqlFileAsync(string sqlFile)
    {
        var start = new ProcessStartInfo
        {
            FileName = ResolvePsql(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-X");
        start.ArgumentList.Add("--host");
        start.ArgumentList.Add(LocalPgFixture.PgHost);
        start.ArgumentList.Add("--username");
        start.ArgumentList.Add(LocalPgFixture.PgUser);
        start.ArgumentList.Add("--dbname");
        start.ArgumentList.Add(LocalPgFixture.DatabaseName);
        start.ArgumentList.Add("--set");
        start.ArgumentList.Add("ON_ERROR_STOP=1");
        start.ArgumentList.Add("--command");
        start.ArgumentList.Add("SET search_path TO laplace, public");
        start.ArgumentList.Add("--file");
        start.ArgumentList.Add(sqlFile);
        if (LocalPgFixture.PgPassword is not null)
            start.Environment["PGPASSWORD"] = LocalPgFixture.PgPassword;

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("failed to start psql");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"psql journal setup exited {process.ExitCode}: {stderr.Result}\n{stdout.Result}");
    }

    private static string ResolvePsql()
    {
        if (!OperatingSystem.IsWindows()) return "psql";
        const string installed = @"C:\Program Files\PostgreSQL\18\bin\psql.exe";
        return File.Exists(installed) ? installed : "psql";
    }

    private static void Write(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
