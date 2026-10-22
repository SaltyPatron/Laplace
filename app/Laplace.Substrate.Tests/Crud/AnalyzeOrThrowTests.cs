using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>A post-ingest ANALYZE that PostgreSQL skipped is a failure, never a success
/// with a WARNING (Laplace#1879).</summary>
public sealed class AnalyzeSkipFailureTests
{
    [Fact]
    public void NoWarnings_IsNoFailure() =>
        Assert.Null(NpgsqlIngestOps.AnalyzeSkipFailure("analyze", []));

    [Fact]
    public void PermissionWarnings_NameEachRelationOnce()
    {
        var failure = NpgsqlIngestOps.AnalyzeSkipFailure("analyze_post_ingest_validation",
        [
            "permission denied to analyze \"attestations_h0\", skipping it",
            "permission denied to analyze \"attestations_h1\", skipping it",
            "permission denied to analyze \"attestations_h0\", skipping it",
        ]);
        Assert.NotNull(failure);
        Assert.Equal(["attestations_h0", "attestations_h1"], failure!.Relations);
        Assert.Contains("ANALYZE skipped 2 relation(s)", failure.Message);
        Assert.Contains("attestations_h0, attestations_h1", failure.Message);
        Assert.Contains("MAINTAIN", failure.Message);
    }

    [Fact]
    public void AnyOtherWarning_IsAlsoAFailure()
    {
        var failure = NpgsqlIngestOps.AnalyzeSkipFailure("analyze", ["something unexpected"]);
        Assert.NotNull(failure);
        Assert.Equal(["something unexpected"], failure!.Relations);
        Assert.DoesNotContain("MAINTAIN", failure.Message);
    }
}

[Collection("substrate-pg")]
[Trait("Category", "RequiresDatabase")]
[Trait("Tier", "db")]
public sealed class AnalyzeOrThrowDbTests(LocalPgFixture pg)
{
    [Fact]
    public async Task RoleWithoutMaintain_Throws_NamingEveryLeaf_AndWithMaintain_RefreshesReltuples()
    {
        string tag = Guid.NewGuid().ToString("N")[..12];
        string role = "analyze_probe_" + tag, table = "analyze_probe_" + tag;
        await using var conn = await pg.DataSource.OpenConnectionAsync();
        async Task Exec(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await Exec($"""
            CREATE ROLE {role} NOLOGIN;
            CREATE TABLE public.{table} (id int) PARTITION BY HASH (id);
            CREATE TABLE public.{table}_h0 PARTITION OF public.{table} FOR VALUES WITH (MODULUS 2, REMAINDER 0);
            CREATE TABLE public.{table}_h1 PARTITION OF public.{table} FOR VALUES WITH (MODULUS 2, REMAINDER 1);
            INSERT INTO public.{table} SELECT generate_series(1, 100);
            GRANT SELECT ON public.{table}, public.{table}_h0, public.{table}_h1 TO {role};
            GRANT MAINTAIN ON public.{table} TO {role};
            """);
        try
        {
            await Exec($"SET ROLE {role}");
            await using var analyze = new NpgsqlCommand($"ANALYZE public.{table}", conn);
            var ex = await Assert.ThrowsAsync<AnalyzeIncompleteException>(() =>
                NpgsqlIngestOps.AnalyzeOrThrowAsync(analyze, "probe"));
            Assert.Equal([table + "_h0", table + "_h1"], ex.Relations.Order().ToArray());
            await Exec("RESET ROLE");

            await Exec($"GRANT MAINTAIN ON public.{table}_h0, public.{table}_h1 TO {role}; SET ROLE {role}");
            await NpgsqlIngestOps.AnalyzeOrThrowAsync(analyze, "probe");
            await Exec("RESET ROLE");
            await using var cmd = new NpgsqlCommand(
                $"SELECT sum(reltuples)::bigint FROM pg_class WHERE relname IN ('{table}_h0', '{table}_h1')", conn);
            Assert.Equal(100L, (long)(await cmd.ExecuteScalarAsync())!);
        }
        finally
        {
            await Exec($"RESET ROLE; DROP TABLE IF EXISTS public.{table}; DROP OWNED BY {role}; DROP ROLE IF EXISTS {role}");
        }
    }
}
