using System.Text;
using System.Threading.Channels;
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
    private readonly Channel<TurnItem> _queue = Channel.CreateBounded<TurnItem>(
        new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

    /// <summary>
    /// One conversational turn with its provenance: the tenant selects the source identity,
    /// the session is the context entity on every attestation row, and the user is
    /// attributed to the session. A turn without a valid tenant and session is refused.
    /// </summary>
    private readonly record struct TurnItem(
        string Tenant, string? UserKey, Hash128 SessionId, string Prompt, string? Reply,
        TaskCompletionSource<bool>? Completion = null, string? OccurrenceKey = null,
        ConversationContent.TurnPhase Phase = ConversationContent.TurnPhase.Complete);

    public bool IsOnline { get; private set; }

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
        await foreach (var item in _queue.Reader.ReadAllAsync(ct))
        {
            bool deposited = false;
            try
            {
                // Each turn is its own witnessing event: content dedups by id, but a
                // repeated utterance folds again as another witness.
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
