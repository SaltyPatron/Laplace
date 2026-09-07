using System.Text;
using System.Threading.Channels;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;
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
        string Tenant, string? UserKey, Hash128 SessionId, string Prompt, string? Reply,
        TaskCompletionSource<bool>? Completion = null, string? OccurrenceKey = null,
        ConversationContent.TurnPhase Phase = ConversationContent.TurnPhase.Complete);

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
        if (!IsOnline || string.IsNullOrEmpty(prompt) || sessionId == Hash128.Zero)
            return false;
        if (!ConversationContent.IsValidIdentifier(tenant))
            return false;
        return _queue.Writer.TryWrite(new TurnItem(tenant, userKey, sessionId, prompt, reply));
    }

    public async Task RecordTurnAsync(
        string tenant, string? userKey, Hash128 sessionId, string prompt, string? reply,
        CancellationToken ct, string? occurrenceKey = null,
        ConversationContent.TurnPhase phase = ConversationContent.TurnPhase.Complete)
    {
        if (!IsOnline || string.IsNullOrEmpty(prompt) || sessionId == Hash128.Zero
            || !ConversationContent.IsValidIdentifier(tenant))
            throw new SubstrateUnavailableException("The conversation turn could not enter the witness writer.");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(new TurnItem(
            tenant, userKey, sessionId, prompt, reply, completion, occurrenceKey, phase), ct).ConfigureAwait(false);
        if (!await completion.Task.WaitAsync(ct).ConfigureAwait(false))
            throw new SubstrateUnavailableException("The conversation turn did not commit to the substrate.");
    }

    public async Task<string> RecordPromptAsync(
        string tenant, string? userKey, Hash128 sessionId, string prompt, CancellationToken ct)
    {
        string occurrence = Guid.NewGuid().ToString("N");
        await RecordTurnAsync(tenant, userKey, sessionId, prompt, null, ct,
            occurrence, ConversationContent.TurnPhase.Input).ConfigureAwait(false);
        return occurrence;
    }

    public Task RecordResponseAsync(
        string tenant, string? userKey, Hash128 sessionId, string prompt, string? reply,
        string occurrenceKey, CancellationToken ct) =>
        string.IsNullOrEmpty(reply) ? Task.CompletedTask :
            RecordTurnAsync(tenant, userKey, sessionId, prompt, reply, ct,
                occurrenceKey, ConversationContent.TurnPhase.Output);

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

        // The close sequence — floor gate, accumulating writer, tenant scope cache,
        // bootstrap-once, attribute-once-per-session, build, apply — lives in the
        // shared TurnCloser (Laplace.Ingestion). This lane owned a private copy of
        // it; MCP owned another, and the CLI had a weaker third that skipped tenant
        // and session entirely. What stays HERE is what is genuinely this lane's:
        // the bounded single-reader channel (turns serialize, so one turn is one
        // apply), IsOnline for the record-or-fail 503 contract, and the
        // consecutive-failure trip.
        await using var closer = new TurnCloser(
            _substrate.DataSource, w => _log.LogWarning("turn-witness: {Warning}", w));
        IsOnline = true;
        _log.LogInformation("turn-witness online");

        try
        {
        await foreach (var item in _queue.Reader.ReadAllAsync(ct))
        {
            bool deposited = false;
            try
            {
                // Every turn is a distinct witnessing event: rows dedup by content
                // address, but the testimony folds again — a repeated utterance IS
                // another witness (chess parity: every play of a move counts).
                deposited = await closer.CloseAsync(
                    item.Tenant, item.SessionId, item.Prompt, item.Reply, item.UserKey, ct,
                    item.OccurrenceKey, item.Phase);

                if (!deposited)
                {
                    _log.LogWarning("turn-witness could not commit turn");
                    continue;
                }

                _log.LogInformation("turn witnessed: tenant={Tenant} session={Session}",
                    item.Tenant, item.SessionId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                IsOnline = false;
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "turn-witness deposit failed");
            }
            finally { item.Completion?.TrySetResult(deposited); }
        }
        }
        finally
        {
            IsOnline = false;
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out var abandoned))
                abandoned.Completion?.TrySetResult(false);
        }
    }

}
