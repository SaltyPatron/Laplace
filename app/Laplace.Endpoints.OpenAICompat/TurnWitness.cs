using System.Text;
using System.Threading.Channels;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
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

    private readonly record struct TurnItem(string Text, string Label);

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
    public bool TryEnqueue(string text, string label)
    {
        if (!IsOnline || string.IsNullOrWhiteSpace(text))
            return false;
        return _queue.Writer.TryWrite(new TurnItem(text.Trim(), label));
    }

    public void Enqueue(string text, string label)
    {
        if (!TryEnqueue(text, label))
            _log.LogWarning("turn-witness rejected {Label} turn (lane offline or queue full)", label);
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

        var writer = new NpgsqlSubstrateWriter(_substrate.DataSource);
        bool floorPresent = false;
        bool bootstrapped = false;
        int consecutiveFailures = 0;
        IsOnline = true;
        _log.LogInformation("turn-witness online");

        await foreach (var item in _queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                _log.LogDebug("turn-witness processing {Label} ({Bytes} bytes)",
                    item.Label, item.Text.Length);
                if (!floorPresent && !(floorPresent = await FloorPresentAsync(ct)))
                {
                    _log.LogWarning(
                        "substrate floor missing (no Codepoint entities); witness lane offline until unicode seed");
                    IsOnline = false;
                    return;
                }

                var utf8 = Encoding.UTF8.GetBytes(item.Text);
                if (!UserPromptContent.TryBuildWitnessChange(utf8, item.Label, out var change, out var rootId))
                    continue;
                if (await AlreadyWitnessedAsync(rootId, ct))
                    continue;

                if (!bootstrapped)
                {
                    await writer.ApplyAsync(UserPromptContent.BuildBootstrapChange(), ct);
                    bootstrapped = true;
                }

                await writer.ApplyAsync(change, ct);
                consecutiveFailures = 0;
                _log.LogInformation("turn witnessed: {Label} root={Root} ({Bytes} bytes)",
                    item.Label, rootId, utf8.Length);
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
                _log.LogWarning(ex, "turn-witness deposit failed; {Label} turn dropped", item.Label);
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

    private async Task<bool> AlreadyWitnessedAsync(Hash128 rootId, CancellationToken ct)
    {
        await using var conn = await _substrate.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM laplace.physicalities WHERE entity_id = @e AND type = 1 LIMIT 1", conn);
        cmd.Parameters.AddWithValue("e", rootId.ToBytes());
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}
