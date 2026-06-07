using System.Diagnostics;
using global::Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

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
        await RunPsqlAdminAsync("dropdb", $"-U {PgUser} --force --if-exists {DatabaseName}");
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
        await RunPsqlAdminAsync("dropdb", $"-U {PgUser} --force --if-exists {DatabaseName}");
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
