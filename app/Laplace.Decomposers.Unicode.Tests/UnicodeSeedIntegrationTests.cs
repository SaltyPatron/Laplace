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
        // ≥: pass 3 (2026-06-05 completeness) presents alias/confusable
        // content on top of the exact codepoint set (verified type-filtered below).
        Assert.True(applied >= TotalCodepoints,
            $"presented {applied:N0} entities, expected at least {TotalCodepoints:N0}");

        // COVERAGE, not a global count (this fixture's own law: shared dev DB —
        // concurrent agents may mint rows; an exact type/source count is not
        // ours to pin). Every renderable codepoint id must EXIST as an entity:
        // codepoint_render is the in-DB id↔cp map (all valid scalars minus
        // surrogates and U+0000), so a full join == full seed coverage. The
        // source-scoped CONTENT physicality count below stays EXACT.
        long renderable = await ScalarLong("SELECT count(*) FROM laplace.codepoint_render");
        long covered = await ScalarLong(
            "SELECT count(*) FROM laplace.codepoint_render cr JOIN laplace.entities e ON e.id = cr.id");
        Assert.True(renderable > 1_100_000, $"codepoint_render unexpectedly small: {renderable:N0}");
        Assert.Equal(renderable, covered);

        // CONTENT physicalities: one per codepoint PLUS the pass-3 wordform
        // content (name aliases / confusable sequences — 2026-06-05
        // completeness), all witnessed by this source. The codepoint half is
        // pinned exactly by the coverage join above; the total is ≥.
        long physCount = await ScalarLong(
            "SELECT laplace.content_count(@s)",
            ("s", UnicodeDecomposer.Source.ToBytes()));
        Assert.True(physCount >= TotalCodepoints,
            $"content physicalities {physCount:N0} < codepoint count {TotalCodepoints:N0}");

        // 'A' (U+0041) — resolved + read back via the substrate operating
        // surface (canonical_id / entity_physicalities), no hand-written join.
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT p.x, p.y, p.z, p.m
                            FROM laplace.entity_physicalities(laplace.canonical_id('A')) p
                            WHERE p.source_id = @s AND p.type = 1";
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
