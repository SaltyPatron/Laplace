using System.Diagnostics;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Unicode.Tests;

/// <summary>
/// End-to-end: UnicodeDecomposer → real NpgsqlSubstrateWriter → live PG with
/// the laplace extensions, then verify the seeded rows match the perf-cache
/// (the DB half of ADR 0006; the cross-verify of #49). Runs against the
/// deployed cluster as <c>laplace_admin</c> (the laplace extensions aren't in
/// the postgis Docker image, so Testcontainers can't host this — same pattern
/// as LocalPgFixture in SubstrateCRUD.Tests).
/// </summary>
public sealed class UnicodeSeedIntegrationTests : IAsyncLifetime
{
    private const string DatabaseName = "laplace_unicode_seed_test";
    private const string PgUser = "laplace_admin";
    private const int TotalCodepoints = 1_114_112;

    private NpgsqlDataSource _ds = null!;

    public async Task InitializeAsync()
    {
        await RunAdmin("dropdb", $"-U {PgUser} --if-exists {DatabaseName}");
        await RunAdmin("createdb", $"-U {PgUser} -O {PgUser} {DatabaseName}");
        _ds = new NpgsqlDataSourceBuilder($"Host=/var/run/postgresql;Username={PgUser};Database={DatabaseName}").Build();
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE EXTENSION IF NOT EXISTS postgis;
            CREATE EXTENSION IF NOT EXISTS laplace_geom;
            CREATE EXTENSION IF NOT EXISTS laplace_substrate;";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_ds is not null) await _ds.DisposeAsync();
        await RunAdmin("dropdb", $"-U {PgUser} --if-exists {DatabaseName}");
    }

    [Fact]
    public async Task Seeds_All_Codepoints_And_Matches_Perfcache()
    {
        var blob = LocateBlob();
        var writer = new NpgsqlSubstrateWriter(_ds);
        var reader = new NpgsqlSubstrateReader(_ds);
        var dec = new UnicodeDecomposer(blob);
        var ctx = new SeedContext(writer, reader);

        // Bootstrap (source + Codepoint type + trust-class), then stream the seed.
        await dec.InitializeAsync(ctx);
        long applied = 0;
        await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
        {
            await writer.ApplyAsync(change);
            applied += change.Entities.Length;
        }
        Assert.Equal(TotalCodepoints, applied);

        // DB now holds all 1,114,112 Codepoint entities.
        long entityCount = await ScalarLong(
            "SELECT count(*) FROM laplace.entities WHERE type_id = @t",
            ("t", UnicodeDecomposer.CodepointType.ToBytes()));
        Assert.Equal(TotalCodepoints, entityCount);

        // ... each with exactly one CONTENT physicality from UnicodeDecomposer.
        long physCount = await ScalarLong(
            "SELECT count(*) FROM laplace.physicalities WHERE source_id = @s AND kind = 1",
            ("s", UnicodeDecomposer.Source.ToBytes()));
        Assert.Equal(TotalCodepoints, physCount);

        // Cross-verify (#49): 'A' (U+0041) coords in the DB == the perf-cache.
        double ax, ay, az, am;
        byte[] aId;
        {
            var rec = CodepointPerfcache.Records[0x41];
            aId = rec.Hash.ToBytes();
            ax = rec.CoordX; ay = rec.CoordY; az = rec.CoordZ; am = rec.CoordM;
        }
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT ST_X(coord), ST_Y(coord), ST_Z(coord), ST_M(coord)
                            FROM laplace.physicalities
                            WHERE entity_id = @e AND source_id = @s AND kind = 1";
        cmd.Parameters.AddWithValue("e", aId);
        cmd.Parameters.AddWithValue("s", UnicodeDecomposer.Source.ToBytes());
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync(), "no CONTENT physicality for U+0041");
        Assert.Equal(ax, r.GetDouble(0));
        Assert.Equal(ay, r.GetDouble(1));
        Assert.Equal(az, r.GetDouble(2));
        Assert.Equal(am, r.GetDouble(3));
    }

    private async Task<long> ScalarLong(string sql, params (string, object)[] ps)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static string LocateBlob()
    {
        var env = Environment.GetEnvironmentVariable("LAPLACE_PERFCACHE_BIN");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        const string share = "/opt/laplace/share/laplace";
        if (Directory.Exists(share))
        {
            var hit = Directory.EnumerateFiles(share, "laplace_t0_perfcache*.bin").FirstOrDefault();
            if (hit is not null) return hit;
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            foreach (var build in dir.EnumerateDirectories("build*"))
            {
                var hit = Directory.EnumerateFiles(build.FullName, "laplace_t0_perfcache.bin",
                                                   SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null) return hit;
            }
        throw new InvalidOperationException("perf-cache blob not found; build the engine or set LAPLACE_PERFCACHE_BIN.");
    }

    private static async Task RunAdmin(string program, string args)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = program, Arguments = args,
            RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false,
        })!;
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{program} {args} exited {p.ExitCode}: {await p.StandardError.ReadToEndAsync()}");
    }

    private sealed class SeedContext(ISubstrateWriter writer, ISubstrateReader reader) : IDecomposerContext
    {
        public string EcosystemPath => "/vault/Data/Unicode";
        public ISubstrateWriter Writer { get; } = writer;
        public ISubstrateReader Reader { get; } = reader;
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public string SubstrateVersion => "test";
    }
}
