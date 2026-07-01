using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Laplace.Chess.Service;

public sealed class ChessLabService
{
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, JobSlot> _jobs = new();

    public ChessLabService(ILogger? log = null) => _log = log ?? NullLogger.Instance;

    public string? StartJob(ChessLabJobKind kind, IReadOnlyDictionary<string, string>? config = null)
    {
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
        _log.LogInformation("chess lab job {JobId} finished ({State})", slot.Job.Id, state);
    }

    public void Publish(JobSlot slot, ChessLabEvent evt)
    {
        if (!slot.Channel.Writer.TryWrite(evt))
            _log.LogWarning("chess lab job {JobId} event dropped (channel full)", slot.Job.Id);
    }

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
