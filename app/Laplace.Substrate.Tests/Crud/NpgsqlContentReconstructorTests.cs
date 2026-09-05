using System.Diagnostics;
using System.Text;
using Laplace.Decomposers.Abstractions.Tests;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class NpgsqlContentReconstructorTests : IAsyncLifetime
{
    private readonly LocalPgFixture _pg;

    public NpgsqlContentReconstructorTests(LocalPgFixture pg) => _pg = pg;

    public async Task InitializeAsync()
    {
        string sqlPath = Path.Combine(
            TypeIdLawTests.FindRepoRootPublic(),
            "extension", "laplace_substrate", "sql", "functions", "readback",
            "reconstruct_content.sql.in");
        var start = new ProcessStartInfo
        {
            FileName = ResolvePsql(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-X");
        start.ArgumentList.Add("--host");
        start.ArgumentList.Add(LocalPgFixture.PgHost);
        start.ArgumentList.Add("--username");
        start.ArgumentList.Add(LocalPgFixture.PgUser);
        start.ArgumentList.Add("--dbname");
        start.ArgumentList.Add(LocalPgFixture.DatabaseName);
        start.ArgumentList.Add("--set");
        start.ArgumentList.Add("ON_ERROR_STOP=1");
        start.ArgumentList.Add("--file");
        start.ArgumentList.Add(sqlPath);
        if (LocalPgFixture.PgPassword is not null)
            start.Environment["PGPASSWORD"] = LocalPgFixture.PgPassword;

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("failed to start psql");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"psql reconstruction setup exited {process.ExitCode}: {stderr.Result}\n{stdout.Result}");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string ResolvePsql()
    {
        if (!OperatingSystem.IsWindows()) return "psql";
        const string installed = @"C:\Program Files\PostgreSQL\18\bin\psql.exe";
        return File.Exists(installed) ? installed : "psql";
    }

    [Fact]
    public async Task ReconstructUtf8Async_ReturnsTheCanonicalNormalizedBytes()
    {
        byte[] admitted = Encoding.UTF8.GetBytes("Cafe\u0301 — exact canonical reconstruction");
        byte[] canonical = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(admitted).Normalize(NormalizationForm.FormC));
        Assert.NotEqual(admitted, canonical);

        Hash128 source = Hash128.OfCanonical(
            $"substrate/test/reconstruct/source/{Guid.NewGuid():N}");
        var builder = new SubstrateChangeBuilder(source, "test/reconstruct/canonical");
        Assert.True(builder.ContentStage.TryAddContentWitness(admitted, source, out Hash128 contentId));

        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        await writer.ApplyAsync(builder.Build());

        byte[] actual = await NpgsqlContentReconstructor.ReconstructUtf8Async(
            _pg.DataSource, contentId);
        Assert.Equal(canonical, actual);
    }

    [Theory]
    [InlineData("\0")]
    [InlineData("a\0b")]
    [InlineData("\0\0")]
    [InlineData("First sentence. Second \0 β sentence.\n")]
    public async Task ReconstructUtf8Async_PreservesUnicodeNul(string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        Hash128 source = Hash128.OfCanonical($"reconstruct-nul/source/{Guid.NewGuid():N}");
        var builder = new SubstrateChangeBuilder(source, "test/reconstruct/nul");
        Assert.True(builder.ContentStage.TryAddContentWitness(bytes, source, out Hash128 id));
        await new NpgsqlSubstrateWriter(_pg.DataSource).ApplyAsync(builder.Build());
        Assert.Equal(bytes, await NpgsqlContentReconstructor.ReconstructUtf8Async(_pg.DataSource, id));

        await using var connection = await _pg.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT laplace.content_id(@bytes) = @id,
                   realize.render_bytes_batch(ARRAY[@id, NULL::bytea, @id])
                     IS NOT DISTINCT FROM ARRAY[@bytes, NULL::bytea, @bytes],
                   realize.render_text(@id) IS NULL
            """;
        command.Parameters.Add("bytes", NpgsqlDbType.Bytea).Value = bytes;
        command.Parameters.Add("id", NpgsqlDbType.Bytea).Value = id.ToBytes();
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (int i = 0; i < 3; ++i) Assert.True(reader.GetBoolean(i));
    }

    [Fact]
    public async Task ReconstructUtf8Async_RejectsAnIncompleteContentIdentity()
    {
        Hash128 source = Hash128.OfCanonical(
            $"substrate/test/reconstruct/source/{Guid.NewGuid():N}");
        Hash128 incomplete = Hash128.OfCanonical(
            $"substrate/test/reconstruct/incomplete/{Guid.NewGuid():N}");
        Hash128 type = Hash128.OfCanonical("TestFixture");
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        await writer.ApplyAsync(new SubstrateChangeBuilder(source, "test/reconstruct/incomplete")
            .AddEntity(incomplete, EntityTier.Document, type, source)
            .Build());

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            NpgsqlContentReconstructor.ReconstructUtf8Async(_pg.DataSource, incomplete));
        Assert.Contains(incomplete.ToString(), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReconstructUtf8Async_RejectsACyclicComposition()
    {
        Hash128 source = Hash128.OfCanonical(
            $"substrate/test/reconstruct/source/{Guid.NewGuid():N}");
        Hash128 type = Hash128.OfCanonical("TestFixture");
        Hash128 a = Hash128.OfCanonical($"substrate/test/reconstruct/cycle-a/{Guid.NewGuid():N}");
        Hash128 b = Hash128.OfCanonical($"substrate/test/reconstruct/cycle-b/{Guid.NewGuid():N}");
        double[] coord = [1, 0, 0, 0];
        Hilbert128 hilbert = Hilbert128.Encode(coord);
        var builder = new SubstrateChangeBuilder(source, "test/reconstruct/cycle")
            .AddEntity(a, EntityTier.Document, type, source)
            .AddEntity(b, EntityTier.Document, type, source)
            .AddPhysicality(Composition(a, b, source, hilbert))
            .AddPhysicality(Composition(b, a, source, hilbert));

        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        await writer.ApplyAsync(builder.Build());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NpgsqlContentReconstructor.ReconstructUtf8Async(_pg.DataSource, a));

        // Reverse traversal must terminate on the same corrupted cycle, omit
        // the requested root, and return its parent once at the shortest hop.
        await using var connection = await _pg.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*) = 1 AND bool_and(entity_id = @b AND hops = 1)
            FROM structural.containers_of(@a, 20, NULL)
            """;
        command.Parameters.Add("a", NpgsqlDbType.Bytea).Value = a.ToBytes();
        command.Parameters.Add("b", NpgsqlDbType.Bytea).Value = b.ToBytes();
        Assert.Equal(true, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task BoundedRendering_MatchesScalarResultsRegardlessOfBatchOrder()
    {
        CodepointPerfcache.LoadDefault();
        Hash128 source = Hash128.OfCanonical($"render-depth/source/{Guid.NewGuid():N}");
        Hash128 inner = Hash128.OfCanonical($"render-depth/inner/{Guid.NewGuid():N}");
        Hash128 outer = Hash128.OfCanonical($"render-depth/outer/{Guid.NewGuid():N}");
        Hash128 type = Hash128.OfCanonical("TestFixture");
        var builder = new SubstrateChangeBuilder(source, "test/render/depth");
        Assert.True(builder.ContentStage.TryAddContentWitness(
            Encoding.UTF8.GetBytes("a"), source, out Hash128 atom));
        Hilbert128 hilbert = Hilbert128.Encode([1, 0, 0, 0]);
        builder.AddEntity(inner, EntityTier.Word, type, source)
            .AddEntity(outer, EntityTier.Sentence, type, source)
            .AddPhysicality(Composition(inner, atom, source, hilbert))
            .AddPhysicality(Composition(outer, inner, source, hilbert));
        await new NpgsqlSubstrateWriter(_pg.DataSource).ApplyAsync(builder.Build());

        await using var connection = await _pg.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT realize.render_text(@outer, 1) IS NULL,
                   realize.render_text(@inner, 1) = 'a',
                   realize.render_text_batch(ARRAY[@outer, @inner], 1)
                       IS NOT DISTINCT FROM ARRAY[NULL::text, 'a'],
                   realize.render_text_batch(ARRAY[@inner, @outer], 1)
                       IS NOT DISTINCT FROM ARRAY['a', NULL::text],
                   realize.render_text_batch(ARRAY[@outer, @inner], 0) = ARRAY['a', 'a']
            """;
        command.Parameters.Add("outer", NpgsqlDbType.Bytea).Value = outer.ToBytes();
        command.Parameters.Add("inner", NpgsqlDbType.Bytea).Value = inner.ToBytes();
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (int i = 0; i < 5; i++) Assert.True(reader.GetBoolean(i));
    }

    private static PhysicalityRow Composition(
        Hash128 entity, Hash128 child, Hash128 source, Hilbert128 hilbert) =>
        new(
            PhysicalityId.Compute(entity, PhysicalityType.Content),
            entity,
            source,
            PhysicalityType.Content,
            1, 0, 0, 0,
            hilbert,
            Trajectory.Build([child]),
            1,
            null,
            null,
            0);
}
