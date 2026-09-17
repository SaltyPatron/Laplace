using Laplace.Decomposers.Abstractions;
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
        var builder = new SubstrateChangeBuilder(source, "test/reconstruct/canonical")
            .DeclareSourcePrior(SourceTrust.StructuredCorpus);
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
        var builder = new SubstrateChangeBuilder(source, "test/reconstruct/nul")
            .DeclareSourcePrior(SourceTrust.StructuredCorpus);
        Assert.True(builder.ContentStage.TryAddContentWitness(bytes, source, out Hash128 id));
        await new NpgsqlSubstrateWriter(_pg.DataSource).ApplyAsync(builder.Build());
        Assert.Equal(bytes, await NpgsqlContentReconstructor.ReconstructUtf8Async(_pg.DataSource, id));

        await using var connection = await _pg.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT laplace.content_id(@bytes) = @id,
                   realize.render_bytes_batch(ARRAY[@id, NULL::bytea, @id])
                     IS NOT DISTINCT FROM ARRAY[@bytes, NULL::bytea, @bytes],
                   realize.render_bytes(@id, NULL) IS NOT DISTINCT FROM @bytes,
                   realize.render_text(@id) IS NULL
            """;
        command.Parameters.Add("bytes", NpgsqlDbType.Bytea).Value = bytes;
        command.Parameters.Add("id", NpgsqlDbType.Bytea).Value = id.ToBytes();
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (int i = 0; i < 4; ++i) Assert.True(reader.GetBoolean(i));
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
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        await writer.ApplyAsync(new SubstrateChangeBuilder(source, "test/reconstruct/cycle-entities")
            .AddEntity(a, EntityTier.Document, type, source)
            .AddEntity(b, EntityTier.Document, type, source)
            .Build());

        // A legal content writer cannot create a cycle: a one-child content node
        // collapses to its child and every multi-child parent is content-addressed.
        // Keep the corruption-defense test by bypassing the writer and injecting
        // malformed stored trajectories directly. The read path must still fail
        // closed if disk state is corrupt.
        await using (var corrupt = _pg.DataSource.CreateCommand("""
            INSERT INTO laplace.physicalities
                (id, entity_id, type, coord, hilbert_index, trajectory, n_constituents)
            VALUES
                (@aid, @a, @ptype, ST_MakePoint(1,0,0,0), decode(repeat('00',16),'hex'),
                 public.laplace_trajectory_build(ARRAY[@b]::bytea[]), 1),
                (@bid, @b, @ptype, ST_MakePoint(1,0,0,0), decode(repeat('01',16),'hex'),
                 public.laplace_trajectory_build(ARRAY[@a]::bytea[]), 1)
            """))
        {
            corrupt.Parameters.Add("aid", NpgsqlDbType.Bytea).Value =
                PhysicalityId.Compute(a, PhysicalityType.Content).ToBytes();
            corrupt.Parameters.Add("bid", NpgsqlDbType.Bytea).Value =
                PhysicalityId.Compute(b, PhysicalityType.Content).ToBytes();
            corrupt.Parameters.Add("a", NpgsqlDbType.Bytea).Value = a.ToBytes();
            corrupt.Parameters.Add("b", NpgsqlDbType.Bytea).Value = b.ToBytes();
            corrupt.Parameters.Add("ptype", NpgsqlDbType.Smallint).Value = (short)PhysicalityType.Content;
            await corrupt.ExecuteNonQueryAsync();
        }

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
        Hash128 type = Hash128.OfCanonical("TestFixture");
        var builder = new SubstrateChangeBuilder(source, "test/render/depth")
            .DeclareSourcePrior(SourceTrust.StructuredCorpus);
        Assert.True(builder.ContentStage.TryAddContentWitness(
            Encoding.UTF8.GetBytes("a"), source, out Hash128 atomA));
        Assert.True(builder.ContentStage.TryAddContentWitness(
            Encoding.UTF8.GetBytes("b"), source, out Hash128 atomB));
        Assert.True(builder.ContentStage.TryAddContentWitness(
            Encoding.UTF8.GetBytes("c"), source, out Hash128 atomC));

        Hash128 inner = Hash128.Merkle((byte)EntityTier.Word, [atomA, atomB]);
        Hash128 outer = Hash128.Merkle((byte)EntityTier.Sentence, [inner, atomC]);
        Hilbert128 hilbert = Hilbert128.Encode([1, 0, 0, 0]);
        builder.AddEntity(inner, EntityTier.Word, type, source)
            .AddEntity(outer, EntityTier.Sentence, type, source)
            .AddPhysicality(Composition(inner, [atomA, atomB], source, hilbert))
            .AddPhysicality(Composition(outer, [inner, atomC], source, hilbert));
        await new NpgsqlSubstrateWriter(_pg.DataSource).ApplyAsync(builder.Build());

        await using var connection = await _pg.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT realize.render_text(@outer, 1) IS NULL,
                   realize.render_text(@inner, 1) = 'ab',
                   realize.render_text_batch(ARRAY[@outer, @inner], 1)
                       IS NOT DISTINCT FROM ARRAY[NULL::text, 'ab'],
                   realize.render_text_batch(ARRAY[@inner, @outer], 1)
                       IS NOT DISTINCT FROM ARRAY['ab', NULL::text],
                   realize.render_text_batch(ARRAY[@outer, @inner], 0) = ARRAY['abc', 'ab']
            """;
        command.Parameters.Add("outer", NpgsqlDbType.Bytea).Value = outer.ToBytes();
        command.Parameters.Add("inner", NpgsqlDbType.Bytea).Value = inner.ToBytes();
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (int i = 0; i < 5; i++) Assert.True(reader.GetBoolean(i));
    }


    [Fact]
    public async Task ModelRecipes_BatchPreservesElectionNullsAndAlignedProvenance()
    {
        CodepointPerfcache.LoadDefault();
        string nonce = Guid.NewGuid().ToString("N");
        Hash128 source = Hash128.OfCanonical($"model-recipes/source/{nonce}");
        Hash128 otherSource = Hash128.OfCanonical($"model-recipes/other-source/{nonce}");
        Hash128[] recipes = Enumerable.Range(0, 5)
            .Select(i => Hash128.OfCanonical($"model-recipes/{nonce}/{i}")).ToArray();
        string[] json = ["{\"name\":\"alpha-" + nonce + "\"}", "{\"name\":\"beta-" + nonce + "\"}"];
        using var builder = new SubstrateChangeBuilder(source, "test/model-recipes/aligned")
            .DeclareSourcePrior(SourceTrust.StructuredCorpus)
            .DeclareSourcePrior(otherSource, SourceTrust.StructuredCorpus);
        Assert.True(builder.ContentStage.TryAddContentWitness(
            Encoding.UTF8.GetBytes(json[0]), source, out Hash128 a));
        Assert.True(builder.ContentStage.TryAddContentWitness(
            Encoding.UTF8.GetBytes(json[1]), source, out Hash128 b));
        // Valid binary content that has no PostgreSQL text representation keeps
        // its recipe row with a NULL body; it must not shift the other columns.
        Assert.True(builder.ContentStage.TryAddContentWitness([0], source, out Hash128 nul));

        builder.AddEntity(recipes[0], EntityTier.Word, EntityTypeRegistry.ModelRecipe, source)
            .AddEntity(recipes[0], EntityTier.Sentence, EntityTypeRegistry.ModelRecipe, otherSource)
            .AddEntity(recipes[1], EntityTier.Word, EntityTypeRegistry.ModelRecipe)
            .AddEntity(recipes[2], EntityTier.Word, EntityTypeRegistry.ModelRecipe, otherSource)
            .AddEntity(recipes[3], EntityTier.Word, EntityTypeRegistry.ModelRecipe, source)
            .AddEntity(recipes[4], EntityTier.Word, EntityTypeRegistry.ModelRecipe, source);
        Hash128 encodes = RelationTypeRegistry.RelationTypeId("ENCODES");
        foreach (var (recipe, content) in new[]
                 { (recipes[0], a), (recipes[0], b), (recipes[1], b), (recipes[2], b), (recipes[3], nul) })
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                recipe, encodes, content, source, null, 1.0));

        var change = builder.Build();
        try
        {
            // Recipe election reads durable ENCODES standings, so fixture admission
            // must use the same atomic evidence-and-consensus owner as production.
            await using var writer = new ConsensusAccumulatingWriter(
                new NpgsqlSubstrateWriter(_pg.DataSource), _pg.DataSource);
            await writer.ApplyAsync(change);
        }
        finally
        {
            foreach (var stage in change.IntentStages) stage.Dispose();
        }

        await using (var standing = _pg.DataSource.CreateCommand("""
            SELECT count(*) FROM laplace.consensus
            WHERE type_id = @encodes AND subject_id = ANY(@recipes)
            """))
        {
            standing.Parameters.Add("encodes", NpgsqlDbType.Bytea).Value = encodes.ToBytes();
            standing.Parameters.Add("recipes", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value =
                recipes.Select(id => id.ToBytes()).ToArray();
            Assert.Equal(5L, (long)(await standing.ExecuteScalarAsync())!);
        }

        await using (var scalar = _pg.DataSource.CreateCommand(
            "SELECT realize.render_text_fast(@a, 16), realize.render_text_fast(@b, 16)"))
        {
            scalar.Parameters.Add("a", NpgsqlDbType.Bytea).Value = a.ToBytes();
            scalar.Parameters.Add("b", NpgsqlDbType.Bytea).Value = b.ToBytes();
            await using var reader = await scalar.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(json[0], reader.GetString(0));
            Assert.Equal(json[1], reader.GetString(1));
        }

        string Hex(Hash128 id) => Convert.ToHexString(id.ToBytes());
        string firstSource = new[] { Hex(source), Hex(otherSource) }
            .OrderBy(value => value, StringComparer.Ordinal).First();
        string chosenJson = StringComparer.Ordinal.Compare(Hex(a), Hex(b)) < 0 ? json[0] : json[1];
        var expected = new Dictionary<string, (string? Json, string? Source)>
        {
            [Hex(recipes[0])] = (chosenJson, firstSource),
            [Hex(recipes[1])] = (json[1], null),
            [Hex(recipes[2])] = (json[1], Hex(otherSource)),
            [Hex(recipes[3])] = (null, Hex(source)),
        };
        await using var command = _pg.DataSource.CreateCommand("""
            SELECT recipe_id, recipe_json, first_observed_by
            FROM structural.model_recipes()
            WHERE recipe_id = ANY(@ids)
            """);
        command.Parameters.Add("ids", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value =
            recipes.Select(id => id.ToBytes()).ToArray();
        await using var rows = await command.ExecuteReaderAsync();
        var actual = new Dictionary<string, (string? Json, string? Source)>();
        while (await rows.ReadAsync())
            actual.Add(Convert.ToHexString((byte[])rows[0]),
                (rows.IsDBNull(1) ? null : rows.GetString(1),
                 rows.IsDBNull(2) ? null : Convert.ToHexString((byte[])rows[2])));
        Assert.Equal(expected.Count, actual.Count);
        foreach (var row in expected)
        {
            Assert.True(actual.TryGetValue(row.Key, out var value), row.Key);
            Assert.Equal(row.Value, value);
        }
        Assert.DoesNotContain(Hex(recipes[4]), actual.Keys);
    }

    [Fact]
    public async Task ModelRecipes_ReconstructsCompleteJsonBeyondTheFormerPreviewDepth()
    {
        CodepointPerfcache.LoadDefault();
        string nonce = Guid.NewGuid().ToString("N");
        string json = "{\"name\":\"deep-" + nonce + "\"}";
        Hash128 source = Hash128.OfCanonical($"model-recipes/deep-source/{nonce}");
        Hash128 recipe = Hash128.OfCanonical($"model-recipes/deep/{nonce}");
        using var builder = new SubstrateChangeBuilder(source, "test/model-recipes/deep")
            .DeclareSourcePrior(SourceTrust.StructuredCorpus);
        Hilbert128 hilbert = Hilbert128.Encode([1, 0, 0, 0]);
        Hash128 content = default;
        for (int i = 0; i < json.Length; ++i)
        {
            Assert.True(builder.ContentStage.TryAddContentWitness(
                Encoding.UTF8.GetBytes(json[i].ToString()), source, out Hash128 atom));
            if (i == 0)
            {
                content = atom;
                continue;
            }
            // Every parent has two real children and their native Merkle identity.
            // This is a valid deep constituent DAG, not a corrupt unary wrapper.
            Hash128[] children = [content, atom];
            byte tier = checked((byte)(i + 1));
            content = Hash128.Merkle(tier, children);
            builder.AddEntity(content, tier, EntityTypeRegistry.Text, source)
                .AddPhysicality(Composition(content, children, source, hilbert));
        }
        Assert.True(json.Length > 17);
        builder.AddEntity(recipe, EntityTier.Word, EntityTypeRegistry.ModelRecipe, source)
            .AddAttestation(NativeAttestation.CategoricalResolved(
                recipe, RelationTypeRegistry.RelationTypeId("ENCODES"), content, source, null, 1.0));
        var change = builder.Build();
        try
        {
            // Recipe election reads durable ENCODES standings, so fixture admission
            // must use the same atomic evidence-and-consensus owner as production.
            await using var writer = new ConsensusAccumulatingWriter(
                new NpgsqlSubstrateWriter(_pg.DataSource), _pg.DataSource);
            await writer.ApplyAsync(change);
        }
        finally
        {
            foreach (var stage in change.IntentStages) stage.Dispose();
        }

        await using (var standing = _pg.DataSource.CreateCommand("""
            SELECT EXISTS (
                SELECT 1 FROM laplace.consensus
                WHERE type_id = @encodes AND subject_id = @recipe AND object_id = @content)
            """))
        {
            standing.Parameters.Add("encodes", NpgsqlDbType.Bytea).Value =
                RelationTypeRegistry.RelationTypeId("ENCODES").ToBytes();
            standing.Parameters.Add("recipe", NpgsqlDbType.Bytea).Value = recipe.ToBytes();
            standing.Parameters.Add("content", NpgsqlDbType.Bytea).Value = content.ToBytes();
            Assert.True((bool)(await standing.ExecuteScalarAsync())!);
        }

        await using var command = _pg.DataSource.CreateCommand("""
            SELECT realize.render_text_fast(@content, 16) IS NULL,
                   m.recipe_json, m.first_observed_by
            FROM structural.model_recipes() m
            WHERE m.recipe_id = @recipe
            """);
        command.Parameters.Add("content", NpgsqlDbType.Bytea).Value = content.ToBytes();
        command.Parameters.Add("recipe", NpgsqlDbType.Bytea).Value = recipe.ToBytes();
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0), "fixture must exceed the former scalar preview depth");
        Assert.Equal(json, reader.GetString(1));
        Assert.Equal(source.ToBytes(), (byte[])reader[2]);
        Assert.False(await reader.ReadAsync());
    }

    private static PhysicalityRow Composition(
        Hash128 entity, Hash128[] children, Hash128 source, Hilbert128 hilbert) =>
        new(
            PhysicalityId.Compute(entity, PhysicalityType.Content),
            entity,
            source,
            PhysicalityType.Content,
            1, 0, 0, 0,
            hilbert,
            Trajectory.Build(children),
            children.Length,
            null,
            null,
            0);
}
