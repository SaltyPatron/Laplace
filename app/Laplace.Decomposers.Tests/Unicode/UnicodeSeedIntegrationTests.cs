using System.Diagnostics;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Tests;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Unicode.Tests;

[Trait("Tier", "db")]
public sealed class UnicodeSeedIntegrationTests : IAsyncLifetime
{
    public const string DatabaseName = "laplace_unicode_seed_test";

    private static readonly NpgsqlConnectionStringBuilder Conn =
        new(LaplaceInstall.PostgresConnectionString(DatabaseName));

    public static readonly string PgHost = Conn.Host!;
    public static readonly string PgUser = Conn.Username!;
    public static readonly string? PgPassword = Conn.Password;

    public static readonly string EcosystemPath = TestIngestPaths.UcdLatest;

    private const int TotalCodepoints = 1_114_112;

    private NpgsqlDataSource _ds = null!;

    public string ConnectionString => Conn.ConnectionString;

    public async Task InitializeAsync()
    {
        await RunPsqlAdminAsync("dropdb", $"-h {PgHost} -U {PgUser} --force --if-exists {DatabaseName}");
        await RunPsqlAdminAsync("createdb", $"-h {PgHost} -U {PgUser} -O {PgUser} {DatabaseName}");

        _ds = new NpgsqlDataSourceBuilder(ConnectionString).Build();
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE EXTENSION IF NOT EXISTS postgis;
            CREATE EXTENSION IF NOT EXISTS laplace_geom;
            CREATE EXTENSION IF NOT EXISTS laplace_substrate;
            SET search_path TO laplace, public;";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_ds is not null) await _ds.DisposeAsync();
        await RunPsqlAdminAsync("dropdb", $"-h {PgHost} -U {PgUser} --force --if-exists {DatabaseName}");
    }

    [Fact]
    public async Task Seeds_All_Codepoints_FromSource()
    {







        if (!CodepointPerfcache.IsLoaded) CodepointPerfcache.Load(TestInstall.ResolvePerfcacheOrThrow());
        IntentStage.ResetContentBank();

        var writer = new NpgsqlSubstrateWriter(_ds);
        var reader = new NpgsqlSubstrateReader(_ds);
        var dec = new UnicodeDecomposer();
        var ctx = new SeedContext(writer, reader, EcosystemPath);

        await dec.InitializeAsync(ctx);
        long applied = 0;
        await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
        {
            await writer.ApplyAsync(change);
            applied += change.Entities.Length;
        }
        Assert.True(applied >= TotalCodepoints,
            $"presented {applied:N0} entities, expected at least {TotalCodepoints:N0}");




        long resolvable = await ScalarLong(
            @"SELECT count(*) FROM laplace.entities e
              WHERE e.type_id = laplace.canonical_id('Codepoint')
                AND e.tier = 0
                AND laplace.codepoint_for_id(e.id) IS NOT NULL");
        Assert.True(resolvable > 1_100_000, $"perfcache-resolvable codepoints unexpectedly few: {resolvable:N0}");







        long physCount = await ScalarLong("SELECT laplace.content_count()");
        Assert.True(physCount >= TotalCodepoints,
            $"content physicalities {physCount:N0} < codepoint count {TotalCodepoints:N0}");



        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT p.x, p.y, p.z, p.m
                            FROM laplace.entity_physicalities(laplace.canonical_id('A')) p
                            WHERE p.type = 1";
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync(), "no CONTENT physicality for U+0041");
        double x = r.GetDouble(0), y = r.GetDouble(1), z = r.GetDouble(2), m = r.GetDouble(3);
        Assert.InRange(Math.Sqrt(x * x + y * y + z * z + m * m), 1.0 - 1e-9, 1.0 + 1e-9);
    }

    private async Task<long> ScalarLong(string sql, params (string, object)[] ps)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return (long)(await cmd.ExecuteScalarAsync())!;
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

    private sealed class SeedContext(ISubstrateWriter writer, ISubstrateReader reader, string ecosystemPath)
        : IDecomposerContext
    {
        public string EcosystemPath { get; } = ecosystemPath;
        public ISubstrateWriter Writer { get; } = writer;
        public ISubstrateReader Reader { get; } = reader;
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public string SubstrateVersion => "test";
    }
}
