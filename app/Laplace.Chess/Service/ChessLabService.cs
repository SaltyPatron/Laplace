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
    private readonly string _labDir;

    // The /chess/lab/* HTTP surface has no request-level auth (see EndpointMappings.Chess.cs) —
    // this cap is the actual mitigation against an unbounded number of concurrent cutechess/
    // Stockfish process spawns or self-play jobs, independent of caller identity.
    private const int MaxConcurrentJobs = 4;

    // Job metadata, event channels, transcripts, and per-job files are one lifecycle.
    // They are process-local observability, not the durable chess corpus: completed PGNs
    // that are meant to become knowledge are already ingested into the substrate. Keeping
    // every terminal job forever previously made both _jobs and chess-lab-work append-only.
    internal const int MaxRetainedTerminalJobs = 32;
    internal static readonly TimeSpan OrphanWorkspaceMinimumAge = TimeSpan.FromHours(24);

    public ChessLabService(
        Func<CancellationToken, Task<ChessLiveGameHost>> getLiveHost,
        ILogger? log = null)
    {
        _getLiveHost = getLiveHost ?? throw new ArgumentNullException(nameof(getLiveHost));
        _log = log ?? NullLogger.Instance;
        _labDir = Path.GetFullPath(ChessLabPaths.LabDir);
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
        // Every new run first bounds process-local history and stale work left by a prior
        // service process. This turns job creation into the maintenance cadence instead of
        // requiring a separate cron job with weaker knowledge of which jobs are live.
        PruneRetainedState(DateTimeOffset.UtcNow);

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
                case ChessLabJobKind.FideProfile:
                    await FideLabRunners.RunProfileAsync(this, slot, ct);
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

    internal int PruneRetainedState(DateTimeOffset now)
    {
        int removed = 0;
        var terminal = _jobs.Values
            .Select(Snapshot)
            .Where(static job => IsTerminal(job.State))
            .OrderByDescending(static job => job.FinishedAt ?? job.CreatedAt)
            .ThenByDescending(static job => job.CreatedAt)
            .ToArray();

        foreach (var job in terminal.Skip(MaxRetainedTerminalJobs))
        {
            if (!_jobs.TryRemove(job.Id, out var slot)) continue;
            slot.Cts?.Dispose();
            if (TryDeleteJobWorkspace(job.Id)) removed++;
        }

        removed += PruneOrphanedWorkspaces(now);
        return removed;
    }

    internal static IReadOnlyList<string> SelectTerminalJobsForRemoval(
        IEnumerable<ChessLabJob> jobs, int retain = MaxRetainedTerminalJobs)
    {
        if (retain < 0) throw new ArgumentOutOfRangeException(nameof(retain));
        return jobs
            .Where(static job => IsTerminal(job.State))
            .OrderByDescending(static job => job.FinishedAt ?? job.CreatedAt)
            .ThenByDescending(static job => job.CreatedAt)
            .Skip(retain)
            .Select(static job => job.Id)
            .ToArray();
    }

    private int PruneOrphanedWorkspaces(DateTimeOffset now)
    {
        if (!Directory.Exists(_labDir)) return 0;
        int removed = 0;
        foreach (var path in Directory.EnumerateDirectories(_labDir))
        {
            string name = Path.GetFileName(path);
            if (!IsCanonicalJobId(name) || _jobs.ContainsKey(name)) continue;
            try
            {
                var info = new DirectoryInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var age = now - new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                if (age < OrphanWorkspaceMinimumAge) continue;
                if (ContainsReparsePoint(path))
                {
                    _log.LogWarning("preserving chess lab orphan with reparse point: {Path}", path);
                    continue;
                }
                Directory.Delete(path, recursive: true);
                removed++;
                _log.LogInformation("reclaimed orphaned chess lab workspace {JobId}", name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "could not reclaim orphaned chess lab workspace {Path}", path);
            }
        }
        return removed;
    }

    private bool TryDeleteJobWorkspace(string jobId)
    {
        if (!IsCanonicalJobId(jobId)) return false;
        string path = Path.GetFullPath(Path.Combine(_labDir, jobId));
        if (!string.Equals(Path.GetDirectoryName(path), _labDir,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return false;
        if (!Directory.Exists(path)) return false;
        try
        {
            var info = new DirectoryInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || ContainsReparsePoint(path))
            {
                _log.LogWarning("preserving chess lab workspace with reparse point: {Path}", path);
                return false;
            }
            Directory.Delete(path, recursive: true);
            _log.LogInformation("reclaimed retained chess lab workspace {JobId}", jobId);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "could not reclaim chess lab workspace {Path}", path);
            return false;
        }
    }

    internal static bool IsCanonicalJobId(string value) =>
        Guid.TryParseExact(value, "N", out var parsed)
        && string.Equals(parsed.ToString("N"), value, StringComparison.Ordinal);

    private static bool ContainsReparsePoint(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
        return false;
    }

    private static bool IsTerminal(ChessLabJobState state) =>
        state is ChessLabJobState.Completed or ChessLabJobState.Failed or ChessLabJobState.Cancelled;

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
