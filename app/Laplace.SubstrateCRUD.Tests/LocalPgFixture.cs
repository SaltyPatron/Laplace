using System.Diagnostics;
using global::Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>
/// xUnit shared fixture that provisions a dedicated PG database against
/// the LIVE local PostgreSQL (per STANDARDS.md Testing — Testcontainers
/// only covers DbUp/postgis-image layer; substrate-level integration
/// tests with the laplace extensions run against the deployed cluster
/// because the laplace extensions aren't in the postgis Docker image).
///
/// On <see cref="InitializeAsync"/> drops + recreates the test DB via
/// <c>dropdb</c>/<c>createdb</c> (matching the pg_regress fixture pattern
/// at extension/laplace_substrate/tests/CMakeLists.txt), then issues
/// CREATE EXTENSION for postgis, laplace_geom, laplace_substrate. Tests
/// receive a fresh <see cref="NpgsqlDataSource"/> via
/// <see cref="DataSource"/>.
///
/// On <see cref="DisposeAsync"/> drops the test DB.
/// </summary>
public sealed class LocalPgFixture : IAsyncLifetime
{
    public const string DatabaseName = "laplace_substratecrud_test";
    public const string PgUser = "laplace_admin";

    private NpgsqlDataSource? _ds;

    public NpgsqlDataSource DataSource =>
        _ds ?? throw new InvalidOperationException("Fixture not initialized");

    public string ConnectionString =>
        $"Host=/var/run/postgresql;Username={PgUser};Database={DatabaseName}";

    public async Task InitializeAsync()
    {
        // dropdb + createdb (idempotent re-run; cannot run in a transaction).
        await RunPsqlAdminAsync("dropdb", $"-U {PgUser} --if-exists {DatabaseName}");
        await RunPsqlAdminAsync("createdb", $"-U {PgUser} -O {PgUser} {DatabaseName}");

        var dsb = new NpgsqlDataSourceBuilder(ConnectionString);
        _ds = dsb.Build();

        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE EXTENSION IF NOT EXISTS postgis;
            CREATE EXTENSION IF NOT EXISTS laplace_geom;
            CREATE EXTENSION IF NOT EXISTS laplace_substrate;
            SET search_path TO laplace, public;
        ";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_ds is not null)
        {
            await _ds.DisposeAsync();
            _ds = null;
        }
        await RunPsqlAdminAsync("dropdb", $"-U {PgUser} --if-exists {DatabaseName}");
    }

    private static async Task RunPsqlAdminAsync(string program, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = program,
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            var stderr = await p.StandardError.ReadToEndAsync();
            throw new InvalidOperationException(
                $"{program} {args} exited {p.ExitCode}: {stderr}");
        }
    }
}
