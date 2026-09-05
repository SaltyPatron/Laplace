using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class UserContentEndpointPgCollection : ICollectionFixture<UserContentEndpointPgFixture>
{
    public const string CollectionName = "user-content-endpoint-pg";
}

[Collection(UserContentEndpointPgCollection.CollectionName)]
[Trait("Tier", "db")]
public sealed class UserContentOwnershipIntegrationTests(UserContentEndpointPgFixture pg)
{
    [Fact]
    public async Task SelectedModelCode_IngestsThroughGrammarStructure_AndReadsBackExactly()
    {
        const string selectedPath =
            "/vault/models/Florence-2-base/configuration_florence2.py";
        byte[] source = await File.ReadAllBytesAsync(selectedPath);
        Assert.NotEmpty(source);

        await using var factory = new UserContentEndpointFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        string tenant = $"code-artifact-{Guid.NewGuid():N}";
        var request = new UserCodeArtifactWriteRequest(
            Name: "configuration_florence2.py",
            Path: "models/Florence-2-base/configuration_florence2.py",
            Text: null,
            ContentBase64: Convert.ToBase64String(source),
            UserId: null,
            ModifiedAt: DateTimeOffset.UnixEpoch);

        UserContentWriteResponse admitted = await AdmitCodeAsync(client, tenant, request);
        AssertCanonicalIds(admitted);
        Assert.Equal(admitted.ContentId, admitted.DocumentId);
        Assert.Equal(source.LongLength, admitted.Bytes);
        Assert.Equal("python", admitted.Modality);

        byte[] fileId = Convert.FromHexString(admitted.FileId);
        byte[] contentId = Convert.FromHexString(admitted.ContentId);
        await using (var conn = await pg.DataSource.OpenConnectionAsync())
        {
            var fileChildren = await NpgsqlSubstrateReads.PackedTrajectoryVerticesAsync(
                conn, fileId, CancellationToken.None);
            Assert.Collection(
                fileChildren.OrderBy(static child => child.Ordinal),
                content => Assert.Equal(admitted.ContentId, content.ChildIdHex),
                metadata => Assert.Equal(admitted.MetadataId, metadata.ChildIdHex));

            var grammarChildren = await NpgsqlSubstrateReads.PackedTrajectoryVerticesAsync(
                conn, contentId, CancellationToken.None);
            Assert.True(grammarChildren.Count > 1,
                "the admitted code root must retain ordered grammar structure");

            await using var semanticTags = new NpgsqlCommand("""
                SELECT encode(type_id, 'hex')
                FROM laplace.attestations
                WHERE source_id = @source
                  AND type_id = ANY(@types)
                  AND outcome = @outcome
                GROUP BY type_id
                """, conn);
            semanticTags.Parameters.Add("source", NpgsqlDbType.Bytea).Value =
                UserArtifactContent.Resolve(tenant).Source.ToBytes();
            semanticTags.Parameters.Add("types", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value =
                new[]
                {
                    RelationTypeRegistry.Resolve("DEFINES").Id.ToBytes(),
                    RelationTypeRegistry.Resolve("CALLS").Id.ToBytes()
                };
            semanticTags.Parameters.Add("outcome", NpgsqlDbType.Smallint).Value =
                (short)AttestationOutcome.Confirm;
            var witnessed = new HashSet<string>(StringComparer.Ordinal);
            await using var tagReader = await semanticTags.ExecuteReaderAsync();
            while (await tagReader.ReadAsync()) witnessed.Add(tagReader.GetString(0));
            Assert.Contains(
                Convert.ToHexStringLower(RelationTypeRegistry.Resolve("DEFINES").Id.ToBytes()),
                witnessed);
            Assert.Contains(
                Convert.ToHexStringLower(RelationTypeRegistry.Resolve("CALLS").Id.ToBytes()),
                witnessed);
        }

        await using (var conn = await pg.DataSource.OpenConnectionAsync())
        await using (var verify = new NpgsqlCommand(
            "SELECT realize.render_text(@content), laplace.grammar_source_id(@source, @modality)",
            conn))
        {
            verify.Parameters.Add("content", NpgsqlDbType.Bytea).Value = contentId;
            verify.Parameters.Add("source", NpgsqlDbType.Bytea).Value = source;
            verify.Parameters.Add("modality", NpgsqlDbType.Text).Value = "python";
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(admitted.ContentId, Convert.ToHexStringLower(reader.GetFieldValue<byte[]>(1)));
            Assert.False(reader.IsDBNull(0));
            Assert.Equal(Encoding.UTF8.GetString(source), reader.GetString(0));
        }

        await using (var conn = await pg.DataSource.OpenConnectionAsync())
        await using (var reconstruct = new NpgsqlCommand(
            "SELECT realize.reconstruct_content(@content, @modality)", conn))
        {
            reconstruct.Parameters.Add("content", NpgsqlDbType.Bytea).Value = contentId;
            reconstruct.Parameters.Add("modality", NpgsqlDbType.Text).Value = "python";
            byte[] reconstructed = Assert.IsType<byte[]>(await reconstruct.ExecuteScalarAsync());
            Assert.Equal(source, reconstructed);
        }

        using var exported = await ExportAsync(client, tenant, admitted.FileId);
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        UserContentExportResponse body = (await exported.Content
            .ReadFromJsonAsync<UserContentExportResponse>())!;
        AssertCanonicalIds(
            body.RequestedId,
            body.FileId!,
            body.DocumentId!,
            body.ContentId,
            body.MetadataId!,
            body.SourceId);
        Assert.Equal("configuration_florence2.py", body.Name);
        Assert.Equal("models/Florence-2-base/configuration_florence2.py", body.Path);
        Assert.Equal("code", body.Kind);
        Assert.Equal("python", body.Modality);
        Assert.Equal(source.LongLength, body.Bytes);
        Assert.Equal(DateTimeOffset.UnixEpoch, body.ModifiedAt);
        Assert.Equal(source, Convert.FromBase64String(body.ContentBase64));
        Assert.Equal(Encoding.UTF8.GetString(source), body.Text);
    }

    [Fact]
    public async Task SharedArtifact_ExportsForEveryTenantThatAdmittedIt_ButNoOtherTenant()
    {
        await using var factory = new UserContentEndpointFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        string suffix = Guid.NewGuid().ToString("N");
        string tenantA = $"artifact-a-{suffix}";
        string tenantB = $"artifact-b-{suffix}";
        string tenantC = $"artifact-c-{suffix}";
        const string text = "Exact shared tenant artifact.\nSecond line survives reconstruction.";
        var modifiedAt = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var request = new UserTextArtifactWriteRequest(
            Name: "shared.txt",
            Path: "proof/shared.txt",
            Text: text,
            ContentBase64: null,
            UserId: null,
            ModifiedAt: modifiedAt);

        UserContentWriteResponse admittedByA = await AdmitAsync(client, tenantA, request);
        UserContentWriteResponse admittedByB = await AdmitAsync(client, tenantB, request);

        Assert.Equal(admittedByA.FileId, admittedByB.FileId);
        Assert.Equal(admittedByA.ContentId, admittedByB.ContentId);
        Assert.Equal(admittedByA.MetadataId, admittedByB.MetadataId);
        Assert.Equal(admittedByA.ContentId, admittedByA.DocumentId);
        Assert.Equal(admittedByB.ContentId, admittedByB.DocumentId);
        AssertCanonicalIds(admittedByA);
        AssertCanonicalIds(admittedByB);
        Assert.Null(admittedByA.Modality);
        Assert.Null(admittedByB.Modality);

        byte[] fileId = Convert.FromHexString(admittedByA.FileId);
        await using (var conn = await pg.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            "SELECT first_observed_by FROM laplace.entities WHERE id = @file", conn))
        {
            command.Parameters.Add("file", NpgsqlDbType.Bytea).Value = fileId;
            byte[] firstObservedBy = Assert.IsType<byte[]>(await command.ExecuteScalarAsync());
            Assert.Equal(UserArtifactContent.Resolve(tenantA).Source.ToBytes(), firstObservedBy);
        }

        await using (var conn = await pg.DataSource.OpenConnectionAsync())
        {
            var children = await NpgsqlSubstrateReads.PackedTrajectoryVerticesAsync(
                conn, fileId, CancellationToken.None);
            Assert.Collection(
                children.OrderBy(static child => child.Ordinal),
                content => Assert.Equal(admittedByA.ContentId, content.ChildIdHex),
                metadata => Assert.Equal(admittedByA.MetadataId, metadata.ChildIdHex));

            await using var containers = new NpgsqlCommand(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM structural.containers_of(@content, 1, 32)
                    WHERE entity_id = @file
                )
                """, conn);
            containers.Parameters.Add("content", NpgsqlDbType.Bytea).Value =
                Convert.FromHexString(admittedByA.ContentId);
            containers.Parameters.Add("file", NpgsqlDbType.Bytea).Value = fileId;
            Assert.True(await containers.ExecuteScalarAsync() is true);
        }

        using var tenantBExport = await ExportAsync(client, tenantB, admittedByA.FileId);
        Assert.Equal(HttpStatusCode.OK, tenantBExport.StatusCode);
        UserContentExportResponse body = (await tenantBExport.Content
            .ReadFromJsonAsync<UserContentExportResponse>())!;
        Assert.Equal(admittedByA.FileId, body.FileId);
        Assert.Equal(admittedByA.ContentId, body.ContentId);
        Assert.Equal(admittedByA.ContentId, body.DocumentId);
        Assert.Equal(admittedByA.MetadataId, body.MetadataId);
        Assert.Equal(
            Convert.ToHexStringLower(UserArtifactContent.Resolve(tenantB).Source.ToBytes()),
            body.SourceId);
        AssertCanonicalIds(
            body.RequestedId,
            body.FileId!,
            body.DocumentId!,
            body.ContentId,
            body.MetadataId!,
            body.SourceId);
        Assert.Equal($"UserContent@{tenantB}", body.Source);
        Assert.Equal("shared.txt", body.Name);
        Assert.Equal("proof/shared.txt", body.Path);
        Assert.Equal("document", body.Kind);
        Assert.Null(body.Modality);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), body.Bytes);
        Assert.Equal(modifiedAt, body.ModifiedAt);
        Assert.Equal(text, body.Text);
        Assert.Equal(Encoding.UTF8.GetBytes(text), Convert.FromBase64String(body.ContentBase64));

        using var tenantCExport = await ExportAsync(client, tenantC, admittedByA.FileId);
        Assert.Equal(HttpStatusCode.NotFound, tenantCExport.StatusCode);
    }

    [Fact]
    public async Task MembershipRequiresAConfirmedClaimToAFileTypedEntity()
    {
        await using var factory = new UserContentEndpointFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        string suffix = Guid.NewGuid().ToString("N");
        string owner = $"membership-owner-{suffix}";
        var request = new UserTextArtifactWriteRequest(
            Name: "membership.txt",
            Path: "proof/membership.txt",
            Text: $"membership negative controls {suffix}",
            ContentBase64: null,
            UserId: null,
            ModifiedAt: DateTimeOffset.UnixEpoch);
        UserContentWriteResponse admitted = await AdmitAsync(client, owner, request);

        var writer = new NpgsqlSubstrateWriter(pg.DataSource);
        Hash128 fileId = Hash128.FromBytes(Convert.FromHexString(admitted.FileId));
        Hash128 contentId = Hash128.FromBytes(Convert.FromHexString(admitted.ContentId));

        string falseTypeTenant = $"membership-type-{suffix}";
        var falseTypeScope = UserArtifactContent.Resolve(falseTypeTenant);
        foreach (var bootstrap in UserArtifactContent.BuildTenantBootstrapChanges(falseTypeScope))
            await writer.ApplyAsync(bootstrap);
        var falseType = new SubstrateChangeBuilder(
            falseTypeScope.Source, "test/user-content/false-type");
        falseType.AddAttestation(NativeAttestation.Categorical(
            falseTypeScope.Source,
            UserArtifactContent.MembershipRelation,
            contentId,
            falseTypeScope.Source,
            contextId: null,
            SourceTrust.UserPrompt));
        await writer.ApplyAsync(falseType.Build());

        string refutedFileTenant = $"membership-refute-{suffix}";
        var refutedFileScope = UserArtifactContent.Resolve(refutedFileTenant);
        foreach (var bootstrap in UserArtifactContent.BuildTenantBootstrapChanges(refutedFileScope))
            await writer.ApplyAsync(bootstrap);
        var refutedFile = new SubstrateChangeBuilder(
            refutedFileScope.Source, "test/user-content/refuted-file");
        refutedFile.AddAttestation(NativeAttestation.Categorical(
            refutedFileScope.Source,
            UserArtifactContent.MembershipRelation,
            fileId,
            refutedFileScope.Source,
            contextId: null,
            SourceTrust.UserPrompt,
            confirm: false));
        await writer.ApplyAsync(refutedFile.Build());

        string refutedPromptTenant = $"prompt-refute-{suffix}";
        var refutedPromptScope = ConversationContent.Resolve(refutedPromptTenant);
        foreach (var bootstrap in ConversationContent.BuildTenantBootstrapChanges(refutedPromptScope))
            await writer.ApplyAsync(bootstrap);
        Hash128 sessionId = ConversationContent.SessionId(refutedPromptTenant, "refuted");
        var refutedPrompt = new SubstrateChangeBuilder(
            refutedPromptScope.PromptSource, "test/user-content/refuted-prompt")
            .AddEntity(
                sessionId,
                EntityTier.Document,
                ConversationContent.SessionType,
                refutedPromptScope.PromptSource);
        refutedPrompt.AddAttestation(NativeAttestation.Categorical(
            contentId,
            ConversationContent.MembershipRelation,
            sessionId,
            refutedPromptScope.PromptSource,
            sessionId,
            SourceTrust.UserPrompt,
            confirm: false));
        await writer.ApplyAsync(refutedPrompt.Build());

        using var falseTypeExport = await ExportAsync(client, falseTypeTenant, admitted.ContentId);
        Assert.Equal(HttpStatusCode.NotFound, falseTypeExport.StatusCode);
        using var refutedFileExport = await ExportAsync(client, refutedFileTenant, admitted.FileId);
        Assert.Equal(HttpStatusCode.NotFound, refutedFileExport.StatusCode);
        using var refutedPromptExport = await ExportAsync(client, refutedPromptTenant, admitted.ContentId);
        Assert.Equal(HttpStatusCode.NotFound, refutedPromptExport.StatusCode);
    }

    [Fact]
    public async Task LegacyReplayToken_ReturnsConflict_AndDoesNotJournalAcceptance()
    {
        await using var factory = new UserContentEndpointFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        string tenant = $"legacy-replay-{Guid.NewGuid():N}";
        const string text = "legacy token cannot prove this semantic payload";
        const string path = "proof/legacy-replay.txt";
        var request = new UserTextArtifactWriteRequest(
            Name: "legacy-replay.txt",
            Path: path,
            Text: text,
            ContentBase64: null,
            UserId: null,
            ModifiedAt: DateTimeOffset.UnixEpoch);
        var scope = UserArtifactContent.Resolve(tenant);
        CodepointPerfcache.LoadDefault();
        await using (var bootstrapWriter = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(pg.DataSource), pg.DataSource))
        {
            await bootstrapWriter.ApplyWorkingSetAsync(
                UserArtifactContent.BuildTenantBootstrapChanges(scope));
        }
        Assert.True(UserArtifactContent.TryBuildTextArtifactChange(
            scope,
            request.Name!,
            path,
            Encoding.UTF8.GetBytes(text),
            userKey: null,
            modifiedUtc: DateTime.UnixEpoch,
            out SubstrateChange change,
            out _));
        foreach (IntentStage stage in change.IntentStages) stage.Dispose();

        // The legacy token was computed before native stages were collected, so
        // reproduce the warmed content-bank state in which the endpoint composes.
        Assert.True(UserArtifactContent.TryBuildTextArtifactChange(
            scope,
            request.Name!,
            path,
            Encoding.UTF8.GetBytes(text),
            userKey: null,
            modifiedUtc: DateTime.UnixEpoch,
            out change,
            out _));

        await using (var seed = pg.DataSource.CreateCommand(
            "INSERT INTO laplace.ingest_flush_journal (working_set_id, source_id) VALUES ($1, $2)"))
        {
            // The closer submits a one-change list, whose legacy working-set
            // token is the digest of its member intent IDs.
            seed.Parameters.AddWithValue(
                NpgsqlDbType.Bytea,
                Hash128.Blake3(change.Metadata.IntentId.ToBytes()).ToBytes());
            seed.Parameters.AddWithValue(NpgsqlDbType.Bytea, scope.Source.ToBytes());
            await seed.ExecuteNonQueryAsync();
        }
        foreach (IntentStage stage in change.IntentStages) stage.Dispose();

        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/content/text")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Add(HeaderTenantResolver.TenantHeader, tenant);
        using HttpResponseMessage response = await client.SendAsync(message);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(
            "legacy_replay_requires_reconciliation",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        await using var accepted = pg.DataSource.CreateCommand("""
            SELECT count(*)
            FROM laplace.ingest_file_journal
            WHERE source_name = $1 AND file_label = $2 AND status = 'ok'
            """);
        accepted.Parameters.AddWithValue(NpgsqlDbType.Text, scope.SourceName);
        accepted.Parameters.AddWithValue(NpgsqlDbType.Text, path);
        Assert.Equal(0L, (long)(await accepted.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task FailedAtomicApply_RollsBackEvidence_AndLaterFileIsAccepted()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string tenant = $"failure-recovery-{suffix}";
        await using var closer = new ContentArtifactCloser(pg.DataSource);
        Assert.NotNull(await closer.CloseTextAsync(
            tenant,
            "warmup.txt",
            "proof/warmup.txt",
            Encoding.UTF8.GetBytes($"warmup {suffix}"),
            modifiedUtc: DateTime.UnixEpoch));

        var scope = UserArtifactContent.Resolve(tenant);
        byte[] failedBytes = Encoding.UTF8.GetBytes($"must roll back {suffix}");
        const string failedPath = "proof/fails-once.txt";
        FileIdentity failedFile = FileEntity.Resolve(
            failedBytes,
            new FileMetadata(
                "fails-once.txt", failedPath, failedBytes.Length, DateTime.UnixEpoch));
        AttestationRow membership = NativeAttestation.Categorical(
            scope.Source,
            UserArtifactContent.MembershipRelation,
            failedFile.FileId,
            scope.Source,
            contextId: null,
            RelationTypeRank.Associative * SourceTrust.UserPrompt * scope.TenantTrust);
        Hash128 membershipType = membership.TypeId;
        await new NpgsqlSubstrateWriter(pg.DataSource).ApplyAsync(
            new SubstrateChangeBuilder(scope.Source, "test/user-artifact/failure-seed")
                .AddAttestation(membership)
                .Build());

        await using (var install = pg.DataSource.CreateCommand("""
            CREATE FUNCTION public.reject_user_artifact_apply() RETURNS trigger
            LANGUAGE plpgsql AS 'BEGIN RAISE EXCEPTION ''injected user artifact apply failure''; END';
            CREATE TRIGGER reject_user_artifact_apply
            BEFORE INSERT OR UPDATE ON laplace.attestations
            FOR EACH ROW
            EXECUTE FUNCTION public.reject_user_artifact_apply();
            ALTER TABLE laplace.attestations
                ENABLE ALWAYS TRIGGER reject_user_artifact_apply
            """))
            await install.ExecuteNonQueryAsync();

        try
        {
            PostgresException failure = await Assert.ThrowsAsync<PostgresException>(() =>
                closer.CloseTextAsync(
                    tenant,
                    "fails-once.txt",
                    failedPath,
                    failedBytes,
                    modifiedUtc: DateTime.UnixEpoch));
            Assert.Contains("injected user artifact apply failure", failure.MessageText);
        }
        finally
        {
            await using var remove = pg.DataSource.CreateCommand("""
                DROP TRIGGER IF EXISTS reject_user_artifact_apply ON laplace.attestations;
                DROP FUNCTION IF EXISTS public.reject_user_artifact_apply()
                """);
            await remove.ExecuteNonQueryAsync();
        }

        await using (var rollback = pg.DataSource.CreateCommand("""
            SELECT
              (SELECT count(*) FROM laplace.attestations
               WHERE source_id = $1 AND type_id = $2 AND object_id = $3),
              (SELECT count(*) FROM laplace.consensus
               WHERE subject_id = $1 AND type_id = $2 AND object_id = $3),
              (SELECT count(*) FROM laplace.ingest_file_journal
               WHERE source_name = $4 AND file_label = $5 AND status = 'ok')
            """))
        {
            rollback.Parameters.AddWithValue(NpgsqlDbType.Bytea, scope.Source.ToBytes());
            rollback.Parameters.AddWithValue(NpgsqlDbType.Bytea, membershipType.ToBytes());
            rollback.Parameters.AddWithValue(NpgsqlDbType.Bytea, failedFile.FileId.ToBytes());
            rollback.Parameters.AddWithValue(NpgsqlDbType.Text, scope.SourceName);
            rollback.Parameters.AddWithValue(NpgsqlDbType.Text, failedPath);
            await using var rows = await rollback.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.Equal(1L, rows.GetInt64(0));
            Assert.Equal(0L, rows.GetInt64(1));
            Assert.Equal(0L, rows.GetInt64(2));
        }

        UserArtifactContent.ArtifactIds? recovered = await closer.CloseTextAsync(
            tenant,
            "after-failure.txt",
            "proof/after-failure.txt",
            Encoding.UTF8.GetBytes($"accepted after rollback {suffix}"),
            modifiedUtc: DateTime.UnixEpoch);
        Assert.NotNull(recovered);
    }

    private static void AssertCanonicalIds(UserContentWriteResponse response)
        => AssertCanonicalIds(
            response.FileId,
            response.DocumentId,
            response.ContentId,
            response.MetadataId,
            response.SourceId);

    private static void AssertCanonicalIds(params string[] ids)
    {
        foreach (string id in ids)
        {
            Assert.Equal(32, id.Length);
            Assert.Equal(id, id.ToLowerInvariant());
            Assert.Equal(16, Convert.FromHexString(id).Length);
        }
    }

    private static async Task<UserContentWriteResponse> AdmitAsync(
        HttpClient client,
        string tenant,
        UserTextArtifactWriteRequest body)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/content/text")
        {
            Content = JsonContent.Create(body)
        };
        message.Headers.Add(HeaderTenantResolver.TenantHeader, tenant);
        using var response = await client.SendAsync(message);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UserContentWriteResponse>())!;
    }

    private static async Task<UserContentWriteResponse> AdmitCodeAsync(
        HttpClient client,
        string tenant,
        UserCodeArtifactWriteRequest body)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/content/code")
        {
            Content = JsonContent.Create(body)
        };
        message.Headers.Add(HeaderTenantResolver.TenantHeader, tenant);
        using var response = await client.SendAsync(message);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UserContentWriteResponse>())!;
    }

    private static async Task<HttpResponseMessage> ExportAsync(
        HttpClient client,
        string tenant,
        string fileId)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, $"/v1/content/{fileId}");
        message.Headers.Add(HeaderTenantResolver.TenantHeader, tenant);
        return await client.SendAsync(message);
    }

    private sealed class UserContentEndpointFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.PostConfigure<LaplaceAuthOptions>(options => options.Mode = "header");
                services.PostConfigure<StripeBillingOptions>(options =>
                {
                    TestBillingOptions.IsolateFromHostStripe(options);
                    options.Bypass = true;
                });
            });
    }
}

public sealed class UserContentEndpointPgFixture : IAsyncLifetime
{
    private const string DatabaseName = "laplace_user_content_endpoint_test";
    private string? _originalConnectionString;
    private NpgsqlDataSource? _dataSource;

    public NpgsqlDataSource DataSource =>
        _dataSource ?? throw new InvalidOperationException("Fixture not initialized");

    public async Task InitializeAsync()
    {
        _originalConnectionString = Environment.GetEnvironmentVariable("LAPLACE_DB");
        var testConnection = new NpgsqlConnectionStringBuilder(LaplaceInstall.PostgresConnectionString())
        {
            Database = DatabaseName,
            Pooling = true
        };
        var adminConnection = new NpgsqlConnectionStringBuilder(testConnection.ConnectionString)
        {
            Database = "postgres",
            Pooling = false
        };

        await RecreateDatabaseAsync(adminConnection.ConnectionString);
        Environment.SetEnvironmentVariable("LAPLACE_DB", testConnection.ConnectionString);

        _dataSource = NpgsqlDataSource.Create(testConnection.ConnectionString);
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var command = conn.CreateCommand();
        command.CommandText = """
            CREATE EXTENSION IF NOT EXISTS postgis;
            CREATE EXTENSION IF NOT EXISTS laplace_geom;
            CREATE EXTENSION IF NOT EXISTS laplace_substrate;
            SET search_path TO laplace, public;
            """;
        await command.ExecuteNonQueryAsync();

        if (!LaplaceInstall.TryRepoRoot(out string root))
            throw new InvalidOperationException("Cannot locate repository root for readback SQL");
        string reconstructSql = Path.Combine(
            root,
            "extension",
            "laplace_substrate",
            "sql",
            "functions",
            "readback",
            "reconstruct_content.sql.in");
        string grammarSourceIdSql = Path.Combine(
            root,
            "extension",
            "laplace_substrate",
            "sql",
            "functions",
            "readback",
            "grammar_source_id.sql.in");
        string ingestFileJournalSql = Path.Combine(
            root,
            "extension",
            "laplace_substrate",
            "sql",
            "schema",
            "tables",
            "ingest_file_journal.sql.in");
        await ApplySqlFileAsync(testConnection, ingestFileJournalSql);
        await ApplySqlFileAsync(
            testConnection,
            grammarSourceIdSql,
            "laplace_substrate");
        await ApplySqlFileAsync(testConnection, reconstructSql);

        command.CommandText = """
            INSERT INTO laplace.entities (id, tier, type_id, first_observed_by)
            VALUES (laplace.word_id('☃'), 0, laplace.entity_type_id('Codepoint'), NULL)
            ON CONFLICT DO NOTHING
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
            _dataSource = null;
        }

        var testConnection = new NpgsqlConnectionStringBuilder(LaplaceInstall.PostgresConnectionString())
        {
            Database = DatabaseName,
            Pooling = false
        };
        var adminConnection = new NpgsqlConnectionStringBuilder(testConnection.ConnectionString)
        {
            Database = "postgres"
        };
        await DropDatabaseAsync(adminConnection.ConnectionString);
        Environment.SetEnvironmentVariable("LAPLACE_DB", _originalConnectionString);
    }

    private static async Task RecreateDatabaseAsync(string connectionString)
    {
        await DropDatabaseAsync(connectionString);
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {DatabaseName}", conn);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS {DatabaseName} WITH (FORCE)", conn);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ApplySqlFileAsync(
        NpgsqlConnectionStringBuilder connection,
        string sqlFile,
        string? modulePath = null)
    {
        string appliedSqlFile = sqlFile;
        string? temporarySqlFile = null;
        if (!string.IsNullOrWhiteSpace(modulePath))
        {
            temporarySqlFile = Path.Combine(
                Path.GetTempPath(),
                $"laplace-user-content-{Guid.NewGuid():N}.sql");
            string sql = await File.ReadAllTextAsync(sqlFile);
            sql = sql.Replace(
                "'MODULE_PATHNAME'",
                $"'{modulePath.Replace("'", "''", StringComparison.Ordinal)}'",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(temporarySqlFile, sql);
            appliedSqlFile = temporarySqlFile;
        }

        var start = new ProcessStartInfo
        {
            FileName = ResolvePgTool("psql"),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("--host");
        start.ArgumentList.Add(connection.Host!);
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(connection.Port.ToString());
        start.ArgumentList.Add("--username");
        start.ArgumentList.Add(connection.Username!);
        start.ArgumentList.Add("--dbname");
        start.ArgumentList.Add(connection.Database!);
        start.ArgumentList.Add("--set");
        start.ArgumentList.Add("ON_ERROR_STOP=1");
        start.ArgumentList.Add("--file");
        start.ArgumentList.Add(appliedSqlFile);
        if (!string.IsNullOrEmpty(connection.Password))
            start.Environment["PGPASSWORD"] = connection.Password;
        if (!string.IsNullOrWhiteSpace(connection.Options))
            start.Environment["PGOPTIONS"] = connection.Options;

        try
        {
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Failed to start psql");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                string stderr = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException(
                    $"psql failed to apply {Path.GetFileName(sqlFile)}: {stderr}");
            }
        }
        finally
        {
            if (temporarySqlFile is not null)
                File.Delete(temporarySqlFile);
        }
    }

    private static string ResolvePgTool(string program)
    {
        if (!OperatingSystem.IsWindows()) return program;
        const string pgBin = @"C:\Program Files\PostgreSQL\18\bin";
        string executable = Path.Combine(pgBin, program + ".exe");
        return File.Exists(executable) ? executable : program;
    }
}
