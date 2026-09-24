using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

internal sealed partial class ChessRecordingMeasurement
{

    public ConsensusBackendDiagnostics ConsensusBackend { get; private set; } = new(0, 0, 0, 0, 0, 0, 0);

    public sealed record ConsensusBackendDiagnostics(long ObservedApplyWindows,
        double ConsensusUpsertSeconds, double HighwayMaskSeconds, long CellsFolded,
        long ConsensusUpsertCalls, long HighwayMaskCalls, long HighwayMaskPairs)
    {
        public string Scope => "Deltas of the owned ConsensusAccumulatingWriter's existing cumulative counters around measured ApplyManyAsync calls. Backend wall-clock times are nested in WriterApply and consensus acceptance, not additional exclusive time or pure CPU time. The writer publishes atomic counters only after successful apply; failed or cancelled backend work can therefore be absent. Cells and pairs count processed work, not unique durable objects. Setup and other admission windows are excluded; borrowed shared writers are not sampled.";
    }

    internal readonly record struct ConsensusBackendSnapshot(long ConsensusTicks, long MaskTicks,
        long Cells, long ConsensusCalls, long MaskCalls, long MaskPairs)
    {
        internal static ConsensusBackendSnapshot Read(ConsensusAccumulatingWriter writer) => new(
            writer.ConsensusUpsertBackendWallClock.Ticks, writer.HighwayMaskBackendWallClock.Ticks,
            writer.CellsFolded, writer.ConsensusUpsertCalls, writer.HighwayMaskCalls, writer.HighwayMaskPairs);
    }

    internal void ObserveConsensusBackend(ConsensusBackendSnapshot before, ConsensusBackendSnapshot after)
    {
        var previous = ConsensusBackend;
        // One owned ingestor applies serially. Publish one immutable diagnostic
        // snapshot so receipt serialization cannot observe partially updated fields.
        ConsensusBackend = new(previous.ObservedApplyWindows + 1,
            previous.ConsensusUpsertSeconds + (after.ConsensusTicks - before.ConsensusTicks) / (double)TimeSpan.TicksPerSecond,
            previous.HighwayMaskSeconds + (after.MaskTicks - before.MaskTicks) / (double)TimeSpan.TicksPerSecond,
            previous.CellsFolded + after.Cells - before.Cells,
            previous.ConsensusUpsertCalls + after.ConsensusCalls - before.ConsensusCalls,
            previous.HighwayMaskCalls + after.MaskCalls - before.MaskCalls,
            previous.HighwayMaskPairs + after.MaskPairs - before.MaskPairs);
    }

    public WorkDiagnostics Work { get; } = new();
    public WriterLogDiagnostics WriterLog { get; } = new();
    private WorkPhase? _workPhase;
    private long _workPhaseStarted;

    public ReadbackOperationDiagnostics ReadbackOperations { get; } = new();

    internal enum ReadbackOperation
    {
        ExactWitnessBodies,
        CanonicalCarrierVertices,
        HydrationAndLegalReplay,
        ResultAndReceiptText,
    }

    public sealed record ReadbackOperationAggregate(string Operation, long ReturnedCalls,
        long InterruptedCalls, double TotalSeconds);

    public sealed class ReadbackOperationDiagnostics
    {
        private readonly long[] _ticks = new long[Enum.GetValues<ReadbackOperation>().Length];
        private readonly long[] _returned = new long[Enum.GetValues<ReadbackOperation>().Length];
        private readonly long[] _interrupted = new long[Enum.GetValues<ReadbackOperation>().Length];
        public string Scope => "Nested wall-clock windows for the four existing exact-readback operations, including interrupted calls. HydrationAndLegalReplay includes database reads, native decoding and managed legal replay. These are not pure database or CPU times, and are already included in ExactReadback and elapsedSeconds.readback. Returning does not establish successful validation or durable-game acceptance.";
        public IReadOnlyList<ReadbackOperationAggregate> Operations
        {
            get
            {
                lock (_ticks)
                    return Enum.GetValues<ReadbackOperation>().Select(operation =>
                        new ReadbackOperationAggregate(operation.ToString(),
                            _returned[(int)operation], _interrupted[(int)operation],
                            _ticks[(int)operation] / (double)Stopwatch.Frequency)).ToArray();
            }
        }

        internal void Add(ReadbackOperation operation, long ticks, bool returned)
        {
            lock (_ticks)
            {
                _ticks[(int)operation] += ticks;
                if (returned) _returned[(int)operation]++;
                else _interrupted[(int)operation]++;
            }
        }
    }

    private async Task<T> MeasureReadbackAsync<T>(ReadbackOperation operation, Func<Task<T>> read)
    {
        long started = Stopwatch.GetTimestamp();
        bool returned = false;
        try
        {
            T result = await read();
            returned = true;
            return result;
        }
        finally { ReadbackOperations.Add(operation, Stopwatch.GetTimestamp() - started, returned); }
    }

    internal enum WorkPhase
    {
        SourceReadParseAndValidation,
        CompositionAndNoveltyProbe,
        Syzygy,
        ChangeMaterializationAndWitnessPreparation,
        BeforeScopeProbe,
        WriterApply,
        ExactReadback,
        AfterScopeProbe,
        ChunkEvidenceOutput,
        FinalSourceVerificationAndEvidenceCompletion,
    }

    public sealed class WorkDiagnostics
    {
        private readonly long[] _ticks = new long[Enum.GetValues<WorkPhase>().Length];
        public string TimingScope => "Exclusive wall-clock child windows of the existing admission route, including failed windows. SourceReadParseAndValidation includes source enumeration, native PGN parsing, legal replay and source validation. CompositionAndNoveltyProbe includes the existing iterator's presence probe, canonical recording, shared board/position replay and calculated outcome observations. ChangeMaterializationAndWitnessPreparation includes builder finalization and missing-witness probes. Syzygy measures actual interleaved game calls; availability is null until a novel game reaches that runtime selection. FinalSourceVerificationAndEvidenceCompletion covers unchanged-source checks and final evidence manifests. These windows are not additional time to add to elapsedSeconds.";
        public IReadOnlyDictionary<string, double> ExclusiveSeconds => Enum.GetValues<WorkPhase>()
            .ToDictionary(phase => phase.ToString(), phase => _ticks[(int)phase] / (double)Stopwatch.Frequency);
        public long ParseAttempts { get; internal set; }
        public long ParseRejected { get; internal set; }
        public long ParsedPlies { get; internal set; }
        public long ChunksStarted { get; internal set; }
        public long ChunksVerified { get; internal set; }
        public long NovelGamesComposed { get; internal set; }
        public long NovelPliesComposed { get; internal set; }
        public long PositionOccurrencesComposed { get; internal set; }
        public long RepairGamesComposed { get; internal set; }
        public bool? SyzygyAvailable { get; internal set; }
        public long SyzygyGameCalls { get; internal set; }
        public long SyzygyGameCallsCompleted { get; internal set; }
        public long WriterApplyAttempts { get; internal set; }
        public long BuiltChanges { get; internal set; }
        public long BuiltManagedEntityRows { get; internal set; }
        public long BuiltManagedPhysicalityRows { get; internal set; }
        public long BuiltManagedAttestationRows { get; internal set; }
        public long BuiltNativeStages { get; internal set; }
        public string CounterScope => "Attempt/composition counters are work observations, not durable games or unique entities. Managed row counts describe finalized builder arrays before writer merging and exclude the separately counted native stages. These counts do not imply distinct entities or placements. Position occurrences retain repeated positions within and across games.";
        internal void Add(WorkPhase phase, long ticks) => _ticks[(int)phase] += ticks;
    }

    internal PhaseScope MeasurePhase(WorkPhase phase)
    {
        var previous = _workPhase;
        SwitchPhase(phase);
        return new(this, previous);
    }

    private void SwitchPhase(WorkPhase? next)
    {
        long now = Stopwatch.GetTimestamp();
        if (_workPhase is { } current) Work.Add(current, now - _workPhaseStarted);
        _workPhase = next;
        _workPhaseStarted = now;
        Checkpoint(next?.ToString() ?? "admission", next is null ? "phase-returned" : "phase-entered");
    }

    internal readonly struct PhaseScope(ChessRecordingMeasurement owner, WorkPhase? previous) : IDisposable
    {
        public void Dispose() => owner.SwitchPhase(previous);
    }

    internal void ObserveBuiltChanges(IReadOnlyList<SubstrateChange> changes)
    {
        foreach (var change in changes)
        {
            Work.BuiltChanges++;
            Work.BuiltManagedEntityRows += change.Entities.Length;
            Work.BuiltManagedPhysicalityRows += change.Physicalities.Length;
            Work.BuiltManagedAttestationRows += change.Attestations.Length;
            Work.BuiltNativeStages += change.IntentStages.IsDefault ? 0 : change.IntentStages.Length;
        }
    }

    public sealed record WriterLogEntry(double AdmissionElapsedSeconds, string Level,
        string Message, IReadOnlyDictionary<string, object?> Fields, string? ExceptionType);

    public sealed record WriterPhaseAggregate(string Phase, long ObservedExits,
        long ReturnedExits, long InterruptedExits, double TotalMilliseconds,
        double MinimumMilliseconds, double MaximumMilliseconds);

    public sealed class WriterLogDiagnostics
    {
        internal const int Capacity = 128;
        internal const int MaximumTextLength = 2048;
        internal const int MaximumDistinctPhases = 32;
        private const int MaximumPhaseNameLength = 128;
        private readonly Queue<WriterLogEntry> _entries = new();
        private readonly Dictionary<string, WriterPhaseAggregate> _phaseAggregates = new(StringComparer.Ordinal);
        private long _unaggregatedPhaseExits, _rejectedPhaseExits;
        public string Scope => "Bounded existing writer ILogger diagnostics; elapsed values may be cumulative, nested or parallel and must not be summed as exclusive phases. A phase completion log describes that operation returning, not durable game acceptance. Missing logs do not prove zero work.";
        public long DroppedEntries { get; private set; }
        public IReadOnlyList<WriterLogEntry> Entries
        {
            get { lock (_entries) return _entries.ToArray(); }
        }
        public string PhaseAggregateScope => "All well-formed writer phase exit observations in this admission, independent of the bounded log tail. At most 32 distinct phase names are retained; later unseen names and invalid samples are counted separately. TotalMilliseconds sums observed call durations, which may overlap or nest; it is not exclusive wall time, CPU time, or durable-game evidence. ReturnedExits describes operations returning, not committed or read-back games.";
        public IReadOnlyList<WriterPhaseAggregate> PhaseAggregates
        {
            get
            {
                lock (_entries)
                    return _phaseAggregates.Values.OrderBy(value => value.Phase, StringComparer.Ordinal).ToArray();
            }
        }
        public long UnaggregatedPhaseExits { get { lock (_entries) return _unaggregatedPhaseExits; } }
        public long RejectedPhaseExits { get { lock (_entries) return _rejectedPhaseExits; } }

        // Called under the same lock as the log tail. No per-call timing or row
        // payload is retained beyond the immutable count/sum/min/max snapshot.
        private void AggregatePhaseExit(WriterLogEntry entry)
        {
            var fields = entry.Fields;
            if (!Equals(fields.GetValueOrDefault("Boundary"), "exited")) return;
            if (fields.GetValueOrDefault("Phase") is not string phase
                || phase.Length is 0 or > MaximumPhaseNameLength
                || fields.GetValueOrDefault("Returned") is not bool returned
                || fields.GetValueOrDefault("ElapsedMs") is not double milliseconds
                || !double.IsFinite(milliseconds) || milliseconds < 0)
            {
                _rejectedPhaseExits++;
                return;
            }
            if (!_phaseAggregates.TryGetValue(phase, out var previous))
            {
                if (_phaseAggregates.Count == MaximumDistinctPhases)
                {
                    _unaggregatedPhaseExits++;
                    return;
                }
                previous = new(phase, 0, 0, 0, 0, milliseconds, milliseconds);
            }
            double total = previous.TotalMilliseconds + milliseconds;
            if (!double.IsFinite(total) || previous.ObservedExits == long.MaxValue)
            {
                _rejectedPhaseExits++;
                return;
            }
            _phaseAggregates[phase] = previous with
            {
                ObservedExits = previous.ObservedExits + 1,
                ReturnedExits = previous.ReturnedExits + (returned ? 1 : 0),
                InterruptedExits = previous.InterruptedExits + (returned ? 0 : 1),
                TotalMilliseconds = total,
                MinimumMilliseconds = Math.Min(previous.MinimumMilliseconds, milliseconds),
                MaximumMilliseconds = Math.Max(previous.MaximumMilliseconds, milliseconds),
            };
        }

        internal static string Bounded(string value) => value.Length <= MaximumTextLength
            ? value : value[..MaximumTextLength] + " [truncated]";
        internal void Add(WriterLogEntry entry)
        {
            lock (_entries)
            {
                AggregatePhaseExit(entry);
                if (_entries.Count == Capacity) { _entries.Dequeue(); DroppedEntries++; }
                _entries.Enqueue(entry);
            }
        }
    }

    // One standalone benchmark owns this logger. The benchmark attaches only its
    // current admission receipt; setup/selection logs cannot enter a fresh/replay window.
    internal sealed class WriterDiagnosticLogger : ILogger<NpgsqlSubstrateWriter>, ILogger<ConsensusAccumulatingWriter>
    {
        internal ChessRecordingMeasurement? Measurement { get; set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => Measurement is not null && logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var measurement = Measurement;
            if (measurement is null || logLevel < LogLevel.Information) return;
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                foreach (var pair in values.Take(32))
                    fields[WriterLogDiagnostics.Bounded(pair.Key)] = pair.Value switch
                    {
                        null => null,
                        bool or byte or short or int or long or uint or ulong or decimal => pair.Value,
                        double d when double.IsFinite(d) => d,
                        float f when float.IsFinite(f) => f,
                        _ => WriterLogDiagnostics.Bounded(Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? ""),
                    };
            measurement.WriterLog.Add(new(Stopwatch.GetElapsedTime(measurement._started).TotalSeconds,
                logLevel.ToString(), WriterLogDiagnostics.Bounded(formatter(state, exception)),
                fields, exception?.GetType().FullName));
            if (fields.TryGetValue("Phase", out var phase) && phase is string phaseName
                && fields.TryGetValue("Boundary", out var boundary) && boundary is string boundaryName)
                measurement._progress?.ObserveWriter(phaseName, boundaryName,
                    fields.TryGetValue("Returned", out var returned) && returned is bool completed ? completed : null,
                    fields.TryGetValue("ElapsedMs", out var elapsed) && elapsed is double milliseconds ? milliseconds : null);
        }
    }
}
