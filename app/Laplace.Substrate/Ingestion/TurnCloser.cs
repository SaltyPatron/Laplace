using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Laplace.Decomposers.Abstractions;
using Npgsql;

namespace Laplace.Ingestion;

/// <summary>
/// The OODA close: one turn deposited as witnessed content, folded inline, under
/// spec 34 provenance (tenant → source, session → context, user → attribution).
///
/// THE ONE IMPLEMENTATION. converse.chat()'s header states the close "happens at the
/// FRONTEND, not here: every caller (MCP chat tool, HTTP TurnWitness, CLI chat)
/// deposits the prompt and response through the writer spine." The payload was
/// already shared (<see cref="ConversationContent"/>); the SEQUENCE was not, and
/// each frontend re-derived it:
///
///   Tier-0 ROM -> writer -> tenant scope -> bootstrap -> build turn change -> apply
///
/// Every lane (HTTP, MCP, CLI) runs this one sequence, so each turn carries its
/// session, tenant and attribution under spec 34.
///
/// Caching is per instance and deliberate: bootstrap rows are idempotent but
/// TESTIMONY IS NOT (see the re-ingest guard law) — registering a tenant's sources
/// once per process bounds the refold to restarts. Each actual occurrence retains
/// its supplied participant; attribution never depends on a process-local set.
///
/// Not thread-safe by construction: a turn is one change and one apply (the
/// writer's φ-per-cell invariant assumes a turn is never batched with another
/// tenant's). Callers that accept concurrent turns serialize them — the HTTP lane
/// does it with a single-reader channel.
/// </summary>
public sealed class TurnCloser : IAsyncDisposable
{
    private readonly NpgsqlDataSource _db;
    private readonly Action<string>? _warn;
    private readonly Dictionary<string, ConversationContent.TenantScope> _scopes = new(StringComparer.Ordinal);
    private ConsensusAccumulatingWriter? _writer;

    /// <summary>
    /// Whether the most recent deposit failed. A later turn can retry the write
    /// lane; one transient failure must not permanently disable session learning.
    /// </summary>
    public bool Broken { get; private set; }

    public TurnCloser(NpgsqlDataSource db, Action<string>? warn = null)
    {
        _db = db;
        _warn = warn;
    }

    /// <summary>
    /// Deposit one turn. Returns false when the lane is offline or the turn produced
    /// no witnessable content; the caller decides whether that is fatal (the HTTP
    /// lane 503s on a record-or-fail contract, MCP and the CLI report and continue).
    /// </summary>
    public async Task<bool> CloseAsync(
        string tenant,
        Hash128 sessionId,
        string prompt,
        string? reply,
        string? userKey = null,
        CancellationToken ct = default,
        string? occurrenceKey = null,
        ConversationContent.TurnPhase phase = ConversationContent.TurnPhase.Complete)
    {
        if (string.IsNullOrEmpty(prompt) || sessionId == Hash128.Zero)
            return false;
        if (!ConversationContent.IsValidIdentifier(tenant))
            return false;

        try
        {
            // Tier-0 is the codepoint ROM, not PostgreSQL rows: every constituent
            // a turn composes resolves against the mapped table, and LoadDefault
            // refuses the lane when that table is missing or stale.
            if (_writer is null)
            {
                CodepointPerfcache.LoadDefault();
                _writer = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(_db), _db);
            }

            if (!_scopes.TryGetValue(tenant, out var scope))
            {
                scope = ConversationContent.Resolve(tenant);
                await _writer.ApplyManyAsync(
                    ConversationContent.BuildTenantBootstrapChanges(scope), ct);
                _scopes[tenant] = scope;
            }

            if (!ConversationContent.TryBuildTurnChange(
                    scope, sessionId,
                    System.Text.Encoding.UTF8.GetBytes(prompt),
                    string.IsNullOrEmpty(reply) ? null : System.Text.Encoding.UTF8.GetBytes(reply),
                    userKey,
                    out var turnChange, out _, out _, out var turns, occurrenceKey, userKey, phase))
                return false;

            // A conversational turn is complete only after its evidence and
            // consensus fold commit. Buffered ingestion apply can defer the fold
            // beyond the next prompt and cannot close a live conversation turn.
            await _writer.ApplyConversationTurnAsync(turnChange, sessionId, turns, ct);
            Broken = false;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Broken = true;
            _warn?.Invoke($"turn deposit failed: {ex.Message}");
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null)
            await _writer.DisposeAsync();
    }
}
