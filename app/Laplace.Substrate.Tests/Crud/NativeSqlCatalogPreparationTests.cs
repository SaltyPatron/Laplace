using System.Text.RegularExpressions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>Parse and resolve fixed shipped statements against an installed schema without executing them.</summary>
[Trait("Tier", "db")]
public sealed class NativeSqlCatalogPreparationTests : IAsyncLifetime
{
    private LocalPgFixture? _fixture;
    private NpgsqlDataSource _source = null!;

    public async Task InitializeAsync()
    {
        var selected = Environment.GetEnvironmentVariable("LAPLACE_SQL_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(selected))
        {
            _source = NpgsqlDataSource.Create(selected);
            return;
        }
        // Normal managed-db CI runs this check too. Complete the disposable
        // fixture with the canonical migrations for both catalog domains.
        _fixture = new LocalPgFixture();
        await _fixture.InitializeAsync();
        _source = _fixture.DataSource;
        foreach (var name in new[] { "20260606000000_layer1_database.sql", "20260611000000_app_billing.sql",
                     "20261015000000_substrate_runtime_grants.sql" })
        {
            var migration = Path.Combine(Laplace.Decomposers.Abstractions.Tests.TypeIdLawTests.FindRepoRootPublic(),
                "db", "migrations", name);
            await using var command = _source.CreateCommand(await File.ReadAllTextAsync(migration));
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (_fixture is not null) await _fixture.DisposeAsync();
        else if (_source is not null) await _source.DisposeAsync();
    }

    [Fact]
    public async Task EveryFixedStatementPreparesAgainstTheInstalledSchema()
    {
        await using var connection = await _source.OpenConnectionAsync();
        using var resource = typeof(SqlCatalog).Assembly.GetManifestResourceStream("Laplace.SqlCatalog.def")!;
        using var reader = new StreamReader(resource);
        var catalog = await reader.ReadToEndAsync();
        var names = Regex.Matches(catalog, "SQL_QUERY\\(\\s*\"(?<name>[^\"]+)\"")
            .Select(match => match.Groups["name"].Value).ToArray();
        Assert.NotEmpty(names);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        var failures = new List<string>();
        var templates = new List<string>();
        foreach (var name in names)
        {
            try
            {
                var contract = SqlCatalog.Get(name);
                // StagedSourceWriter expands its identifier templates and runs their
                // dependent DDL/COPY lifecycle in StagedSourceWriterTests. They are
                // not executable fixed statements and cannot be prepared raw.
                if (contract.Text.Contains('{'))
                {
                    Assert.StartsWith("stage.", name);
                    templates.Add(name);
                    continue;
                }
                await using var command = NpgsqlCatalog.Command(connection, null, name,
                    new object?[contract.ParameterTypes.Length]);
                // fold_route.c's typed_plan specializes this relation id before SPI
                // prepares the statement; retain that native substitution contract.
                if (name == "consensus.evidence_rows_typed")
                    command.CommandText = command.CommandText.Replace("%s", new string('0', 32), StringComparison.Ordinal);
                await command.PrepareAsync();
                await command.UnprepareAsync();
            }
            catch (PostgresException error)
            {
                failures.Add($"{name}: [{error.SqlState}] {error.MessageText}");
            }
        }
        Assert.NotEmpty(templates);
        Assert.True(failures.Count == 0,
            $"{failures.Count} of {names.Length - templates.Count} fixed native SQL contracts failed preparation " +
            $"({templates.Count} staged templates require their lifecycle tests):\n" + string.Join("\n", failures));
    }

    [Fact]
    public async Task CompletionReadsPropagateDatabaseFailuresInsteadOfReprocessing()
    {
        await using var connection = await _source.OpenConnectionAsync();
        var role = "completion_read_test_" + Guid.NewGuid().ToString("N");
        await using (var create = new NpgsqlCommand($"CREATE ROLE {role}; GRANT USAGE ON SCHEMA laplace TO {role}", connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            var limited = new NpgsqlConnectionStringBuilder(_source.ConnectionString)
            {
                Options = $"-c role={role}",
                Pooling = false,
            };
            await using var deniedSource = NpgsqlDataSource.Create(limited.ConnectionString);
            var reader = new NpgsqlSubstrateReader(deniedSource);
            var id = Hash128.Blake3("completion-failure"u8);
            Func<Task>[] reads =
            [
                async () => await reader.HasSourceEverCompletedAsync(1),
                async () => await reader.HasSourceCompletedAsync(id, 1),
                async () => await reader.HasSourcesCompletedAsync([id], 1),
                async () => await reader.HasFilesCompletedAsync([id], id, 1),
            ];
            var swallowed = new List<int>();
            for (var index = 0; index < reads.Length; index++)
            {
                var error = await Record.ExceptionAsync(reads[index]);
                if (error is null) swallowed.Add(index);
                else Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, Assert.IsType<PostgresException>(error).SqlState);
            }
            Assert.Empty(swallowed);
        }
        finally
        {
            await using var remove = new NpgsqlCommand($"REVOKE USAGE ON SCHEMA laplace FROM {role}; DROP ROLE {role}", connection);
            await remove.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task CompletionReadsReturnExactWitnessLayerAndUnversionedFileMatches()
    {
        var id = Hash128.Blake3(Guid.NewGuid().ToByteArray());
        var other = Hash128.Blake3(id.ToBytes());
        var versioned = Hash128.Blake3(other.ToBytes());
        var absent = Hash128.Blake3(versioned.ToBytes());
        var reader = new NpgsqlSubstrateReader(_source);
        Assert.False(await reader.HasSourceCompletedAsync(id, 255));
        Assert.Empty(await reader.HasSourcesCompletedAsync([id, other], 255));
        Assert.Empty(await reader.HasFilesCompletedAsync([id, other], id, 255));
        try
        {
            await using var insert = _source.CreateCommand("""
                WITH layers AS (
                    INSERT INTO laplace.ingest_layer_completion(witness_id,layer) VALUES ($1,255),($2,254)
                )
                INSERT INTO laplace.ingest_unit_completion(witness_id,unit_id,layer,digest)
                VALUES ($1,$1,255,NULL),($1,$2,254,NULL),($1,$3,255,$3),($2,$4,255,NULL)
                """);
            foreach (var value in new[] { id, other, versioned, absent }) insert.Parameters.AddWithValue(value.ToBytes());
            await insert.ExecuteNonQueryAsync();
            Assert.True(await reader.HasSourceEverCompletedAsync(255));
            Assert.True(await reader.HasSourceCompletedAsync(id, 255));
            Assert.False(await reader.HasSourceCompletedAsync(other, 255));
            Assert.Equal(new[] { id }, await reader.HasSourcesCompletedAsync([id, other, absent], 255));
            Assert.Equal(new[] { id }, await reader.HasFilesCompletedAsync([id, other, versioned, absent], id, 255));
            Assert.Empty(await reader.HasSourcesCompletedAsync([], 255));
            Assert.Empty(await reader.HasFilesCompletedAsync([], id, 255));
        }
        finally
        {
            await using var remove = _source.CreateCommand("""
                WITH units AS (
                    DELETE FROM laplace.ingest_unit_completion WHERE witness_id=ANY($1)
                )
                DELETE FROM laplace.ingest_layer_completion WHERE witness_id=ANY($1)
                """);
            remove.Parameters.AddWithValue(new[] { id.ToBytes(), other.ToBytes() });
            await remove.ExecuteNonQueryAsync();
        }
    }
}
