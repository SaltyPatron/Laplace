using System.Text.RegularExpressions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Laplace.SubstrateCRUD;
using Npgsql;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>Parse and resolve fixed shipped statements against an installed schema without executing them.</summary>
[Trait("Category", "RequiresDatabase")]
[Trait("Tier", "db")]
public sealed class NativeSqlCatalogPreparationTests : IAsyncLifetime
{
    private LocalPgFixture? _fixture;
    private NpgsqlDataSource _source = null!;

    [Fact]
    public async Task BrokenBackendLogsRollbackFailureAndPreservesThePrimaryDatabaseException()
    {
        await using var connection = await _source.OpenConnectionAsync();
        var log = new CleanupLog();
        var failure = await Assert.ThrowsAnyAsync<NpgsqlException>(() => AdvisoryTxLock.BeginWithLockAsync(
            connection, "laplace_apply_batch", PostgresWriteDurability.Synchronous, log,
            CancellationToken.None, async (transaction, _, token) =>
            {
                await using var command = new NpgsqlCommand("SELECT pg_terminate_backend(pg_backend_pid())", connection, transaction);
                await command.ExecuteNonQueryAsync(token);
            }));
        Assert.NotNull(failure);
        Assert.Contains(log.Errors, x => x.Exception is not null
            && x.Message.Contains("rollback failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectedTransactionSettingRollsBackAndLeavesTheConnectionReusable()
    {
        await using var connection = await _source.OpenConnectionAsync();
        var log = new CleanupLog();
        var failure = await Assert.ThrowsAsync<PostgresException>(() => AdvisoryTxLock.BeginWithLockAsync(
            connection, "laplace_apply_batch", PostgresWriteDurability.Synchronous, log,
            CancellationToken.None, async (transaction, _, token) =>
            {
                await using var command = new NpgsqlCommand("SET LOCAL work_mem='invalid-setting'", connection, transaction);
                await command.ExecuteNonQueryAsync(token);
            }));
        Assert.Equal(PostgresErrorCodes.InvalidParameterValue, failure.SqlState);
        Assert.Empty(log.Errors);
        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT 1";
        Assert.Equal(1, await probe.ExecuteScalarAsync());
    }

    private sealed class CleanupLog : ILogger
    {
        public List<(Exception? Exception, string Message)> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Errors.Add((exception, formatter(state, exception)));
    }

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
                // Identifier-bearing operation and maintenance templates have their
                // own lifecycle checks; they cannot be prepared without expansion.
                if (Regex.IsMatch(contract.Text, @"\{[a-z_][a-z0-9_]*\}", RegexOptions.IgnoreCase))
                {
                    if (name.StartsWith("migration.", StringComparison.Ordinal))
                    {
                        // Parse utility syntax without executing lifecycle operations.
                        // Confirmed recreation remains a separate operator action.
                        using var identifiers = new NpgsqlCommandBuilder();
                        string statement = Regex.Replace(contract.Text, @"\{([a-z_]+)\}", match =>
                            match.Groups[1].Value switch
                            {
                                "database" => identifiers.QuoteIdentifier(connection.Database),
                                "replacement" => identifiers.QuoteIdentifier("laplace_catalog_prepare"),
                                "options" => "TEMPLATE template0",
                                "owner" => "CURRENT_USER",
                                _ => throw new InvalidOperationException($"Unknown bootstrap DDL slot in {name}."),
                            });
                        await using var utility = new NpgsqlCommand(statement, connection);
                        await utility.PrepareAsync();
                        await utility.UnprepareAsync();
                        continue;
                    }
                    Assert.True(name.StartsWith("maintenance.vacuum_", StringComparison.Ordinal)
                        || name.StartsWith("operation.", StringComparison.Ordinal)
                        // StagedSourceWriterTests executes these exact CREATE,
                        // COPY and DROP templates in a caller-rolled-back transaction.
                        || name is "write.canonical_stage_create" or "write.canonical_stage_drop"
                            or "write.canonical_copy_entities" or "write.canonical_copy_paths",
                        $"No lifecycle verification owns template {name}.");
                    templates.Add(name);
                    continue;
                }
                await using var command = NpgsqlCatalog.Command(connection, null, name,
                    new object?[contract.ParameterTypes.Length]);
                // The native router quotes the physical owner; policy/ACL fallback
                // uses the parent. Prepare that real fallback statement here.
                if (name == "consensus.evidence_rows_batch")
                    command.CommandText = command.CommandText.Replace("%s", "laplace.attestations", StringComparison.Ordinal);
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
            $"({templates.Count} identifier templates require their lifecycle tests):\n" + string.Join("\n", failures));
    }

    [Fact]
    public async Task CatalogVacuumExecutesEveryModeOnOneQuotedRelationOutsideATransaction()
    {
        var table = "catalog vacuum " + Guid.NewGuid().ToString("N");
        using var identifiers = new NpgsqlCommandBuilder();
        var relation = "public." + identifiers.QuoteIdentifier(table);
        await using var connection = await _source.OpenConnectionAsync();
        await using (var create = new NpgsqlCommand($"CREATE TABLE {relation}(n integer); INSERT INTO {relation} VALUES (1),(2)", connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            foreach (var (full, analyze, expected) in new[]
            {
                (false, false, "VACUUM "),
                (false, true, "VACUUM (ANALYZE) "),
                (true, false, "VACUUM (FULL, ANALYZE) "),
            })
            {
                string executed = await NpgsqlMaintenance.VacuumAsync(_source, relation, full, analyze, 30);
                Assert.Equal(expected + relation, executed);
                await using var count = new NpgsqlCommand($"SELECT count(*) FROM {relation}", connection);
                Assert.Equal(2L, await count.ExecuteScalarAsync());
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP TABLE {relation}", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task CatalogOperationTemplateExecutesNamedTypedArgumentsAgainstPostgres()
    {
        // The real PostgreSQL operation supplies the names/types. Only the command
        // rendering is under test here; this does not bypass ops.api in InvokeAsync.
        await using var metadata = _source.CreateCommand(
            "SELECT pg_get_function_arguments('pg_catalog.make_interval(integer,integer,integer,integer,integer,integer,double precision)'::regprocedure)");
        var signature = InstalledOpInvoker.ParseSignature((string)(await metadata.ExecuteScalarAsync())!);
        var days = signature.Single(parameter => parameter.Name == "days");
        var seconds = signature.Single(parameter => parameter.Name == "secs");
        var operation = InstalledOpInvoker.QualifiedCatalogName("pg_catalog.make_interval");
        var arguments = new (string Name, string Type, NpgsqlParameter Parameter)[]
        {
            (days.Name, days.Type, InstalledOpInvoker.BindArg("a0", "2")),
            (seconds.Name, seconds.Type, InstalledOpInvoker.BindArg("a1", "1.5")),
        };
        await using var command = NpgsqlCatalog.InstalledOperationCommand(_source, false, operation, arguments, 2);
        Assert.Equal(TimeSpan.FromDays(2) + TimeSpan.FromSeconds(1.5), await command.ExecuteScalarAsync());
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
