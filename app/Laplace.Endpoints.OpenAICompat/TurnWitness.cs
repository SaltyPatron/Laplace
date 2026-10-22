using System.Text;
using System.Diagnostics;
using System.Threading.Channels;
using Serilog.Context;
using Serilog.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed class TurnWitness : BackgroundService, IConversationWitness
{
    private readonly SubstrateClient _substrate;
    private readonly ILogger<TurnWitness> _log;
    private readonly object _queueReadGate = new();
    private volatile bool _stopping;
    private volatile bool _online;
    private readonly Channel<TurnItem> _queue = Channel.CreateBounded<TurnItem>(
        new BoundedChannelOptions(4096)
        {
            // The worker consumes turns; shutdown may drain queued turns while
            // the active database operation still owns its connection.
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    /// <summary>
    /// One conversational turn with its provenance: the tenant selects the source identity,
    /// the session is the context entity on every attestation row, and the user is
    /// attributed to the session. A turn without a valid tenant and session is refused.
    /// </summary>
    private readonly record struct TurnItem(
        string Tenant, string? UserKey, Hash128 SessionId, string Prompt, string? Reply,
        TaskCompletionSource<TurnCommit>? Completion = null, string? OccurrenceKey = null,
        ConversationContent.TurnPhase Phase = ConversationContent.TurnPhase.Complete,
        ILogEventEnricher? Diagnostics = null, ActivityContext ParentTrace = default);

    private readonly record struct TurnCommit(bool Committed, Exception? Failure);

    private static TurnItem CaptureTurn(
        string tenant, string? userKey, Hash128 sessionId, string prompt, string? reply,
        TaskCompletionSource<TurnCommit>? completion = null, string? occurrenceKey = null,
        ConversationContent.TurnPhase phase = ConversationContent.TurnPhase.Complete)
        => new(tenant, userKey, sessionId, prompt, reply, completion, occurrenceKey, phase,
            LogContext.Clone(), Activity.Current?.Context ?? default);

    private static Activity TraceTurn(ActivityContext parent)
    {
        var activity = new Activity("laplace.turn.witness").SetIdFormat(ActivityIdFormat.W3C);
        if (parent.TraceId != default)
        {
            activity.SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags);
            activity.TraceStateString = parent.TraceState;
        }
        return activity.Start();
    }

    public bool IsOnline { get => _online; private set => _online = value; }

    public bool IsAvailable => IsOnline;

    public TurnWitness(SubstrateClient substrate, ILogger<TurnWitness> log)
    {
        _substrate = substrate;
        _log = log;
    }

    /// <summary>Enqueues without waiting; false when the writer is offline, the turn is invalid, or the queue is full.</summary>
    public bool TryEnqueueTurn(string tenant, string? userKey, Hash128 sessionId, string prompt, string? reply)
    {
        if (!IsOnline || string.IsNullOrEmpty(prompt) || sessionId == Hash128.Zero)
            return false;
        if (!ConversationContent.IsValidIdentifier(tenant))
            return false;
        return _queue.Writer.TryWrite(CaptureTurn(tenant, userKey, sessionId, prompt, reply));
    }

    public async Task RecordTurnAsync(
        string tenant, string? userKey, Hash128 sessionId, string prompt, string? reply,
        CancellationToken ct, string? occurrenceKey = null,
        ConversationContent.TurnPhase phase = ConversationContent.TurnPhase.Complete)
    {
        if (!IsOnline || string.IsNullOrEmpty(prompt) || sessionId == Hash128.Zero
            || !ConversationContent.IsValidIdentifier(tenant))
            throw new SubstrateUnavailableException("The conversation turn could not enter the witness writer.");
        var completion = new TaskCompletionSource<TurnCommit>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _queue.Writer.WriteAsync(CaptureTurn(
                tenant, userKey, sessionId, prompt, reply, completion, occurrenceKey, phase), ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException failure)
        {
            throw new SubstrateUnavailableException("The witness writer stopped before accepting the turn.", failure);
        }
        var result = await completion.Task.WaitAsync(ct).ConfigureAwait(false);
        if (!result.Committed)
            throw new SubstrateUnavailableException("The conversation turn did not commit to the substrate.",
                result.Failure ?? new InvalidOperationException("The witness writer returned without a commit receipt."));
    }

    public async Task<string> RecordPromptAsync(
        string tenant, string? userKey, Hash128 sessionId, string prompt, CancellationToken ct)
    {
        string occurrence = Guid.NewGuid().ToString("N");
        await RecordTurnAsync(tenant, userKey, sessionId, prompt, null, ct,
            occurrence, ConversationContent.TurnPhase.Input).ConfigureAwait(false);
        return occurrence;
    }

    public async Task<bool> RecordResponseAsync(
        string tenant, string? userKey, Hash128 sessionId, string prompt, string? reply,
        string occurrenceKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(reply)) return false;
        await RecordTurnAsync(tenant, userKey, sessionId, prompt, reply, ct,
            occurrenceKey, ConversationContent.TurnPhase.Output).ConfigureAwait(false);
        return true;
    }

    public void EnqueueTurn(string tenant, string? userKey, Hash128 sessionId, string prompt, string? reply)
    {
        if (!TryEnqueueTurn(tenant, userKey, sessionId, prompt, reply))
            _log.LogWarning("turn-witness rejected turn (lane offline or queue full)");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        IsOnline = false;
        _queue.Writer.TryComplete();
        // Resolve queued requests without waiting for an active connection's
        // handshake/rollback. The active owner is still joined by BackgroundService.
        Exception? diagnosticsFailure = null;
        try { AbandonPendingTurns(); }
        catch (Exception failure) { diagnosticsFailure = failure; }
        try { await base.StopAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception stopFailure) when (diagnosticsFailure is not null)
        {
            throw new AggregateException("Turn shutdown/diagnostics failed.", diagnosticsFailure, stopFailure);
        }
        if (diagnosticsFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(diagnosticsFailure).Throw();
    }

    private void AbandonPendingTurns()
    {
        List<Exception>? failures = null;
        while (true)
        {
            TurnItem abandoned;
            lock (_queueReadGate)
                if (!_queue.Reader.TryRead(out abandoned)) break;
            var failure = new InvalidOperationException("The witness writer stopped before committing the queued turn.");
            abandoned.Completion?.TrySetResult(new TurnCommit(false, failure));
            try
            {
                using var diagnostics = abandoned.Diagnostics is { } context ? LogContext.Push(context) : null;
                using var trace = TraceTurn(abandoned.ParentTrace);
                _log.LogWarning(failure, "turn-witness abandoned pending turn during shutdown");
            }
            catch (Exception loggingFailure) { (failures ??= []).Add(loggingFailure); }
        }
        if (failures is not null) throw new AggregateException("Turn shutdown diagnostics failed.", failures);
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
        if (_stopping) return;

        // Turn admission (compose, persist, fold, receipt) is the shared TurnCloser,
        // the same one MCP and the CLI use. This service adds the bounded
        // single-reader channel, so turns serialize and each turn is one apply, and
        // IsOnline, which callers check before recording.
        await using var closer = new TurnCloser(
            _substrate.DataSource, w => _log.LogWarning("turn-witness: {Warning}", w));
        IsOnline = true;
        _log.LogInformation("turn-witness online");

        try
        {
        while (await _queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            TurnItem item;
            lock (_queueReadGate)
            {
                if (_stopping || ct.IsCancellationRequested) return;
                if (!_queue.Reader.TryRead(out item)) continue;
            }
            using var diagnostics = item.Diagnostics is { } context ? LogContext.Push(context) : null;
            using var trace = TraceTurn(item.ParentTrace);
            bool deposited = false;
            Exception? depositFailure = null;
            try
            {
                // Each turn is its own witnessing event: content dedups by id, but a
                // repeated utterance folds again as another witness.
                deposited = await closer.CloseAsync(
                    item.Tenant, item.SessionId, item.Prompt, item.Reply, item.UserKey, ct,
                    item.OccurrenceKey, item.Phase);

                if (!deposited)
                {
                    depositFailure = closer.LastFailure;
                    if (ct.IsCancellationRequested)
                    {
                        _log.LogWarning(depositFailure, "turn-witness stopped while awaiting turn commit");
                        return;
                    }
                    if (depositFailure is { } failure)
                        _log.LogError(failure, "turn-witness could not commit turn");
                    else
                        _log.LogWarning("turn-witness could not commit turn");
                    continue;
                }

                _log.LogInformation("turn witnessed: tenant={Tenant} session={Session}",
                    item.Tenant, item.SessionId);
            }
            catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
            {
                depositFailure = ex;
                _log.LogWarning(ex, "turn-witness stopped while awaiting turn commit");
                IsOnline = false;
                return;
            }
            catch (Exception ex)
            {
                depositFailure = ex;
                _log.LogWarning(ex, "turn-witness deposit failed");
            }
            finally { item.Completion?.TrySetResult(new TurnCommit(deposited, depositFailure)); }
        }
        }
        finally
        {
            IsOnline = false;
            _queue.Writer.TryComplete();
            AbandonPendingTurns();
        }
    }

}
