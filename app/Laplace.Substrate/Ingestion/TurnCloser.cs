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
/// THE ONE IMPLEMENTATION. chat()'s header states the close "happens at the
/// FRONTEND, not here: every caller (MCP chat tool, HTTP TurnWitness, CLI chat)
/// deposits the prompt and response through the writer spine." The payload was
/// already shared (<see cref="ConversationContent"/>); the SEQUENCE was not, and
/// each frontend re-derived it:
///
///   floor check -> writer -> tenant scope -> bootstrap once -> attribute user
///   once per session -> build turn change -> apply
///
/// They had already diverged. Only the HTTP lane checked that the substrate floor
/// exists before depositing, so an MCP turn against a floorless database wrote
/// testimony with nothing to anchor it. The CLI did not use this lane at all — it
/// deposited through the plain untenanted UserPrompt/Response sources, so no CLI
/// turn carried a session, a tenant, or attribution, and spec 34 simply did not
/// apply to it.
///
/// Caching is per instance and deliberate: bootstrap rows are idempotent but
/// TESTIMONY IS NOT (see the re-ingest guard law) — registering a tenant's sources
/// once per process bounds the refold to restarts, and session attribution is once
/// per session for the same reason.
///
/// Not thread-safe by construction: a turn is one change and one apply (the
/// writer's φ-per-cell invariant assumes a turn is never batched with another
/// tenant's). Callers that accept concurrent turns serialize them — the HTTP lane
/// does it with a single-reader channel.
/// </summary>
public sealed class TurnCloser : IAsyncDisposable
{
    private readonly NpgsqlDataSource _db;
    private readonly ISubstrateReader _reader;
    private readonly Action<string>? _warn;
    private readonly Dictionary<string, ConversationContent.TenantScope> _scopes = new(StringComparer.Ordinal);
    private readonly HashSet<Hash128> _attributed = [];
    private ConsensusAccumulatingWriter? _writer;
    private bool _floorPresent;

    /// <summary>
    /// True once a deposit has failed hard. The reply still flows to the caller —
    /// a missing deposit is reported, never hidden, and never fails the turn — but
    /// the lane stops retrying so a broken writer does not cost every subsequent
    /// turn a connection attempt.
    /// </summary>
    public bool Broken { get; private set; }

    public TurnCloser(NpgsqlDataSource db, Action<string>? warn = null)
    {
        _db = db;
        _reader = new NpgsqlSubstrateReader(db);
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
        CancellationToken ct = default)
    {
        if (Broken || string.IsNullOrWhiteSpace(prompt) || sessionId == Hash128.Zero)
            return false;
        if (!ConversationContent.IsValidIdentifier(tenant))
            return false;

        try
        {
            if (_writer is null)
            {
                CodepointPerfcache.LoadDefault();
                _writer = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(_db), _db);
            }

            // The floor gate, formerly only on the HTTP lane. Depositing into a
            // database with no Codepoint entities mints content whose tier-0
            // constituents do not exist — testimony anchored to nothing.
            if (!_floorPresent)
            {
                _floorPresent = await FloorPresentAsync(ct);
                if (!_floorPresent)
                {
                    _warn?.Invoke("substrate floor missing (no Codepoint entities); turn not deposited");
                    return false;
                }
            }

            if (!_scopes.TryGetValue(tenant, out var scope))
            {
                scope = ConversationContent.Resolve(tenant);
                foreach (var change in ConversationContent.BuildTenantBootstrapChanges(scope))
                    await _writer.ApplyAsync(change, ct);
                _scopes[tenant] = scope;
            }

            // Attribution is once per session per process: the edge is idempotent by
            // content address, but re-emitting it refolds the testimony every turn.
            string? attributeAs = userKey is not null && _attributed.Add(sessionId) ? userKey : null;

            if (!ConversationContent.TryBuildTurnChange(
                    scope, sessionId,
                    System.Text.Encoding.UTF8.GetBytes(prompt.Trim()),
                    string.IsNullOrWhiteSpace(reply) ? null : System.Text.Encoding.UTF8.GetBytes(reply.Trim()),
                    attributeAs,
                    out var turnChange, out _, out _))
                return false;

            await _writer.ApplyAsync(turnChange, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Broken = true;
            _warn?.Invoke($"turn deposit disabled: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The substrate floor: tier-0 codepoints exist, so minted content has
    /// constituents to anchor to. Reads through the shared reader surface — this
    /// was a hand-written `SELECT 1 FROM laplace.entities WHERE type_id = @t` in
    /// TurnWitness, which the read-path gate correctly flags: a consumer that
    /// hand-writes a query gives every other caller a reason to write their own.
    /// </summary>
    private async Task<bool> FloorPresentAsync(CancellationToken ct) =>
        await _reader.CountEntitiesByTypeAsync(EntityTypeRegistry.Codepoint, ct) > 0;

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null)
            await _writer.DisposeAsync();
    }
}
