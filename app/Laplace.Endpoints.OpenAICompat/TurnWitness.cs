using System.Text;
using System.Threading.Channels;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed class TurnWitness : BackgroundService
{
    private readonly SubstrateClient _substrate;
    private readonly ILogger<TurnWitness> _log;
    private readonly Channel<TurnItem> _queue = Channel.CreateBounded<TurnItem>(
        new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

    /// <summary>
    /// One conversational turn with its full provenance (spec 34): tenant → per-tenant
    /// source identity, session → context entity on every evidence row, user → session
    /// attribution. A turn without a tenant/session does not exist on this lane.
    /// </summary>
    private readonly record struct TurnItem(
        string Tenant, string? UserKey, Hash128 SessionId, string Prompt, string? Reply);

    public bool IsOnline { get; private set; }

    /// <summary>WebApplicationFactory golden tests: gate open before BackgroundService starts.</summary>
    internal bool TestForceAvailable { get; set; }

    public bool IsAvailable => TestForceAvailable || IsOnline;

    public TurnWitness(SubstrateClient substrate, ILogger<TurnWitness> log)
    {
        _substrate = substrate;
        _log = log;
    }

    /// <summary>Record-or-fail: returns false when witness lane is offline (caller → 503).</summary>
    public bool TryEnqueueTurn(string tenant, string? userKey, Hash128 sessionId, string prompt, string? reply)
    {
        if (!IsOnline || string.IsNullOrWhiteSpace(prompt) || sessionId == Hash128.Zero)
            return false;
        if (!ConversationContent.IsValidIdentifier(tenant))
            return false;
        return _queue.Writer.TryWrite(new TurnItem(tenant, userKey, sessionId, prompt.Trim(),
            string.IsNullOrWhiteSpace(reply) ? null : reply.Trim()));
    }

    public void EnqueueTurn(string tenant, string? userKey, Hash128 sessionId, string prompt, string? reply)
    {
        if (!TryEnqueueTurn(tenant, userKey, sessionId, prompt, reply))
            _log.LogWarning("turn-witness rejected turn (lane offline or queue full)");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            CodepointPerfcache.LoadDefault();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "turn-witness disabled: codepoint perf-cache unavailable");
            return;
        }

        // The accumulating wrapper is the load-bearing half: evidence lands AND the
        // Glicko delta folds into consensus per apply, so the deposited turn is
        // visible to the very next walk. A bare writer leaves testimony unfolded.
        var inner = new NpgsqlSubstrateWriter(_substrate.DataSource);
        await using var acc = new ConsensusAccumulatingWriter(inner, _substrate.DataSource);
        var writer = (ISubstrateWriter)acc;
        bool floorPresent = false;
        int consecutiveFailures = 0;
        // Per-process caches: bootstrap rows are idempotent, but testimony is not —
        // registering a tenant's sources once per process bounds the refold to
        // restarts (same class as every decomposer bootstrap). Session attribution
        // is once per session per process for the same reason.
        var scopes = new Dictionary<string, ConversationContent.TenantScope>(StringComparer.Ordinal);
        var attributedSessions = new HashSet<Hash128>();
        IsOnline = true;
        _log.LogInformation("turn-witness online");

        await foreach (var item in _queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                if (!floorPresent && !(floorPresent = await FloorPresentAsync(ct)))
                {
                    _log.LogWarning(
                        "substrate floor missing (no Codepoint entities); witness lane offline until unicode seed");
                    IsOnline = false;
                    return;
                }

                if (!scopes.TryGetValue(item.Tenant, out var scope))
                {
                    scope = ConversationContent.Resolve(item.Tenant);
                    foreach (var change in ConversationContent.BuildTenantBootstrapChanges(scope))
                        await writer.ApplyAsync(change, ct);
                    scopes[item.Tenant] = scope;
                    _log.LogInformation("tenant witness sources registered: {Tenant}", item.Tenant);
                }

                string? userKey = item.UserKey is not null && attributedSessions.Add(item.SessionId)
                    ? item.UserKey
                    : null;

                // Every turn is a distinct witnessing event: rows dedup by content
                // address, but the testimony folds again — a repeated utterance IS
                // another witness (chess parity: every play of a move counts).
                // One turn = one change = one apply (the writer's φ-per-cell
                // invariant; cross-tenant turns are never batched together).
                if (!ConversationContent.TryBuildTurnChange(
                        scope, item.SessionId,
                        Encoding.UTF8.GetBytes(item.Prompt),
                        item.Reply is { } r ? Encoding.UTF8.GetBytes(r) : null,
                        userKey,
                        out var turnChange, out var promptRoot, out var replyRoot))
                {
                    _log.LogWarning("turn-witness could not decompose turn; dropped");
                    continue;
                }

                await writer.ApplyAsync(turnChange, ct);
                _log.LogInformation(
                    "turn witnessed: tenant={Tenant} session={Session} prompt={PromptRoot} reply={ReplyRoot}",
                    item.Tenant, item.SessionId, promptRoot,
                    replyRoot == Hash128.Zero ? "(none)" : replyRoot);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                IsOnline = false;
                return;
            }
            catch (Exception ex)
            {
                if (++consecutiveFailures >= 8)
                {
                    IsOnline = false;
                    _log.LogError(ex, "turn-witness disabled after {Count} consecutive failures", consecutiveFailures);
                    return;
                }
                _log.LogWarning(ex, "turn-witness deposit failed; turn dropped");
            }
        }
    }

    private async Task<bool> FloorPresentAsync(CancellationToken ct)
    {
        await using var conn = await _substrate.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM laplace.entities WHERE type_id = @t LIMIT 1", conn);
        cmd.Parameters.AddWithValue("t", EntityTypeRegistry.Codepoint.ToBytes());
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}
