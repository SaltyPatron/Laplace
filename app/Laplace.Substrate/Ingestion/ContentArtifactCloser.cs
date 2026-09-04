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

    public async Task<UserArtifactContent.ArtifactIds?> CloseTextAsync(
        string tenant,
        string name,
        string relativePath,
        byte[] utf8,
        string? userKey = null,
        DateTime? modifiedUtc = null,
        CancellationToken ct = default)
    {
        if (Broken || utf8.Length == 0 || !ConversationContent.IsValidIdentifier(tenant))
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
                await _writer.ApplyManyAsync(
                    UserArtifactContent.BuildTenantBootstrapChanges(scope), ct).ConfigureAwait(false);
                _scopes[tenant] = scope;
            }

            if (!UserArtifactContent.TryBuildTextArtifactChange(
                    scope,
                    name,
                    relativePath,
                    utf8,
                    userKey,
                    (modifiedUtc ?? DateTime.UtcNow).ToUniversalTime(),
                    out var change,
                    out var ids))
                return null;

            await _writer.ApplyAsync(change, ct).ConfigureAwait(false);
            return ids;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Broken = true;
            _warn?.Invoke($"user artifact deposit disabled: {ex.Message}");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null)
            await _writer.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
