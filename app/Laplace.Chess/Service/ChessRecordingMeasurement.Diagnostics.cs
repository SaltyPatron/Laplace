using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

internal sealed partial class ChessRecordingMeasurement
{
    public WorkDiagnostics Work { get; } = new();
    public WriterLogDiagnostics WriterLog { get; } = new();
    private WorkPhase? _workPhase;
    private long _workPhaseStarted;

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
        public long BuiltManagedPhysicalityObservationRows { get; internal set; }
        public long BuiltManagedAttestationRows { get; internal set; }
        public long BuiltNativeStages { get; internal set; }
        public string CounterScope => "Attempt/composition counters are work observations, not durable games or unique entities. Managed row counts describe finalized builder arrays before writer merging and exclude the separately counted native stages. BuiltManagedPhysicalityObservationRows counts explicit raw sidecar occurrences, including multiplicity, and excludes fallback to selected physicality rows; these counts do not imply distinct entities or placements. Position occurrences retain repeated positions within and across games.";
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
            Work.BuiltManagedPhysicalityObservationRows += change.PhysicalityObservations.IsDefault
                ? 0 : change.PhysicalityObservations.Length;
            Work.BuiltManagedAttestationRows += change.Attestations.Length;
            Work.BuiltNativeStages += change.IntentStages.IsDefault ? 0 : change.IntentStages.Length;
        }
    }

    public sealed record WriterLogEntry(double AdmissionElapsedSeconds, string Level,
        string Message, IReadOnlyDictionary<string, object?> Fields, string? ExceptionType);

    public sealed class WriterLogDiagnostics
    {
        internal const int Capacity = 128;
        internal const int MaximumTextLength = 2048;
        private readonly Queue<WriterLogEntry> _entries = new();
        public string Scope => "Bounded existing writer ILogger diagnostics; elapsed values may be cumulative, nested or parallel and must not be summed as exclusive phases. A phase completion log describes that operation returning, not durable game acceptance. Missing logs do not prove zero work.";
        public long DroppedEntries { get; private set; }
        public IReadOnlyList<WriterLogEntry> Entries
        {
            get { lock (_entries) return _entries.ToArray(); }
        }
        internal static string Bounded(string value) => value.Length <= MaximumTextLength
            ? value : value[..MaximumTextLength] + " [truncated]";
        internal void Add(WriterLogEntry entry)
        {
            lock (_entries)
            {
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
