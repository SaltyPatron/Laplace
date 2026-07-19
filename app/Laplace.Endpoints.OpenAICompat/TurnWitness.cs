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

    private readonly record struct TurnItem(string Prompt, string? Reply);

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
    public bool TryEnqueueTurn(string prompt, string? reply)
    {
        if (!IsOnline || string.IsNullOrWhiteSpace(prompt))
            return false;
        return _queue.Writer.TryWrite(new TurnItem(prompt.Trim(),
            string.IsNullOrWhiteSpace(reply) ? null : reply.Trim()));
    }

    public void EnqueueTurn(string prompt, string? reply)
    {
        if (!TryEnqueueTurn(prompt, reply))
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
        bool bootstrapped = false;
        int consecutiveFailures = 0;
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

                if (!bootstrapped)
                {
                    await writer.ApplyAsync(UserPromptContent.BuildBootstrapChange(), ct);
                    await writer.ApplyAsync(ResponseContent.BuildBootstrapChange(), ct);
                    bootstrapped = true;
                }

                // Every turn is a distinct witnessing event: rows dedup by content
                // address, but the testimony folds again — a repeated utterance IS
                // another witness (chess parity: every play of a move counts).
                var promptRoot = Hash128.Zero;
                if (UserPromptContent.TryBuildWitnessChange(
                        Encoding.UTF8.GetBytes(item.Prompt), "turn/prompt",
                        out var promptChange, out var pr))
                {
                    await writer.ApplyAsync(promptChange, ct);
                    promptRoot = pr;
                }

                if (item.Reply is { } reply &&
                    ResponseContent.TryBuildWitnessChange(
                        Encoding.UTF8.GetBytes(reply), "turn/reply",
                        promptRoot == Hash128.Zero ? null : promptRoot,
                        out var replyChange, out var replyRoot))
                {
                    await writer.ApplyAsync(replyChange, ct);
                    _log.LogInformation("turn witnessed: prompt={PromptRoot} reply={ReplyRoot}",
                        promptRoot, replyRoot);
                }
                else
                {
                    _log.LogInformation("turn witnessed: prompt={PromptRoot}", promptRoot);
                }
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
