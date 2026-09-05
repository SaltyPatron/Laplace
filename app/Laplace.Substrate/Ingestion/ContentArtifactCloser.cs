using System.Diagnostics;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Ingestion;

/// <summary>
/// Runtime close for standard user-owned text artifacts. This is deliberately separate
/// from corpus ingestion and from conversation turns: user files witness the same global
/// content ids, but under <c>UserContent@tenant</c> rather than a seeded source or
/// <c>UserPrompt@tenant</c>.
/// </summary>
public sealed class ContentArtifactCloser : IAsyncDisposable
{
    private readonly NpgsqlDataSource _db;
    private readonly ISubstrateReader _reader;
    private readonly Action<string>? _warn;
    private readonly Dictionary<string, UserArtifactContent.TenantScope> _scopes =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConsensusAccumulatingWriter? _writer;
    private bool _floorPresent;

    public ContentArtifactCloser(NpgsqlDataSource db, Action<string>? warn = null)
    {
        _db = db;
        _reader = new NpgsqlSubstrateReader(db);
        _warn = warn;
    }

    public bool Broken { get; private set; }

    private readonly record struct ArtifactBuild(
        SubstrateChange Change,
        UserArtifactContent.ArtifactIds Ids,
        string FileLabel);

    public Task<UserArtifactContent.ArtifactIds?> CloseTextAsync(
        string tenant,
        string name,
        string relativePath,
        byte[] utf8,
        string? userKey = null,
        DateTime? modifiedUtc = null,
        CancellationToken ct = default)
        => CloseArtifactAsync(
            tenant,
            utf8,
            modifiedUtc,
            (scope, observedModifiedUtc) =>
            {
                if (!UserArtifactContent.TryBuildTextArtifactChange(
                        scope,
                        name,
                        relativePath,
                        utf8,
                        userKey,
                        observedModifiedUtc,
                        out var change,
                        out var ids))
                    return null;
                return new ArtifactBuild(
                    change,
                    ids,
                    UserArtifactContent.NormalizeRelativePath(relativePath));
            },
            ct);

    public Task<UserArtifactContent.ArtifactIds?> CloseCodeAsync(
        string tenant,
        string name,
        string relativePath,
        byte[] utf8,
        string modality,
        string? userKey = null,
        DateTime? modifiedUtc = null,
        CancellationToken ct = default)
    {
        if (GrammarDecomposer.LookupById(modality) == IntPtr.Zero)
            throw new ArgumentException($"grammar modality '{modality}' is not registered", nameof(modality));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("artifact name is required", nameof(name));
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("artifact relative path is required", nameof(relativePath));

        return CloseArtifactAsync(
            tenant,
            utf8,
            modifiedUtc,
            (scope, observedModifiedUtc) => BuildCodeArtifact(
                scope, name, relativePath, utf8, modality, userKey, observedModifiedUtc),
            ct);
    }

    private async Task<UserArtifactContent.ArtifactIds?> CloseArtifactAsync(
        string tenant,
        byte[] utf8,
        DateTime? modifiedUtc,
        Func<UserArtifactContent.TenantScope, DateTime?, ArtifactBuild?> build,
        CancellationToken ct)
    {
        if (utf8.Length == 0 || !ConversationContent.IsValidIdentifier(tenant))
            return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_writer is null)
            {
                CodepointPerfcache.LoadDefault();
                _writer = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(_db), _db);
            }

            if (!_floorPresent)
            {
                _floorPresent = await _reader.CountEntitiesByTypeAsync(EntityTypeRegistry.Codepoint, ct) > 0;
                if (!_floorPresent)
                {
                    _warn?.Invoke("substrate floor missing (no Codepoint entities); user artifact not deposited");
                    return null;
                }
            }

            if (!_scopes.TryGetValue(tenant, out var scope))
            {
                scope = UserArtifactContent.Resolve(tenant);
                SubstrateChange[] bootstrap = UserArtifactContent.BuildTenantBootstrapChanges(scope);
                Hash128 attributionType = RelationTypeRegistry.RelationTypeId(
                    UserArtifactContent.AttributionRelation);
                AttestationRow marker = bootstrap
                    .SelectMany(static change => change.Attestations)
                    .Single(attestation =>
                        attestation.SubjectId == scope.Source
                        && attestation.TypeId == attributionType
                        && attestation.SourceId == scope.Source);
                await _writer.ApplyLegacyBootstrapWorkingSetAsync(
                    bootstrap, marker.Id, ct).ConfigureAwait(false);
                _scopes[tenant] = scope;
            }

            DateTime? observedModifiedUtc = modifiedUtc?.ToUniversalTime();
            ArtifactBuild? artifact = build(scope, observedModifiedUtc);
            if (artifact is not { } value) return null;

            var clock = Stopwatch.StartNew();
            ApplyResult applied = await _writer.ApplyWorkingSetAsync(value.Change, ct).ConfigureAwait(false);
            clock.Stop();
            await new NpgsqlIngestObservability(_db).RecordAcceptedArtifactAsync(
                scope.SourceName,
                scope.Source,
                value.FileLabel,
                value.Ids.FileId,
                utf8.LongLength,
                observedModifiedUtc is { } timestamp ? new DateTimeOffset(timestamp) : null,
                applied,
                clock.Elapsed,
                ct).ConfigureAwait(false);
            return value.Ids;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (LegacyReplayRequiresReconciliationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed apply rolls back its journal/evidence/fold transaction.
            // Retire this writer because a failed fold lane can remain poisoned,
            // but do not disable unrelated later files handled by this closer.
            var failedWriter = _writer;
            _writer = null;
            Broken = false;
            if (failedWriter is not null)
            {
                try
                {
                    await failedWriter.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception disposeError)
                {
                    _warn?.Invoke($"user artifact writer reset failed: {disposeError.Message}");
                }
            }
            _warn?.Invoke($"user artifact deposit failed: {ex.Message}");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ArtifactBuild BuildCodeArtifact(
        UserArtifactContent.TenantScope scope,
        string name,
        string relativePath,
        byte[] utf8,
        string modality,
        string? userKey,
        DateTime? observedModifiedUtc)
    {
        var metadata = new FileMetadata(
            name,
            UserArtifactContent.NormalizeRelativePath(relativePath),
            utf8.LongLength,
            observedModifiedUtc ?? DateTime.UnixEpoch,
            modality);
        var builder = new SubstrateChangeBuilder(
            scope.Source,
            $"user-content/{scope.Tenant}/{metadata.RelativePath}",
            parentIntentId: null);

        using var ast = GrammarDecomposer.Parse(utf8, modality);
        using var composer = new GrammarRowComposer(
            utf8, ast, scope.Source, modality, GrammarCompositionMode.FullSource);
        OrderedCompositionComponent contentRoot = composer.RootComponent();
        if (composer.DrainInto(builder, SourceTrust.UserPrompt) != contentRoot.Id)
            throw new InvalidOperationException("grammar content identity changed during compose");
        GrammarTagWitness.Emit(
            builder,
            utf8,
            ast,
            composer,
            modality,
            scope.Source,
            SourceTrust.UserPrompt * scope.TenantTrust);
        FileIdentity file = FileEntity.Emit(builder, scope.Source, contentRoot, metadata);

        double weight = RelationTypeRank.Associative * SourceTrust.UserPrompt * scope.TenantTrust;
        builder.AddAttestation(NativeAttestation.Categorical(
            scope.Source,
            UserArtifactContent.MembershipRelation,
            file.FileId,
            scope.Source,
            contextId: null,
            weight));

        if (!string.IsNullOrWhiteSpace(userKey))
        {
            if (!ConversationContent.IsValidIdentifier(userKey))
                throw new ArgumentException($"user key '{userKey}' is not a valid identifier", nameof(userKey));
            if (ContentEmitter.Emit(builder, userKey, scope.Source) is { } userRoot)
                builder.AddAttestation(NativeAttestation.Categorical(
                    file.FileId,
                    UserArtifactContent.AttributionRelation,
                    userRoot,
                    scope.Source,
                    contextId: null,
                    weight));
        }

        var ids = new UserArtifactContent.ArtifactIds(
            file.FileId,
            contentRoot.Id,
            contentRoot.Id,
            file.MetadataRootId,
            scope.Source);
        return new ArtifactBuild(builder.Build(), ids, metadata.RelativePath);
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null)
            await _writer.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
