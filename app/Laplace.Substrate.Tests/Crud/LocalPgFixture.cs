using System.Diagnostics;
using global::Npgsql;
using NpgsqlTypes;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class LocalPgFixture : IAsyncLifetime
{
    private static readonly SemaphoreSlim InitGate = new(1, 1);
    private static int _refCount;
    private static NpgsqlDataSource? _sharedDataSource;

    public const string DatabaseName = "laplace_substratecrud_test";

    private static readonly NpgsqlConnectionStringBuilder Conn =
        new(LaplaceInstall.PostgresConnectionString(DatabaseName));

    public static readonly string PgHost = Conn.Host!;
    public static readonly string PgUser = Conn.Username!;
    public static readonly string? PgPassword = Conn.Password;

    private NpgsqlDataSource? _ds;

    public NpgsqlDataSource DataSource =>
        _ds ?? throw new InvalidOperationException("Fixture not initialized");

    public string ConnectionString => Conn.ConnectionString;

    public async Task InitializeAsync()
    {
        await InitGate.WaitAsync();
        try
        {
            if (_ds is not null) return;
            if (_refCount == 0)
            {
                // New empty DB: forget any content-ladder skips from a prior fixture life.
                ContentLadderLedger.Reset();
                await RunPsqlAdminAsync("dropdb", $"-h {PgHost} -U {PgUser} --force --if-exists {DatabaseName}");
                await RunPsqlAdminAsync("createdb", $"-h {PgHost} -U {PgUser} -O {PgUser} {DatabaseName}");
                // The database is shared across fixtures; its connection pool must
                // be shared too. Independent default-sized pools retained idle
                // sessions and exhausted the server during parallel CI tests.
                var candidate = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnectionString);
                try
                {
                    await using var conn = await candidate.OpenConnectionAsync();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
            CREATE EXTENSION IF NOT EXISTS postgis;
            CREATE EXTENSION IF NOT EXISTS laplace_geom;
            CREATE EXTENSION IF NOT EXISTS laplace_substrate;
            SET search_path TO laplace, public;
        ";
                    await cmd.ExecuteNonQueryAsync();
                    await DeclareNativeFixtureFloorAsync(conn);
                    _sharedDataSource = candidate;
                }
                catch
                {
                    await candidate.DisposeAsync();
                    throw;
                }
            }
            // Acquire ownership only after initialization has succeeded.
            _ds = _sharedDataSource ?? throw new InvalidOperationException("Shared fixture pool is unavailable");
            _refCount++;
        }
        finally
        {
            InitGate.Release();
        }
    }

    private static async Task DeclareNativeFixtureFloorAsync(NpgsqlConnection connection)
    {
        // Loading the native floor makes its atoms resolvable; it does not
        // persist their entity rows. This finite basis covers descriptor vocabulary,
        // the 15 copied authority artifacts, and the accented/Greek fixture words.
        // It does not mark the full Unicode layer complete or invent atom IDs.
        string[] symbols = Enumerable.Range(32, 95).Select(char.ConvertFromUtf32)
            .Concat("\t\n\r³×éñóúαζκλ—“”→".Select(c => c.ToString())).ToArray();
        await using var declare = connection.CreateCommand();
        declare.CommandText = """
            WITH basis AS MATERIALIZED (
                SELECT root_id, tier FROM converse.text_root_placements($1)
            )
            INSERT INTO laplace.entities(id,tier,type_id,first_observed_by)
            SELECT root_id,0,laplace.entity_type_id('Codepoint'),
                   realize.canonical_id('substrate/source/UnicodeDecomposer/v1')
            FROM basis WHERE tier=0
            ON CONFLICT DO NOTHING
            """;
        declare.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Text, symbols);
        await declare.ExecuteNonQueryAsync();

        await using var verify = connection.CreateCommand();
        verify.CommandText = """
            WITH basis AS MATERIALIZED (
                SELECT root_id, tier FROM converse.text_root_placements($1)
            )
            SELECT count(DISTINCT basis.root_id)
            FROM basis JOIN laplace.entities e ON e.id=basis.root_id
            WHERE basis.tier=0 AND e.tier=0
              AND e.type_id=laplace.entity_type_id('Codepoint')
              AND e.first_observed_by=realize.canonical_id('substrate/source/UnicodeDecomposer/v1')
            """;
        verify.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Text, symbols);
        if (await verify.ExecuteScalarAsync() is not long count || count != symbols.Length)
            throw new InvalidOperationException(
                "Fixture basis did not retain every declared actual native floor entity");
    }

    public async Task DisposeAsync()
    {
        await InitGate.WaitAsync();
        try
        {
            if (_ds is null) return;
            _ds = null;
            if (--_refCount == 0)
            {
                await _sharedDataSource!.DisposeAsync();
                _sharedDataSource = null;
                await RunPsqlAdminAsync("dropdb", $"-h {PgHost} -U {PgUser} --force --if-exists {DatabaseName}");
            }
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
        const string pgBin = @"C:\Program Files\PostgreSQL\18\bin";
        string exe = Path.Combine(pgBin, program + ".exe");
        return File.Exists(exe) ? exe : program;
    }
}
