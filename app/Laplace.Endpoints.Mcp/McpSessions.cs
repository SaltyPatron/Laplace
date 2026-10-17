using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Laplace.Endpoints.Mcp;

internal sealed class McpSession(IMcpTools tools, string? id = null)
{
    public string Id { get; } = !string.IsNullOrWhiteSpace(id)
        ? id
        : Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public McpServer Server { get; } = new(tools);
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public bool Closed { get; private set; }
    private long _lastUse = Environment.TickCount64;
    public long LastUseTicks => Interlocked.Read(ref _lastUse);
    public bool Expired(TimeSpan idle) => Environment.TickCount64 - Interlocked.Read(ref _lastUse) > idle.TotalMilliseconds;
    public void Touch() => Interlocked.Exchange(ref _lastUse, Environment.TickCount64);
    public async ValueTask CloseAsync()
    {
        if (Closed) return;
        Closed = true;
        await tools.DisposeAsync();
    }
}

internal sealed class McpSessions(McpHttpOptions options, Func<IMcpTools> factory) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, McpSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _removed = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _creation = new(1, 1);

    public McpSession? Find(string id) => _sessions.TryGetValue(id, out var session)
        && !(session.Gate.CurrentCount > 0 && session.Expired(TimeSpan.FromMinutes(options.IdleMinutes))) ? session : null;

    public async Task<McpSession?> CreateAsync(string? requestedId = null, CancellationToken ct = default)
    {
        await _creation.WaitAsync(ct);
        try
        {
            // Lazy expiration before allocation bounds both pooled DB connections
            // and native writer ownership, even if clients never send DELETE.
            foreach (var pair in _sessions)
                if (pair.Value.Expired(TimeSpan.FromMinutes(options.IdleMinutes)) && await pair.Value.Gate.WaitAsync(0, ct))
                {
                    try
                    {
                        if (_sessions.TryRemove(pair.Key, out _)) await pair.Value.CloseAsync();
                    }
                    finally { pair.Value.Gate.Release(); }
                }
            if (requestedId is not null && _sessions.TryGetValue(requestedId, out var existing))
            {
                existing.Touch();
                return existing;
            }
            if (_sessions.Count >= options.MaxSessions)
            {
                // When rehydrating a valid session after recycle and at capacity, evict the oldest idle session.
                if (requestedId is not null)
                {
                    var oldest = _sessions.OrderBy(p => p.Value.LastUseTicks).FirstOrDefault();
                    if (oldest.Key is not null && await oldest.Value.Gate.WaitAsync(0, ct))
                    {
                        try
                        {
                            if (_sessions.TryRemove(oldest.Key, out _)) await oldest.Value.CloseAsync();
                        }
                        finally { oldest.Value.Gate.Release(); }
                    }
                }
                if (_sessions.Count >= options.MaxSessions) return null;
            }
            var session = new McpSession(factory(), requestedId);
            _removed.TryRemove(session.Id, out _);
            _sessions[session.Id] = session;
            return session;
        }
        finally { _creation.Release(); }
    }

    /// <summary>
    /// Finds an active session or transparently re-hydrates a previously issued session ID
    /// (for example after an IIS app-pool recycle), unless the session was explicitly deleted.
    /// </summary>
    public async Task<McpSession?> FindOrCreateAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || _removed.ContainsKey(id))
            return null;
        if (Find(id) is { } active)
        {
            active.Touch();
            return active;
        }
        return await CreateAsync(id, ct);
    }

    public async Task<bool> RemoveAsync(string id, CancellationToken ct)
    {
        _removed[id] = 1;
        if (!_sessions.TryGetValue(id, out var session)) return false;
        await session.Gate.WaitAsync(ct);
        try
        {
            if (!_sessions.TryRemove(id, out _)) return false;
            await session.CloseAsync();
            return true;
        }
        finally { session.Gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var key in _sessions.Keys) await RemoveAsync(key, CancellationToken.None);
        _creation.Dispose();
    }
}
