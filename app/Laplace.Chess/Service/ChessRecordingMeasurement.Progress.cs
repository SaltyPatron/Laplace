using System.Diagnostics;
using System.Text.Json;

namespace Laplace.Chess.Service;

internal sealed partial class ChessRecordingMeasurement
{
    private ProgressSink? _progress;
    private long _lastLoopCheckpoint;
    private int _progressChunkGames;
    public ProgressDiagnostics? Progress => _progress?.Diagnostics;

    // Only the admission owner samples mutable counters. The heartbeat and writer
    // logger receive immutable scalar copies and never inspect a game/change graph.
    internal async Task StartProgressAsync(string path, Action<string>? log = null)
    {
        _progress = new ProgressSink(path, _started, log);
        Checkpoint("admission", "started");
        await _progress.FlushAsync();
        try { _progress.Start(); }
        catch (Exception failure) { _progress.Diagnostics.ObservationFailed(failure); }
    }

    internal async Task StopProgressAsync()
    {
        if (_progress is null) return;
        Checkpoint("admission", "terminal");
        await _progress.DisposeAsync();
    }

    internal void Checkpoint(string phase, string boundary, bool periodic = false, int? chunkGames = null)
    {
        var progress = _progress;
        if (progress is null) return;
        try
        {
            long now = Stopwatch.GetTimestamp();
            if (periodic && Stopwatch.GetElapsedTime(_lastLoopCheckpoint, now) < TimeSpan.FromMilliseconds(250))
                return;
            _lastLoopCheckpoint = now;
            if (chunkGames is { } count) _progressChunkGames = count;
            var counters = new ProgressCounters(
                RequestedGames, ParsedGames, Work.ParseAttempts, Work.ParseRejected, Work.ParsedPlies,
                Work.ChunksStarted, Work.ChunksVerified, _progressChunkGames,
                Work.NovelGamesComposed, Work.NovelPliesComposed, Work.PositionOccurrencesComposed,
                Work.RepairGamesComposed, Work.BuiltChanges, Work.BuiltManagedEntityRows,
                Work.BuiltManagedPhysicalityRows, Work.BuiltManagedPhysicalityObservationRows,
                Work.BuiltManagedAttestationRows, Work.BuiltNativeStages, Work.WriterApplyAttempts,
                Writer.ApplyCalls, Writer.CopyTransactionsCommitted, CommittedGames,
                _streamedReadbackGames + Games.Count, _corpusEvidence?.ReadbackGames ?? 0,
                _corpusEvidence?.NewlyRecordedGames ?? 0);
            progress.Publish(phase, boundary, Status, counters);
        }
        catch (Exception failure) { progress.Diagnostics.ObservationFailed(failure); }
    }

    public sealed record ProgressCounters(int RequestedGames, int ParsedGames,
        long ParseAttempts, long ParseRejected, long ParsedPlies,
        long ChunksStarted, long ChunksSealed, int CurrentChunkGames,
        long NovelGamesComposed, long NovelPliesComposed, long PositionOccurrencesComposed,
        long RepairGamesComposed, long BuiltChanges, long BuiltManagedEntityRows,
        long BuiltManagedPhysicalityRows, long BuiltManagedPhysicalityObservationRows,
        long BuiltManagedAttestationRows, long BuiltNativeStages, long WriterApplyAttempts,
        long WriterCallsAcknowledged, long CopyTransactionsAcknowledged,
        int GamesEnteredReadback, int ExactReadbackGames,
        int GamesWithSealedChunkEvidence, int NewlyRecordedGamesWithSealedChunkEvidence);

    public sealed record ProgressCheckpoint(long Sequence, double ObservedElapsedSeconds,
        double PhaseEnteredElapsedSeconds, string Phase, string Boundary, string AdmissionStatus,
        ProgressCounters Counters, WriterProgress? LastWriterBoundary = null);

    public sealed record WriterProgress(string OwnerPhase, string Phase, string Boundary,
        double ObservedElapsedSeconds, double? ElapsedMilliseconds, bool? Returned);

    public sealed class ProgressDiagnostics
    {
        private long _successfulWrites, _writeFailures, _logFailures, _observationFailures;
        private string? _lastDiagnosticErrorType, _lastDiagnosticError;
        public string Scope => "Diagnostic observations only. Heartbeats prove the observer ran, not forward admission progress. Counters are immutable samples from the admission owner; writer boundary events keep that last sample. Attempts, composed rows, acknowledged writer calls, games entering readback and exact readback are separate from sealed chunk evidence. Replay can enter readback without invoking the writer. No rate or durable-success verdict is inferred here.";
        public long SuccessfulWrites => Interlocked.Read(ref _successfulWrites);
        public long WriteFailures => Interlocked.Read(ref _writeFailures);
        public long LogFailures => Interlocked.Read(ref _logFailures);
        public long ObservationFailures => Interlocked.Read(ref _observationFailures);
        public double HeartbeatIntervalSeconds => 1;
        public double LoopCheckpointIntervalSeconds => .25;
        public string? LastDiagnosticErrorType => Volatile.Read(ref _lastDiagnosticErrorType);
        public string? LastDiagnosticError => Volatile.Read(ref _lastDiagnosticError);
        internal void Wrote() => Interlocked.Increment(ref _successfulWrites);
        internal void LogFailed() => Interlocked.Increment(ref _logFailures);
        internal void ObservationFailed(Exception failure)
        {
            Interlocked.Increment(ref _observationFailures);
            Remember(failure);
        }
        internal void Failed(Exception failure)
        {
            Interlocked.Increment(ref _writeFailures);
            Remember(failure);
        }
        private void Remember(Exception failure)
        {
            Volatile.Write(ref _lastDiagnosticErrorType, failure.GetType().FullName);
            Volatile.Write(ref _lastDiagnosticError, WriterLogDiagnostics.Bounded(failure.Message));
        }
    }

    internal sealed class ProgressSink : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly string _path;
        private readonly long _started;
        private readonly Action<string>? _log;
        private readonly Func<string, string, Task> _write;
        private readonly object _stateLock = new();
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly CancellationTokenSource _stop = new();
        private readonly DateTimeOffset? _processStartedAt;
        private readonly int _processId = Environment.ProcessId;
        private ProgressCheckpoint? _latest;
        private Task? _loop;
        private string? _loggedBoundary;
        private int _disposed;
        public ProgressDiagnostics Diagnostics { get; } = new();

        internal ProgressSink(string path, long started, Action<string>? log = null,
            Func<string, string, Task>? write = null)
        {
            _path = path;
            _started = started;
            _log = log;
            _write = write ?? AtomicWriteAsync;
            try
            {
                using var process = Process.GetCurrentProcess();
                _processStartedAt = new DateTimeOffset(process.StartTime.ToUniversalTime());
            }
            catch { _processStartedAt = null; }
        }

        internal void Publish(string phase, string boundary, string status, ProgressCounters counters)
        {
            lock (_stateLock)
            {
                double now = Stopwatch.GetElapsedTime(_started).TotalSeconds;
                double entered = _latest?.Phase == phase ? _latest.PhaseEnteredElapsedSeconds : now;
                _latest = new((_latest?.Sequence ?? 0) + 1, now, entered,
                    phase, boundary, status, counters, _latest?.LastWriterBoundary);
            }
        }

        internal void ObserveWriter(string phase, string boundary, bool? returned, double? elapsedMs)
        {
            try
            {
                lock (_stateLock)
                {
                    if (_latest is not { } previous) return;
                    _latest = previous with
                    {
                        Sequence = previous.Sequence + 1,
                        LastWriterBoundary = new(previous.Phase, WriterLogDiagnostics.Bounded(phase),
                            boundary, Stopwatch.GetElapsedTime(_started).TotalSeconds, elapsedMs, returned),
                    };
                }
            }
            catch (Exception failure) { Diagnostics.ObservationFailed(failure); }
        }

        internal void Start() => _loop = Task.Run(HeartbeatAsync);

        private async Task HeartbeatAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                while (await timer.WaitForNextTickAsync(_stop.Token))
                    await FlushAsync();
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception failure) { Diagnostics.Failed(failure); }
        }

        internal async Task FlushAsync()
        {
            await _writeGate.WaitAsync();
            try
            {
                ProgressCheckpoint? snapshot;
                lock (_stateLock) snapshot = _latest;
                if (snapshot is null) return;
                var document = new
                {
                    schema = "laplace.chess-recording-progress/v1",
                    processId = _processId, processStartedAt = _processStartedAt,
                    heartbeatAt = DateTimeOffset.UtcNow,
                    heartbeatElapsedSeconds = Stopwatch.GetElapsedTime(_started).TotalSeconds,
                    checkpoint = snapshot, diagnostics = Diagnostics,
                };
                try
                {
                    await _write(_path, JsonSerializer.Serialize(document, JsonOptions));
                    Diagnostics.Wrote();
                }
                catch (Exception failure)
                {
                    Diagnostics.Failed(failure);
                    if (Diagnostics.WriteFailures == 1 && _log is not null)
                        try { _log($"CHESS_RECORDING_PROGRESS_WRITE_FAILED error_type={failure.GetType().Name}"); }
                        catch { Diagnostics.LogFailed(); }
                }
                string boundary = snapshot.Phase + "/" + snapshot.Boundary + "/"
                    + snapshot.LastWriterBoundary?.Phase + "/" + snapshot.LastWriterBoundary?.Boundary;
                if (_log is not null && boundary != _loggedBoundary)
                {
                    _loggedBoundary = boundary;
                    try
                    {
                        _log($"CHESS_RECORDING_PHASE phase={snapshot.Phase} boundary={snapshot.Boundary} writer_phase={snapshot.LastWriterBoundary?.Phase ?? "-"} writer_boundary={snapshot.LastWriterBoundary?.Boundary ?? "-"} parsed_games={snapshot.Counters.ParsedGames} composed_games={snapshot.Counters.NovelGamesComposed} sealed_new_games={snapshot.Counters.NewlyRecordedGamesWithSealedChunkEvidence}");
                    }
                    catch { Diagnostics.LogFailed(); }
                }
            }
            catch (Exception failure) { Diagnostics.Failed(failure); }
            finally { _writeGate.Release(); }
        }

        private static async Task AtomicWriteAsync(string path, string json)
        {
            await File.WriteAllTextAsync(path + ".pending", json);
            File.Move(path + ".pending", path, overwrite: true);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            if (_loop is not null) await _loop;
            await FlushAsync();
            _stop.Dispose();
            // Keep the lightweight gate/state alive for a late diagnostic callback;
            // it must not turn completed writer work into an ObjectDisposedException.
        }
    }
}
