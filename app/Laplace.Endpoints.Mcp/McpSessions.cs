using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Laplace.Endpoints.Mcp;

internal sealed class McpSession(IMcpTools tools)
{
    public string Id { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public McpServer Server { get; } = new(tools);
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public bool Closed { get; private set; }
    private long _lastUse = Environment.TickCount64;
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
    private readonly SemaphoreSlim _creation = new(1, 1);

    public McpSession? Find(string id) => _sessions.TryGetValue(id, out var session)
        && !(session.Gate.CurrentCount > 0 && session.Expired(TimeSpan.FromMinutes(options.IdleMinutes))) ? session : null;

    public async Task<McpSession?> CreateAsync()
    {
        await _creation.WaitAsync();
        try
        {
            // Lazy expiration before allocation bounds both pooled DB connections
            // and native writer ownership, even if clients never send DELETE.
            foreach (var pair in _sessions)
                if (pair.Value.Expired(TimeSpan.FromMinutes(options.IdleMinutes)) && await pair.Value.Gate.WaitAsync(0))
                {
                    try
                    {
                        if (_sessions.TryRemove(pair.Key, out _)) await pair.Value.CloseAsync();
                    }
                    finally { pair.Value.Gate.Release(); }
                }
            if (_sessions.Count >= options.MaxSessions) return null;
            var session = new McpSession(factory());
            _sessions[session.Id] = session;
            return session;
        }
        finally { _creation.Release(); }
    }

    public async Task<bool> RemoveAsync(string id, CancellationToken ct)
    {
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
