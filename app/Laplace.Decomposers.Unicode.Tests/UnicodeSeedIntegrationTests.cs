using System.Diagnostics;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Unicode.Tests;

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
        var dec = new UnicodeDecomposer();
        var ctx = new SeedContext(writer, reader);

        await dec.InitializeAsync(ctx);
        long applied = 0;
        await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
        {
            await writer.ApplyAsync(change);
            applied += change.Entities.Length;
        }
        Assert.True(applied >= TotalCodepoints,
            $"presented {applied:N0} entities, expected at least {TotalCodepoints:N0}");

        long renderable = await ScalarLong("SELECT count(*) FROM laplace.codepoint_render");
        long covered = await ScalarLong(
            "SELECT count(*) FROM laplace.codepoint_render cr JOIN laplace.entities e ON e.id = cr.id");
        Assert.True(renderable > 1_100_000, $"codepoint_render unexpectedly small: {renderable:N0}");
        Assert.Equal(renderable, covered);

        long physCount = await ScalarLong(
            "SELECT laplace.content_count(@s)",
            ("s", UnicodeDecomposer.Source.ToBytes()));
        Assert.True(physCount >= TotalCodepoints,
            $"content physicalities {physCount:N0} < codepoint count {TotalCodepoints:N0}");

        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT p.x, p.y, p.z, p.m
                            FROM laplace.entity_physicalities(laplace.canonical_id('A')) p
                            WHERE p.source_id = @s AND p.type = 1";
        cmd.Parameters.AddWithValue("s", UnicodeDecomposer.Source.ToBytes());
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

    private sealed class SeedContext(ISubstrateWriter writer, ISubstrateReader reader) : IDecomposerContext
    {
        public string EcosystemPath => "/vault/Data/Unicode";
        public ISubstrateWriter Writer { get; } = writer;
        public ISubstrateReader Reader { get; } = reader;
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public string SubstrateVersion => "test";
    }
}
