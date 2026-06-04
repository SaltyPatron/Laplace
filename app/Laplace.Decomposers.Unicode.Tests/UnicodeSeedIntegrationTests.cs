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
/// (the DB half of; the cross-verify of #49). Runs against the SHARED dev
/// database (laplace-dev; LAPLACE_TEST_DB overrides) as <c>laplace_admin</c> —
/// two databases only (laplace-dev + laplace), never a per-run DB. The seed is
/// content-addressed and idempotent (ON CONFLICT), so re-running against an
/// already-seeded substrate converges to the same rows; every assertion is
/// source-/type-scoped, never a global count.
/// </summary>
public sealed class UnicodeSeedIntegrationTests : IAsyncLifetime
{
    private static readonly string DatabaseName =
        Environment.GetEnvironmentVariable("LAPLACE_TEST_DB") ?? "laplace-dev";
    private const string PgUser = "laplace_admin";
    private const int TotalCodepoints = 1_114_112;

    private NpgsqlDataSource _ds = null!;

    public async Task InitializeAsync()
    {
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
    }

    [Fact]
    public async Task Seeds_All_Codepoints_FromSource()
    {
        var writer = new NpgsqlSubstrateWriter(_ds);
        var reader = new NpgsqlSubstrateReader(_ds);
        // No ctor-injected paths: production code resolves from context.EcosystemPath.
        var dec = new UnicodeDecomposer();
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

        // 'A' (U+0041) — BLAKE3-128 of single byte 0x41, present in the DB.
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT ST_X(coord), ST_Y(coord), ST_Z(coord), ST_M(coord)
                            FROM laplace.physicalities
                            WHERE entity_id = @e AND source_id = @s AND kind = 1";
        cmd.Parameters.AddWithValue("e", Hash128.Blake3(new byte[] { 0x41 }).ToBytes());
        cmd.Parameters.AddWithValue("s", UnicodeDecomposer.Source.ToBytes());
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync(), "no CONTENT physicality for U+0041");
        // every super-Fibonacci point lies on the unit glome (radius=1)
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

    private sealed class SeedContext(ISubstrateWriter writer, ISubstrateReader reader) : IDecomposerContext
    {
        public string EcosystemPath => "/vault/Data/Unicode";
        public ISubstrateWriter Writer { get; } = writer;
        public ISubstrateReader Reader { get; } = reader;
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public string SubstrateVersion => "test";
    }
}
