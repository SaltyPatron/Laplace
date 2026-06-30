using System.Diagnostics;
using global::Npgsql;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class LocalPgFixture : IAsyncLifetime
{
    private static readonly SemaphoreSlim InitGate = new(1, 1);
    private static int _refCount;

    public const string DatabaseName = "laplace_substratecrud_test";

    public static readonly string PgHost =
        Environment.GetEnvironmentVariable("LAPLACE_TEST_PGHOST")
        ?? (OperatingSystem.IsWindows() ? "localhost" : "/var/run/postgresql");

    public static readonly string PgUser =
        Environment.GetEnvironmentVariable("LAPLACE_TEST_PGUSER")
        ?? (OperatingSystem.IsWindows() ? "postgres" : "laplace_admin");

    public static readonly string? PgPassword =
        Environment.GetEnvironmentVariable("LAPLACE_TEST_PGPASSWORD")
        ?? (OperatingSystem.IsWindows() ? "postgres" : null);

    private NpgsqlDataSource? _ds;

    public NpgsqlDataSource DataSource =>
        _ds ?? throw new InvalidOperationException("Fixture not initialized");

    public string ConnectionString =>
        $"Host={PgHost};Username={PgUser};Database={DatabaseName}"
        + (PgPassword is null ? "" : $";Password={PgPassword}");

    public async Task InitializeAsync()
    {
        IntentStage.SetBulkFreshBypass(false);
        await InitGate.WaitAsync();
        try
        {
            if (_refCount++ == 0)
            {
                await RunPsqlAdminAsync("dropdb", $"-h {PgHost} -U {PgUser} --force --if-exists {DatabaseName}");
                await RunPsqlAdminAsync("createdb", $"-h {PgHost} -U {PgUser} -O {PgUser} {DatabaseName}");
            }

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
        finally
        {
            InitGate.Release();
        }
    }

    public async Task DisposeAsync()
    {
        await InitGate.WaitAsync();
        try
        {
            if (_ds is not null)
            {
                await _ds.DisposeAsync();
                _ds = null;
            }
            if (--_refCount == 0)
                await RunPsqlAdminAsync("dropdb", $"-h {PgHost} -U {PgUser} --force --if-exists {DatabaseName}");
        }
        finally
        {
            InitGate.Release();
        }
    }

    private static async Task RunPsqlAdminAsync(string program, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolvePgTool(program),
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        if (PgPassword is not null) psi.Environment["PGPASSWORD"] = PgPassword;
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            var stderr = await p.StandardError.ReadToEndAsync();
            throw new InvalidOperationException(
                $"{program} {args} exited {p.ExitCode}: {stderr}");
        }
    }

    private static string ResolvePgTool(string program)
    {
        if (!OperatingSystem.IsWindows()) return program;
        string exe = Path.Combine(
            Environment.GetEnvironmentVariable("PGBIN") ?? @"C:\Program Files\PostgreSQL\18\bin",
            program + ".exe");
        return File.Exists(exe) ? exe : program;
    }
}
