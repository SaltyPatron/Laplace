using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Laplace.Chess.Service;

public sealed class ChessLabService
{
    private readonly ILogger _log;
    private readonly Func<CancellationToken, Task<ChessLiveGameHost>> _getLiveHost;
    private readonly ConcurrentDictionary<string, JobSlot> _jobs = new();

    // The /chess/lab/* HTTP surface has no request-level auth (see EndpointMappings.Chess.cs) —
    // this cap is the actual mitigation against an unbounded number of concurrent cutechess/
    // Stockfish process spawns or self-play jobs, independent of caller identity.
    private const int MaxConcurrentJobs = 4;

    public ChessLabService(
        Func<CancellationToken, Task<ChessLiveGameHost>> getLiveHost,
        ILogger? log = null)
    {
        _getLiveHost = getLiveHost ?? throw new ArgumentNullException(nameof(getLiveHost));
        _log = log ?? NullLogger.Instance;
    }

    // Kept for non-substrate unit callers. Production composition supplies the Generic-Host
    // owned ChessRuntimeService provider so lab jobs never manufacture their own PG pools.
    public ChessLabService(ILogger? log = null)
        : this(_ => Task.FromException<ChessLiveGameHost>(new InvalidOperationException(
            "ChessLabService substrate jobs require the host-owned ChessRuntimeService.")), log)
    {
    }

    internal Task<ChessLiveGameHost> GetLiveHostAsync(CancellationToken ct) => _getLiveHost(ct);

    public string? StartJob(ChessLabJobKind kind, IReadOnlyDictionary<string, string>? config = null)
    {
        int running = 0;
        foreach (var s in _jobs.Values)
            if (Snapshot(s).State == ChessLabJobState.Running && ++running >= MaxConcurrentJobs)
            {
                _log.LogWarning("chess lab job rejected: {Max} jobs already running", MaxConcurrentJobs);
                return null;
            }

        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var job = new ChessLabJob(
            id, kind, ChessLabJobState.Pending,
            config ?? EmptyConfig.Instance,
            new ChessLabJobSummary(),
            EmptyConfig.Instance,
            now);

        var channel = Channel.CreateBounded<ChessLabEvent>(new BoundedChannelOptions(4096)
        {
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        var slot = new JobSlot(job, channel);
        if (!_jobs.TryAdd(id, slot)) return null;

        lock (slot.Gate)
        {
            slot.Job = slot.Job with { State = ChessLabJobState.Running };
            slot.Cts = new CancellationTokenSource();
            slot.RunTask = Task.Run(() => RunJobAsync(slot, slot.Cts.Token));
        }

        _log.LogInformation("chess lab job {JobId} started ({Kind})", id, kind);
        return id;
    }

    public bool StopJob(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var slot)) return false;

        CancellationTokenSource? cts;
        lock (slot.Gate)
        {
            if (slot.Job.State is ChessLabJobState.Completed or ChessLabJobState.Failed or ChessLabJobState.Cancelled)
                return false;
            cts = slot.Cts;
        }

        if (cts is null) return false;
        cts.Cancel();
        Publish(slot, new ChessLabLogEvent("info", "stop requested — cancelling workers"));
        return true;
    }

    public ChessLabJob? GetJob(string jobId) =>
        _jobs.TryGetValue(jobId, out var slot) ? Snapshot(slot) : null;

    public IReadOnlyList<ChessLabJob> ListJobs()
    {
        var list = new List<ChessLabJob>(_jobs.Count);
        foreach (var slot in _jobs.Values)
            list.Add(Snapshot(slot));
        list.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return list;
    }

    public ChannelReader<ChessLabEvent>? EventReader(string jobId) =>
        _jobs.TryGetValue(jobId, out var slot) ? slot.Channel.Reader : null;

    /// <summary>The job's raw process transcript — replayable, multi-viewer, independent of the event channel.</summary>
    public ChessLabTerminal? Terminal(string jobId) =>
        _jobs.TryGetValue(jobId, out var slot) ? slot.Terminal : null;

    private async Task RunJobAsync(JobSlot slot, CancellationToken ct)
    {
        try
        {
            Publish(slot, new ChessLabLogEvent("info", $"job {slot.Job.Id} ({slot.Job.Kind}) starting"));
            switch (slot.Job.Kind)
            {
                case ChessLabJobKind.SubstrateTest:
                    await ChessLabRunners.RunSubstrateTestAsync(this, slot, ct); return;
                case ChessLabJobKind.Ladder:
                    await ChessLabRunners.RunLadderAsync(this, slot, ct); return;
                case ChessLabJobKind.Tactics:
                    await ChessLabRunners.RunTacticsAsync(this, slot, ct); return;
                case ChessLabJobKind.Review:
                    await ChessLabRunners.RunReviewAsync(this, slot, ct); return;
                case ChessLabJobKind.LearnedPst:
                    await ChessLabRunners.RunLearnedPstAsync(this, slot, ct); return;
                case ChessLabJobKind.Cutechess:
                    await ChessLabRunners.RunCutechessAsync(this, slot, ct); return;
                case ChessLabJobKind.LichessBot:
                    await ChessLabRunners.RunLichessBotAsync(this, slot, ct); return;
                case ChessLabJobKind.LichessFetch:
                    await ChessLabRunners.RunLichessFetchAsync(this, slot, ct); return;
                case ChessLabJobKind.PlayerProfile:
                    await ChessLabRunners.RunPlayerProfileAsync(this, slot, ct); return;
                case ChessLabJobKind.FideSearch:
                    await FideLabRunners.RunSearchAsync(this, slot, ct);
                    Finish(slot, ChessLabJobState.Completed, null);
                    return;
                case ChessLabJobKind.FideRoster:
                    await FideLabRunners.RunRosterAsync(this, slot, ct);
                    Finish(slot, ChessLabJobState.Completed, null);
                    return;
                default:
                    Finish(slot, ChessLabJobState.Failed, "unknown job kind");
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            Finish(slot, ChessLabJobState.Cancelled, "cancelled");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "chess lab job {JobId} failed", slot.Job.Id);
            Publish(slot, new ChessLabLogEvent("error", ex.Message));
            Finish(slot, ChessLabJobState.Failed, ex.Message);
        }
    }

    private void Finish(JobSlot slot, ChessLabJobState state, string? message)
    {
        lock (slot.Gate)
        {
            slot.Job = slot.Job with
            {
                State = state,
                FinishedAt = DateTimeOffset.UtcNow,
                Summary = slot.Job.Summary with { Message = message ?? slot.Job.Summary.Message },
            };
        }

        Publish(slot, new ChessLabDoneEvent(state, message));
        slot.Channel.Writer.TryComplete();
        slot.Terminal.Complete();
        _log.LogInformation("chess lab job {JobId} finished ({State})", slot.Job.Id, state);
    }

    public void Publish(JobSlot slot, ChessLabEvent evt)
    {
        if (!slot.Channel.Writer.TryWrite(evt))
            _log.LogWarning("chess lab job {JobId} event dropped (channel full)", slot.Job.Id);
    }

    /// <summary>
    /// Raw process I/O goes here, never to <see cref="Publish"/>: the event channel is a
    /// single consumed stream sized for semantic events, and a burst of UCI traffic would
    /// evict the progress and result frames sharing it.
    /// </summary>
    public ChessLabTerminalLine AppendTerminal(JobSlot slot, ChessLabTerminalEvent evt) =>
        slot.Terminal.Append(evt.Stream, evt.Text, evt.Engine, evt.Direction);

    public void UpdateSummary(JobSlot slot, ChessLabJobSummary summary)
    {
        lock (slot.Gate) { slot.Job = slot.Job with { Summary = summary }; }
    }

    public void AddArtifact(JobSlot slot, string name, string path)
    {
        lock (slot.Gate)
        {
            var artifacts = new Dictionary<string, string>(slot.Job.Artifacts, StringComparer.OrdinalIgnoreCase)
            {
                [name] = path,
            };
            slot.Job = slot.Job with { Artifacts = artifacts };
        }
    }

    internal bool TryGetSlot(string jobId, out JobSlot slot) => _jobs.TryGetValue(jobId, out slot!);

    private static ChessLabJob Snapshot(JobSlot slot)
    {
        lock (slot.Gate) { return slot.Job; }
    }

    public sealed class JobSlot
    {
        public readonly object Gate = new();
        public ChessLabJob Job;
        public Channel<ChessLabEvent> Channel { get; }
        public ChessLabTerminal Terminal { get; } = new();
        public CancellationTokenSource? Cts;
        public Task? RunTask;

        public JobSlot(ChessLabJob job, Channel<ChessLabEvent> channel)
        {
            Job = job;
            Channel = channel;
        }
    }

    private sealed class EmptyConfig : IReadOnlyDictionary<string, string>
    {
        public static readonly EmptyConfig Instance = new();
        public string this[string key] => throw new KeyNotFoundException(key);
        public IEnumerable<string> Keys => [];
        public IEnumerable<string> Values => [];
        public int Count => 0;
        public bool ContainsKey(string key) => false;
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            yield break;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public bool TryGetValue(string key, out string value) { value = null!; return false; }
    }
}
