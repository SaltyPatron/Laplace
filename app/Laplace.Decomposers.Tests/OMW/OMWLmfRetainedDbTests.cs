using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.OMW;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Laplace.Decomposers.Tests.OMW;

[Trait("Tier", "db")]
public sealed class OMWLmfRetainedDbTests
{
    private const string LexiconRoot =
        "/vault/Data/.refresh-20260903/OMW-2.0/extracted/omw-2.0/omw-it";
    private const long XmlRecords = 78_007;
    private const long TotalRecords = XmlRecords + 3;

    [SkippableFact]
    public async Task Selected_Italian_Lexicon_Reconciles_Artifacts_And_Native_Graph()
    {
        string? connectionString = Environment.GetEnvironmentVariable("LAPLACE_OMW_RETAINED_DB");
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set LAPLACE_OMW_RETAINED_DB to an explicitly retained isolated database.");
        Skip.IfNot(Directory.Exists(LexiconRoot), "complete OMW 2.0 Italian lexicon is not mounted");

        CodepointPerfcache.LoadDefault();
        LanguageReference.EnsureLoaded(TestIngestPaths.Iso639);

        var graph = Assert.IsType<IngestArtifactGraph>(
            OMWLmfArtifacts.Build(LexiconRoot, DecomposerOptions.Default));
        Assert.Equal(4, graph.Artifacts.Count);
        Assert.Equal(4, graph.Selected.Count);
        Assert.Single(graph.Selected, artifact => artifact.MediaType == "application/xml");
        Assert.Contains(graph.Selected, artifact => artifact.RelativePath == "LICENSE");
        Assert.Contains(graph.Selected, artifact => artifact.RelativePath == "citation.bib");
        Assert.Contains(graph.Selected, artifact => artifact.RelativePath == "README");
        Assert.DoesNotContain(graph.Artifacts, artifact =>
            artifact.RelativePath.Contains("omw-en", StringComparison.Ordinal));

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.ConnectionStringBuilder.CommandTimeout = 0;
        await using var dataSource = dataSourceBuilder.Build();
        Guid? runId = await ScalarNullableAsync<Guid>(dataSource,
            "SELECT run_id FROM laplace.ingest_run_journal "
            + "WHERE source_name = 'OMWDecomposer' AND status = 'ok' "
            + "AND files_done = 4 AND input_units_done = $1 "
            + "ORDER BY started_at DESC LIMIT 1", TotalRecords);
        if (runId is null)
        {
            var inner = new NpgsqlSubstrateWriter(dataSource);
            await using var writer = new ConsensusAccumulatingWriter(inner, dataSource);
            var observer = new NpgsqlIngestObservability(dataSource, evidencePersisted: true);
            var runner = new IngestRunner(
                writer, new NpgsqlSubstrateReader(dataSource), NullLoggerFactory.Instance, observer);

            var result = await runner.RunAsync(new OMWDecomposer(), IngestRunOptions.Default with
            {
                EcosystemPath = LexiconRoot,
                SkipLayerOrderingCheck = true,
                BatchSize = 256,
                CommitRows = 65_536,
                DecomposerOptions = DecomposerOptions.Default with { BatchSize = 256 },
            });

            Assert.Empty(result.Failures);
            Assert.Equal(0, result.UnitsFailed);
            Assert.Equal(4, result.FilesDone);
            Assert.Equal(TotalRecords, result.InputUnitsDone);
            Assert.Equal(TotalRecords, result.InputUnitsTotal);
            runId = await ScalarAsync<Guid>(dataSource,
                "SELECT run_id FROM laplace.ingest_run_journal "
                + "WHERE source_name = 'OMWDecomposer' ORDER BY started_at DESC LIMIT 1");
        }

        RunRow run = await ReadRunAsync(dataSource, runId.Value);
        Assert.Equal("ok", run.Status);
        Assert.Equal(4, run.FilesDone);
        Assert.Equal(TotalRecords, run.InputUnitsDone);
        Assert.Equal(TotalRecords, run.InputUnitsTotal);
        var journal = await JournalAsync(dataSource, runId.Value);
        Assert.Equal(4, journal.Count);
        Assert.All(journal, row =>
        {
            Assert.Equal("admitted", row.Disposition);
            Assert.Equal("ok", row.Status);
        });
        Assert.Equal(TotalRecords, journal.Sum(static row => row.Records));
        Assert.Equal(15_009_987, journal.Sum(static row => row.Bytes));

        await AssertEvidenceCountAsync(dataSource, OmwRelation.HasSense, 62_125);
        await AssertEvidenceCountAsync(dataSource, OmwRelation.IsSenseOf, 62_125);
        await AssertEvidenceCountAsync(dataSource, OmwRelation.HasMember, 62_125);
        await AssertEvidenceCountAsync(dataSource, OmwRelation.CorrespondsTo, 35_001);
        await AssertEvidenceCountAsync(dataSource, OmwRelation.HasDefinition, 2_169);
        await AssertEvidenceCountAsync(
            dataSource, OmwRelation.HasExample, expectedCells: 1_953, expectedObservations: 1_955);
        await AssertEvidenceCountAsync(dataSource, OmwRelation.Requires, 1);

        const string entryRaw = "omw-it-tenda_per_doccia-n";
        const string senseRaw = "omw-it-tenda_per_doccia-04209239-n";
        const string synsetRaw = "omw-it-04209239-n";
        const string definition = "le tende che impediscono all'acqua di uscire dall'area della doccia";
        Hash128 entry = OMWLmfEmitter.Identity("entry", "omw-it", entryRaw);
        Hash128 sense = OMWLmfEmitter.Identity("sense", "omw-it", senseRaw);
        Hash128 synset = OMWLmfEmitter.Identity("synset", "omw-it", synsetRaw);
        Hash128 ili = ReferenceAnchor.Id(ReferenceIdentityKind.CiliIli, "i58874")!.Value;
        Hash128 lemma = ContentEmitter.RootId("tenda per doccia")!.Value;
        Hash128 definitionId = ContentEmitter.RootId(definition)!.Value;
        Hash128 lexicalized = ContentEmitter.RootId("false")!.Value;

        await AssertEdgeAsync(dataSource, entry, OmwRelation.HasNameAlias, lemma);
        await AssertEdgeAsync(dataSource, entry, OmwRelation.HasSense, sense);
        await AssertEdgeAsync(dataSource, sense, OmwRelation.IsSenseOf, synset);
        await AssertEdgeAsync(dataSource, synset, OmwRelation.HasMember, sense);
        await AssertUnorderedEdgeAsync(dataSource, synset, OmwRelation.CorrespondsTo, ili);
        await AssertEdgeAsync(dataSource, synset, OmwRelation.HasDefinition, definitionId);
        await AssertEdgeAsync(dataSource, sense, OmwRelation.HasFeature, lexicalized);

        Hash128 italian = OMWLmfEmitter.Identity("lexicon", "omw-it", "omw-it");
        Hash128 english = OMWLmfEmitter.Identity("lexicon", "omw-en", "omw-en");
        await AssertEdgeAsync(dataSource, italian, OmwRelation.Requires, english);

        string proofDirectory = "/tmp/laplace-content-recovery-proof";
        Directory.CreateDirectory(proofDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(proofDirectory, "omw2-retained-db.json"),
            JsonSerializer.Serialize(new
            {
                Database = new NpgsqlConnectionStringBuilder(connectionString).Database,
                Lexicon = "omw-it",
                SelectedScope = "one complete omw-it physical lexicon directory",
                DependencyClosureDelivered = false,
                RunId = runId.Value,
                SelectedPhysicalArtifacts = graph.Selected.Count,
                SelectedBytes = graph.Selected.Sum(static artifact => artifact.Bytes ?? 0),
                XmlRecords,
                SidecarRecords = 3,
                TotalRecords = run.InputUnitsDone,
                LexicalEntries = 43_004,
                Senses = 62_125,
                Synsets = 35_001,
                SenseMembers = 62_125,
                Definitions = 2_169,
                Examples = 1_955,
                ExampleEvidenceCells = 1_953,
                SenseLexicalizedFields = 810,
                DeclaredRequiredLexicons = new[]
                {
                    new
                    {
                        Lexicon = "omw-en",
                        PresentInSelectedPhysicalEstate = false,
                        ReferenceTargetIdentityDeclared = true,
                        CompleteExternalLexiconIngested = false,
                        Disposition = "unresolved-external-reference",
                    },
                },
                UnresolvedExternalRequiredLexicons = new[] { "omw-en" },
                RepresentativeTraversal = new
                {
                    Lemma = "tenda per doccia",
                    Entry = Hex(entry),
                    Sense = Hex(sense),
                    Synset = Hex(synset),
                    Ili = "i58874",
                    Definition = definition,
                },
                Result = new
                {
                    run.UnitsApplied,
                    run.Entities,
                    run.Physicalities,
                    run.Attestations,
                    run.ElapsedMilliseconds,
                },
                Journal = journal,
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task AssertEvidenceCountAsync(
        NpgsqlDataSource dataSource,
        OmwRelation relation,
        long expectedCells,
        long? expectedObservations = null)
    {
        long cells = await ScalarAsync<long>(dataSource,
            "SELECT count(*) FROM laplace.attestations WHERE source_id = $1 AND type_id = $2",
            OMWDecomposer.Source.ToBytes(), OMWSource.Resolve(relation).Id.ToBytes());
        Assert.Equal(expectedCells, cells);
        if (expectedObservations is { } observations)
        {
            long actual = await ScalarAsync<long>(dataSource,
                "SELECT COALESCE(sum(observation_count), 0)::bigint FROM laplace.attestations "
                + "WHERE source_id = $1 AND type_id = $2",
                OMWDecomposer.Source.ToBytes(), OMWSource.Resolve(relation).Id.ToBytes());
            Assert.Equal(observations, actual);
        }
    }

    private static async Task<RunRow> ReadRunAsync(NpgsqlDataSource dataSource, Guid runId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT status, files_done, input_units_done, input_units_total, units_applied, "
            + "entities, physicalities, attestations, throughput_elapsed_ms "
            + "FROM laplace.ingest_run_journal WHERE run_id = $1");
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, runId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new RunRow(
            reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7),
            reader.GetInt64(8));
    }

    private static Task AssertEdgeAsync(
        NpgsqlDataSource dataSource, Hash128 subject, OmwRelation relation, Hash128 obj) =>
        AssertEdgeCoreAsync(dataSource, subject, relation, obj, unordered: false);

    private static Task AssertUnorderedEdgeAsync(
        NpgsqlDataSource dataSource, Hash128 left, OmwRelation relation, Hash128 right) =>
        AssertEdgeCoreAsync(dataSource, left, relation, right, unordered: true);

    private static async Task AssertEdgeCoreAsync(
        NpgsqlDataSource dataSource,
        Hash128 left,
        OmwRelation relation,
        Hash128 right,
        bool unordered)
    {
        string predicate = unordered
            ? "((subject_id = $3 AND object_id = $4) OR (subject_id = $4 AND object_id = $3))"
            : "subject_id = $3 AND object_id = $4";
        bool exists = await ScalarAsync<bool>(dataSource,
            "SELECT EXISTS (SELECT 1 FROM laplace.attestations "
            + "WHERE source_id = $1 AND type_id = $2 AND " + predicate + ")",
            OMWDecomposer.Source.ToBytes(), OMWSource.Resolve(relation).Id.ToBytes(),
            left.ToBytes(), right.ToBytes());
        Assert.True(exists, $"missing {relation} edge {left} -> {right}");
    }

    private static async Task<List<JournalRow>> JournalAsync(
        NpgsqlDataSource dataSource, Guid runId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT relative_path, disposition, status, bytes, records "
            + "FROM laplace.ingest_file_journal WHERE run_id = $1 ORDER BY relative_path");
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, runId);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<JournalRow>();
        while (await reader.ReadAsync())
            rows.Add(new JournalRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetInt64(4)));
        return rows;
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlDataSource dataSource, string sql, params object[] parameters)
    {
        await using var command = dataSource.CreateCommand(sql);
        for (int i = 0; i < parameters.Length; i++)
        {
            var value = parameters[i];
            command.Parameters.AddWithValue(value is byte[] ? NpgsqlDbType.Bytea : NpgsqlDbType.Unknown, value);
        }
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<T?> ScalarNullableAsync<T>(
        NpgsqlDataSource dataSource, string sql, params object[] parameters) where T : struct
    {
        await using var command = dataSource.CreateCommand(sql);
        for (int i = 0; i < parameters.Length; i++)
            command.Parameters.AddWithValue(NpgsqlDbType.Bigint, parameters[i]);
        object? value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : (T)value;
    }

    private static string Hex(Hash128 id) => Convert.ToHexString(id.ToBytes()).ToLowerInvariant();

    private sealed record JournalRow(
        string RelativePath, string Disposition, string Status, long Bytes, long Records);

    private sealed record RunRow(
        string Status,
        long FilesDone,
        long InputUnitsDone,
        long InputUnitsTotal,
        long UnitsApplied,
        long Entities,
        long Physicalities,
        long Attestations,
        long ElapsedMilliseconds);
}
