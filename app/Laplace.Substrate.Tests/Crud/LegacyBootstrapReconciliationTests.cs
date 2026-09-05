using System.Diagnostics;
using System.Collections.Immutable;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Abstractions.Tests;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class LegacyBootstrapReconciliationTests : IAsyncLifetime
{
    private readonly LocalPgFixture _pg;

    public LegacyBootstrapReconciliationTests(LocalPgFixture pg) => _pg = pg;

    public async Task InitializeAsync()
    {
        string schema = Path.Combine(
            TypeIdLawTests.FindRepoRootPublic(),
            "extension", "laplace_substrate", "sql", "schema", "tables",
            "ingest_flush_journal.sql.in");
        await ApplySqlFileAsync(schema);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CompleteLegacyBootstrap_SeedsV2ReceiptWithoutChangingEvidenceOrStanding()
    {
        var scope = UserArtifactContent.Resolve($"legacy-complete-{Guid.NewGuid():N}");
        SubstrateChange[] bootstrap = UserArtifactContent.BuildTenantBootstrapChanges(scope);
        Hash128 marker = Marker(bootstrap, scope);

        await using (var legacy = NewWriter())
            await legacy.ApplyManyAsync(bootstrap);

        string before = await DurableStateAsync(scope.Source);
        Assert.Equal(0, await ReceiptCountAsync(scope.Source));

        await using (var upgraded = NewWriter())
        {
            bootstrap = UserArtifactContent.BuildTenantBootstrapChanges(scope);
            marker = Marker(bootstrap, scope);
            ApplyResult reconciled = await upgraded.ApplyLegacyBootstrapWorkingSetAsync(
                bootstrap, marker);
            Assert.True(reconciled.JournalReplayHit);
            Assert.Equal(before, await DurableStateAsync(scope.Source));
            Assert.Equal("reconciled-existing", await ReceiptKindAsync(scope.Source));

            SubstrateChange[] retry = UserArtifactContent.BuildTenantBootstrapChanges(scope);
            ApplyResult replay = await upgraded.ApplyLegacyBootstrapWorkingSetAsync(
                retry, Marker(retry, scope));
            Assert.True(replay.JournalReplayHit);
            Assert.Equal(before, await DurableStateAsync(scope.Source));
        }
    }

    [Fact]
    public async Task PartialLegacyBootstrap_IsRejectedWithoutReceiptOrAdditionalEvidence()
    {
        var scope = UserArtifactContent.Resolve($"legacy-partial-{Guid.NewGuid():N}");
        SubstrateChange[] bootstrap = UserArtifactContent.BuildTenantBootstrapChanges(scope);
        Hash128 marker = Marker(bootstrap, scope);

        await using (var legacy = NewWriter())
            await legacy.ApplyManyAsync(new[] { bootstrap[1] });

        string before = await DurableStateAsync(scope.Source);
        bootstrap = UserArtifactContent.BuildTenantBootstrapChanges(scope);
        marker = Marker(bootstrap, scope);
        await using var upgraded = NewWriter();
        await Assert.ThrowsAsync<LegacyBootstrapReconciliationException>(
            () => upgraded.ApplyLegacyBootstrapWorkingSetAsync(bootstrap, marker));
        Assert.Equal(before, await DurableStateAsync(scope.Source));
        Assert.Equal(0, await ReceiptCountAsync(scope.Source));
    }

    [Fact]
    public async Task DisjointHistoricalArtifact_AllowsBootstrapReceiptWithoutChangingState()
    {
        var scope = UserArtifactContent.Resolve($"legacy-mismatch-{Guid.NewGuid():N}");
        SubstrateChange[] bootstrap = UserArtifactContent.BuildTenantBootstrapChanges(scope);
        Hash128 marker = Marker(bootstrap, scope);
        Assert.True(UserArtifactContent.TryBuildTextArtifactChange(
            scope, "legacy.txt", "legacy.txt", "historical payload"u8.ToArray(),
            userKey: null, DateTime.UnixEpoch,
            out SubstrateChange historicalArtifact, out _));

        await using (var legacy = NewWriter())
            await legacy.ApplyManyAsync(bootstrap.Append(historicalArtifact).ToArray());

        string before = await DurableStateAsync(scope.Source);
        bootstrap = UserArtifactContent.BuildTenantBootstrapChanges(scope);
        marker = Marker(bootstrap, scope);

        await using var upgraded = NewWriter();
        ApplyResult reconciled = await upgraded.ApplyLegacyBootstrapWorkingSetAsync(
            bootstrap, marker);
        Assert.True(reconciled.JournalReplayHit);
        Assert.Equal(before, await DurableStateAsync(scope.Source));
        Assert.Equal("reconciled-existing", await ReceiptKindAsync(scope.Source));
    }

    [Fact]
    public async Task DisjointArtifactPlusOverlappingBootstrapCell_IsRejectedWithoutStateChange()
    {
        var scope = UserArtifactContent.Resolve($"legacy-overlap-{Guid.NewGuid():N}");
        SubstrateChange[] bootstrap = UserArtifactContent.BuildTenantBootstrapChanges(scope);
        Hash128 marker = Marker(bootstrap, scope);
        AttestationRow bootstrapMarker = bootstrap
            .SelectMany(static change => change.Attestations)
            .Single(row => row.Id == marker);
        AttestationRow overlapping = NativeAttestation.CategoricalResolved(
            bootstrapMarker.SubjectId,
            bootstrapMarker.TypeId,
            bootstrapMarker.ObjectId,
            bootstrapMarker.SourceId,
            Hash128.OfCanonical($"legacy-overlap/context/{Guid.NewGuid():N}"),
            SourceTrust.SubstrateMandate);
        var overlapChange = new SubstrateChangeBuilder(scope.Source, "legacy-overlap")
            .AddAttestation(overlapping)
            .Build();
        Assert.True(UserArtifactContent.TryBuildTextArtifactChange(
            scope, "legacy.txt", "legacy.txt", "historical payload"u8.ToArray(),
            userKey: null, DateTime.UnixEpoch,
            out SubstrateChange disjointArtifact, out _));

        await using (var legacy = NewWriter())
            await legacy.ApplyManyAsync(
                bootstrap.Append(disjointArtifact).Append(overlapChange).ToArray());

        string before = await DurableStateAsync(scope.Source);
        bootstrap = UserArtifactContent.BuildTenantBootstrapChanges(scope);
        marker = Marker(bootstrap, scope);
        await using var upgraded = NewWriter();
        await Assert.ThrowsAsync<LegacyBootstrapReconciliationException>(
            () => upgraded.ApplyLegacyBootstrapWorkingSetAsync(bootstrap, marker));
        Assert.Equal(before, await DurableStateAsync(scope.Source));
        Assert.Equal(0, await ReceiptCountAsync(scope.Source));
    }

    [Fact]
    public async Task BootstrapPayloadMismatch_IsRejectedWithoutStateChange()
    {
        var scope = UserArtifactContent.Resolve($"legacy-payload-mismatch-{Guid.NewGuid():N}");
        SubstrateChange[] bootstrap = UserArtifactContent.BuildTenantBootstrapChanges(scope);
        Hash128 marker = Marker(bootstrap, scope);
        bootstrap[1] = bootstrap[1] with
        {
            Attestations = bootstrap[1].Attestations
                .Select(row => row.Id == marker
                    ? row with { OpponentRdFp1e9 = row.OpponentRdFp1e9 + 1 }
                    : row)
                .ToImmutableArray()
        };
        await using (var legacy = NewWriter())
            await legacy.ApplyManyAsync(bootstrap);

        string before = await DurableStateAsync(scope.Source);
        bootstrap = UserArtifactContent.BuildTenantBootstrapChanges(scope);
        marker = Marker(bootstrap, scope);
        await using var upgraded = NewWriter();
        await Assert.ThrowsAsync<LegacyBootstrapReconciliationException>(
            () => upgraded.ApplyLegacyBootstrapWorkingSetAsync(bootstrap, marker));
        Assert.Equal(before, await DurableStateAsync(scope.Source));
        Assert.Equal(0, await ReceiptCountAsync(scope.Source));
    }

    private ConsensusAccumulatingWriter NewWriter() => new(
        new NpgsqlSubstrateWriter(_pg.DataSource), _pg.DataSource);

    private static Hash128 Marker(
        IReadOnlyList<SubstrateChange> bootstrap,
        UserArtifactContent.TenantScope scope)
    {
        Hash128 attribution = RelationTypeRegistry.RelationTypeId(
            UserArtifactContent.AttributionRelation);
        return bootstrap.SelectMany(static change => change.Attestations)
            .Single(row => row.SubjectId == scope.Source
                && row.TypeId == attribution && row.SourceId == scope.Source).Id;
    }

    private async Task<long> ReceiptCountAsync(Hash128 source)
    {
        await using var command = _pg.DataSource.CreateCommand(
            "SELECT count(*) FROM laplace.ingest_flush_journal WHERE source_id = $1");
        command.Parameters.AddWithValue(source.ToBytes());
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> ReceiptKindAsync(Hash128 source)
    {
        await using var command = _pg.DataSource.CreateCommand(
            "SELECT receipt_kind FROM laplace.ingest_flush_journal WHERE source_id = $1");
        command.Parameters.AddWithValue(source.ToBytes());
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> DurableStateAsync(Hash128 source)
    {
        await using var command = _pg.DataSource.CreateCommand("""
            SELECT jsonb_build_object(
              'evidence', coalesce((
                SELECT jsonb_agg(jsonb_build_array(
                    encode(id, 'hex'), observation_count, sum_score_fp1e9,
                    extract(epoch FROM last_observed_at)) ORDER BY id)
                FROM laplace.attestations WHERE source_id = $1), '[]'::jsonb),
              'consensus', coalesce((
                SELECT jsonb_agg(jsonb_build_array(
                    encode(c.subject_id, 'hex'), encode(c.type_id, 'hex'),
                    coalesce(encode(c.object_id, 'hex'), ''), c.witness_count,
                    c.rating, c.rd, extract(epoch FROM c.last_observed_at))
                  ORDER BY c.subject_id, c.type_id, c.object_id)
                FROM laplace.consensus c
                WHERE EXISTS (SELECT 1 FROM laplace.attestations a
                              WHERE a.source_id = $1
                                AND a.subject_id = c.subject_id
                                AND a.type_id = c.type_id
                                AND a.object_id IS NOT DISTINCT FROM c.object_id)), '[]'::jsonb))::text
            """);
        command.Parameters.AddWithValue(source.ToBytes());
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task ApplySqlFileAsync(string sqlFile)
    {
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows()
                ? @"C:\Program Files\PostgreSQL\18\bin\psql.exe"
                : "psql",
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
        start.ArgumentList.Add("--command");
        start.ArgumentList.Add("SET search_path TO laplace, public");
        start.ArgumentList.Add("--file");
        start.ArgumentList.Add(sqlFile);
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
                $"psql receipt schema setup exited {process.ExitCode}: {stderr.Result}\n{stdout.Result}");
    }
}
