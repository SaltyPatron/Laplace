using System.Collections.Immutable;
using System.Diagnostics;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>
/// In-process PGN → substrate ingest: witnessed record (ChessPgn source) plus the calculated
/// analyze pass (ChessAnalysis source) per game, through the same writer spine the live hosts
/// use. This is the loop-closer for games the lab plays via external engines (cutechess drives
/// the laplace-uci binary, which cannot record its own games) — the PGN artifact feeds straight
/// back into consensus instead of waiting for a manual `laplace ingest chess` run.
///
/// Exact durable PGN testimony controls recording novelty; content identity alone cannot prove
/// acceptance after a failed control transaction. Every selected game also composes the current
/// source and calculated owners. Re-ingest admits only missing canonical testimony, preserving
/// accepted observations while repairing incomplete or older recorded games from their source.
/// </summary>
public sealed partial class ChessPgnIngestor : IAsyncDisposable
{
    // Serialize in-process ingests; bulk CLI ingests hold the command-line mutex, this holds the
    // API process's own lane. Lab artifacts are small (tens of games) so waiting is fine.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // Nominal parse/novelty-probe window only. Actual admission chunks close on observed
    // staged bytes after complete games; the historical per-game profile is not a
    // bound on expanded observation residency.
    private static readonly int ChunkSize =
        IngestPipelineDefaults.ResolveBatch(IngestSourceProfile.ChessPgn, null);

    // An owned direct run holds one aggregate composition window under Gate through
    // apply/readback/disposal. Record, Analyze and both repair builders are already
    // included in that window's byte model; the generic compose fan is not active.
    // This static value also describes the owned corpus benchmark's reported threshold.
    internal static long ResolvedChunkStagedBytes => ResolveChunkStagedBytes(
        ownsResources: true, IngestTopology.Current.ComposeWorkers,
        IngestSizing.ResolveWorkingSetBudgetBytes(),
        IngestSizing.ResolveWorkingSetFlushEnvelopeBytes());

    // AttachAsync shares a live host whose other producers do not hold this Gate.
    // Preserve its existing division until that host owns a common residency budget.
    private long ChunkStagedBytes => ResolveChunkStagedBytes(
        _ownsResources, IngestTopology.Current.ComposeWorkers,
        IngestSizing.ResolveWorkingSetBudgetBytes(),
        IngestSizing.ResolveWorkingSetFlushEnvelopeBytes());

    internal static long ResolveChunkStagedBytes(
        bool ownsResources, int composeWorkers, long workingSetBudgetBytes, long flushEnvelopeBytes) =>
        Math.Max(1, Math.Min(workingSetBudgetBytes, flushEnvelopeBytes)
            / (ownsResources ? 1 : Math.Max(1, composeWorkers)));

    private readonly NpgsqlDataSource _ds;
    private readonly ConsensusAccumulatingWriter _writer;
    private readonly NpgsqlSubstrateReader _reader;
    private readonly bool _ownsResources;
    private long _entitiesInserted, _physicalitiesInserted, _attestationsInserted;
    private int _concurrentLandingRetries;

    public readonly record struct Result(
        int Parsed, int Novel, int Applied,
        int Repaired = 0, int Verified = 0, long VerifiedPlies = 0);
    public readonly record struct ProfileResult(int Profiles, int Players, int Links);

    /// <summary>Rows this ingestor's acknowledged applies inserted (run-journal counters).</summary>
    internal (long Entities, long Physicalities, long Attestations) Inserted => (
        Interlocked.Read(ref _entitiesInserted),
        Interlocked.Read(ref _physicalitiesInserted),
        Interlocked.Read(ref _attestationsInserted));

    /// <summary>Concurrent-landing re-probes this ingestor's applies took.</summary>
    internal int ConcurrentLandingRetries => Volatile.Read(ref _concurrentLandingRetries);

    private ChessPgnIngestor(
        NpgsqlDataSource ds, ConsensusAccumulatingWriter writer, NpgsqlSubstrateReader reader,
        bool ownsResources)
    {
        _ds = ds;
        _writer = writer;
        _reader = reader;
        _ownsResources = ownsResources;
    }

    public static Task<ChessPgnIngestor> CreateAsync(CancellationToken ct = default)
        => CreateAsync(null, ct);

    internal static Task<ChessPgnIngestor> CreateAsync(
        ChessRecordingMeasurement.WriterDiagnosticLogger? diagnostics, CancellationToken ct)
        => CreateOwnedAsync(diagnostics, bootstrap: true, ct);

    internal static Task<ChessPgnIngestor> CreateRecordedVerifierAsync(
        ChessRecordingMeasurement.WriterDiagnosticLogger? diagnostics, CancellationToken ct)
        => CreateOwnedAsync(diagnostics, bootstrap: false, ct);

    private static async Task<ChessPgnIngestor> CreateOwnedAsync(
        ChessRecordingMeasurement.WriterDiagnosticLogger? diagnostics, bool bootstrap, CancellationToken ct)
    {
        CodepointPerfcache.LoadDefault();
        var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest);
        var inner = new NpgsqlSubstrateWriter(ds, diagnostics, durability: PostgresWriteDurability.Synchronous);
        var writer = new ConsensusAccumulatingWriter(
            inner, ds, logger: diagnostics, persistEvidence: true);
        var reader = new NpgsqlSubstrateReader(ds);

        if (bootstrap) await BootstrapSourcesAsync(ds, writer, reader, ct);
        return new ChessPgnIngestor(ds, writer, reader, ownsResources: true);
    }

    /// <summary>
    /// Attach the lab PGN loop-closure lane to the Generic-Host-owned live chess runtime.
    /// The returned ingestor borrows both datasource and writer and therefore never disposes
    /// either one. This is the API-host path: one ingest pool/write spine, regardless of how
    /// many concurrent Lab jobs finish artifacts.
    /// </summary>
    public static async Task<ChessPgnIngestor> AttachAsync(
        ChessLiveGameHost host, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        CodepointPerfcache.LoadDefault();
        var ds = host.DataSource;
        var writer = host.Writer;
        var reader = new NpgsqlSubstrateReader(ds);

        await BootstrapSourcesAsync(ds, writer, reader, ct);
        return new ChessPgnIngestor(ds, writer, reader, ownsResources: false);
    }

    private static async Task BootstrapSourcesAsync(
        NpgsqlDataSource ds, ConsensusAccumulatingWriter writer, NpgsqlSubstrateReader reader,
        CancellationToken ct)
    {
        var names = await ChessVocabulary.BootstrapManyAsync(writer,
        [
            new(ChessVocabulary.PgnSourceId, "ChessPgn", ChessVocabulary.PgnTrustClass),
            new(ChessVocabulary.AnalysisSourceId, "ChessAnalysis", ChessVocabulary.AnalysisTrustClass),
            new(ChessTransitions.SourceId, "ChessTransitions", ChessTransitions.TrustClassId),
            new(ChessPositionOutcomes.SourceId, ChessPositionOutcomes.SourceName,
                ChessPositionOutcomes.TrustClassId),
            new(ChessSyzygy.SourceId, ChessSyzygy.SourceName, ChessSyzygy.TrustClassId),
            ..ChessCorpus.Names.Select(static name => new ChessVocabulary.BootstrapSource(
                ChessCorpus.SourceId(name), name, ChessVocabulary.PgnTrustClass)),
        ], ct, reader);
        await NpgsqlCanonicalRegistry.RegisterCanonicalsAsync(ds, names, ct);
    }

    internal static int ResolvedGamesPerChunk => ChunkSize;

    internal IAsyncEnumerable<ChessGameRecord> SelectNovelAsync(
        List<ChessGameRecord> games, CancellationToken ct)
        => ChessPgnDecomposer.FilterNovelAsync(games, _reader, ct);

    internal Task<Result> IngestCorpusGamesAsync(
        IEnumerable<string> games, ChessRecordingMeasurement measurement, CancellationToken ct)
    {
        if (!measurement.IsCorpus)
            throw new ArgumentException("corpus ingestion requires bound corpus provenance", nameof(measurement));
        return IngestGamesCoreAsync(games, "bound original corpus selection", null, ct, null, measurement,
            requireCompleteSource: true);
    }

    public async Task<Result> IngestFileAsync(
        string pgnPath, Action<string>? log = null, CancellationToken ct = default,
        string? experimentReceiptJson = null, bool requireCompleteSource = false)
        => await IngestGamesAsync(PgnGames.StreamGames(pgnPath), Path.GetFileName(pgnPath), log, ct,
            experimentReceiptJson, requireCompleteSource);

    public async Task<Result> IngestGamesAsync(
        IEnumerable<string> games, string sourceLabel, Action<string>? log = null,
        CancellationToken ct = default, string? experimentReceiptJson = null,
        bool requireCompleteSource = false)
        => await IngestGamesCoreAsync(games, sourceLabel, log, ct, experimentReceiptJson, null,
            requireCompleteSource);

    internal Task<Result> IngestRecordedFileAsync(string pgnPath, ChessRecordingMeasurement measurement,
        Action<string>? log, CancellationToken ct, string experimentReceiptJson)
        => IngestGamesCoreAsync(PgnGames.StreamGames(pgnPath), Path.GetFileName(pgnPath),
            log, ct, experimentReceiptJson, measurement, requireCompleteSource: true);

    private async Task<Result> IngestGamesCoreAsync(
        IEnumerable<string> games, string sourceLabel, Action<string>? log,
        CancellationToken ct, string? experimentReceiptJson, ChessRecordingMeasurement? measurement,
        bool requireCompleteSource)
    {
        var experiment = experimentReceiptJson is null ? null : ChessExperimentEvidence.Parse(experimentReceiptJson);
        measurement?.Checkpoint("gate", "waiting");
        await Gate.WaitAsync(ct);
        try
        {
            measurement?.Checkpoint("gate", "acquired");
            if (experiment is not null)
            {
                var names = await ChessVocabulary.BootstrapAsync(_writer,
                    ChessExperimentEvidence.SourceId, ChessExperimentEvidence.SourceName,
                    ChessExperimentEvidence.TrustClassId, ct, _reader);
                await NpgsqlCanonicalRegistry.RegisterCanonicalsAsync(_ds, names, ct);
            }
            long recordStarted = Stopwatch.GetTimestamp();
            double otherBefore = (measurement?.ElapsedSeconds.Commit ?? 0) + (measurement?.ElapsedSeconds.Readback ?? 0);
            try
            {
                using var sourcePhase = measurement?.MeasurePhase(
                    ChessRecordingMeasurement.WorkPhase.SourceReadParseAndValidation);
                int parsed = 0, novel = 0, applied = 0, repaired = 0, verified = 0;
                long verifiedPlies = 0;
                int parseWindow = measurement?.NextReplayChunkGames ?? ChunkSize;
                var chunk = new List<ChessGameRecord>(Math.Max(1, parseWindow));

                foreach (var gameText in games)
                {
                    ct.ThrowIfCancellationRequested();
                    if (parseWindow == 0)
                        throw new InvalidDataException("corpus replay has source games beyond its sealed chunk sequence");
                    experiment?.ValidateGame(gameText);
                    if (measurement is not null) measurement.Work.ParseAttempts++;
                    measurement?.Checkpoint("SourceReadParseAndValidation", "parse-entered", periodic: true,
                        chunkGames: chunk.Count);
                    if (ChessPgnDecomposer.TryParseGame(gameText,
                        requireNormalCompletion: measurement?.RequiresNormalCompletion == true,
                        requireCompleteSource: requireCompleteSource || measurement?.IsCorpus == true) is not { } parsedGame)
                    {
                        if (measurement is not null) measurement.Work.ParseRejected++;
                        measurement?.Checkpoint("SourceReadParseAndValidation", "parse-progress", periodic: true);
                        continue;
                    }
                    var game = ChessPgnDecomposer.WithCorpus(parsedGame, sourceLabel);
                    measurement?.ObserveParsed(game);
                    parsed++;
                    chunk.Add(game);
                    measurement?.Checkpoint("SourceReadParseAndValidation", "parse-progress", periodic: true,
                        chunkGames: chunk.Count);
                    if (chunk.Count < parseWindow) continue;
                    measurement?.Checkpoint("SourceReadParseAndValidation", "chunk-parsed", chunkGames: chunk.Count);
                    (int n, int a, int r) = await ApplyChunkAsync(chunk, ct, experiment, measurement, log);
                    novel += n; applied += a; repaired += r;
                    if (requireCompleteSource && measurement is null)
                    {
                        await VerifyPersistedCompleteGamesAsync(chunk, (n, a, r), ct);
                        verified += chunk.Count;
                        verifiedPlies += chunk.Sum(static game => (long)game.MoveIds.Length);
                        log?.Invoke($"verified persisted witness + typed move trajectory for {chunk.Count} provider games");
                    }
                    chunk.Clear();
                    parseWindow = measurement?.NextReplayChunkGames ?? ChunkSize;
                }
                measurement?.Checkpoint("SourceReadParseAndValidation", "source-enumeration-complete", chunkGames: chunk.Count);
                if (chunk.Count > 0)
                {
                    (int n, int a, int r) = await ApplyChunkAsync(chunk, ct, experiment, measurement, log);
                    novel += n; applied += a; repaired += r;
                    if (requireCompleteSource && measurement is null)
                    {
                        await VerifyPersistedCompleteGamesAsync(chunk, (n, a, r), ct);
                        verified += chunk.Count;
                        verifiedPlies += chunk.Sum(static game => (long)game.MoveIds.Length);
                        log?.Invoke($"verified persisted witness + typed move trajectory for {chunk.Count} provider games");
                    }
                }

                log?.Invoke($"ingested {applied}/{parsed} new games from {sourceLabel}"
                            + (parsed > novel ? $" ({parsed - novel} already present)" : "")
                            + (repaired > 0
                                ? $"; repaired current testimony across {repaired} already-recorded games"
                                : "")
                            + (verified > 0
                                ? $"; exact persisted readback verified {verified} games / {verifiedPlies} plies"
                                : ""));
                var result = new Result(parsed, novel, applied, repaired, verified, verifiedPlies);
                measurement?.ObserveResult(result);
                return result;
            }
            finally
            {
                if (measurement is not null)
                    measurement.ElapsedSeconds.Recording += Math.Max(0, Stopwatch.GetElapsedTime(recordStarted).TotalSeconds
                        - (measurement.ElapsedSeconds.Commit + measurement.ElapsedSeconds.Readback - otherBefore));
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task VerifyPersistedCompleteGamesAsync(
        IReadOnlyList<ChessGameRecord> games, (int Novel, int Applied, int Repaired) apply, CancellationToken ct)
    {
        if (games.Count == 0) return;

        var witnessIds = games.Select(ChessPgnDecomposer.RecordingWitnessId).Distinct().ToArray();
        var present = await _reader.PresentAttestationIdsAsync(
            ChessVocabulary.PlaysLineType, witnessIds, ct).ConfigureAwait(false);
        foreach (var witnessId in witnessIds)
            if (!present.Contains(witnessId))
                throw new InvalidDataException(
                    $"provider game commit acknowledged but recording witness {Convert.ToHexStringLower(witnessId.ToBytes())} is absent on exact readback "
                    + $"(chunk of {games.Count} games: {apply.Novel} novel, {apply.Applied} applied, {apply.Repaired} repaired; {present.Count} of {witnessIds.Length} witnesses present)");

        if (await PersistedTrajectoryMismatchAsync(games, ct).ConfigureAwait(false) is { } mismatch)
            throw new InvalidDataException(mismatch);

        // Exercise the same reconstruction used by browsing and later analysis.
        // Matching line hashes alone cannot prove that rules and state can be recovered.
        var hydrated = await ChessWitnessHydrator.TryHydrateChunkAsync(
            _ds, games.Select(g => g.PlayingId).Distinct().ToArray(), ct).ConfigureAwait(false);
        var byPlaying = hydrated.ToDictionary(g => g.PlayingId);
        foreach (var expected in games)
        {
            if (!byPlaying.TryGetValue(expected.PlayingId, out var actual)
                || actual.LineId != expected.LineId || actual.Result != expected.Result
                || !actual.MoveIds.SequenceEqual(expected.MoveIds)
                || (actual.Rules ?? ChessVariants.Standard) != (expected.Rules ?? ChessVariants.Standard))
                throw new InvalidDataException($"provider game {expected.PlayingId} could not reconstruct its complete recorded state and moves");
        }
    }

    private async Task<string?> PersistedTrajectoryMismatchAsync(
        IReadOnlyList<ChessGameRecord> games, CancellationToken ct)
    {
        if (games.Count == 0) return null;
        // A legitimate zero-ply resignation/timeout has a PLAYING witness and the
        // initial position as its line identity, but deliberately owns no move carrier.
        // Only non-empty source lines are required to round-trip a Content trajectory.
        var expectedLines = games
            .Where(static game => game.MoveIds.Length > 0)
            .GroupBy(static game => game.LineId)
            .ToDictionary(static group => group.Key, static group => group.First().MoveIds);
        if (expectedLines.Count == 0) return null;
        var lineIds = expectedLines.Keys.Select(static id => id.ToBytes()).ToArray();
        var rows = await NpgsqlSubstrateReads.TypedTrajectoryConstituentsAsync(
            _ds, lineIds, [PhysicalityType.Content], ct).ConfigureAwait(false);

        foreach (var (lineId, expectedMoves) in expectedLines)
        {
            var game = games.First(candidate => candidate.LineId == lineId);
            var actual = rows
                .Where(row => row.Type == PhysicalityType.Content
                              && Hash128.FromBytes(row.ParentId) == lineId)
                .OrderBy(static row => row.Ordinal)
                .ToArray();
            int expectedConstituents = expectedMoves.Length + 1;
            if (actual.Length != expectedConstituents)
                return $"provider game line {Convert.ToHexStringLower(lineId.ToBytes())} persisted {actual.Length} content constituents; expected start + {expectedMoves.Length} moves";

            if (Hash128.FromBytes(actual[0].EntityId) != game.PositionIds[0])
                return $"provider game line {Convert.ToHexStringLower(lineId.ToBytes())} failed exact start-position readback";

            for (int i = 0; i < expectedMoves.Length; i++)
                if (Hash128.FromBytes(actual[i + 1].EntityId) != expectedMoves[i])
                    return $"provider game line {Convert.ToHexStringLower(lineId.ToBytes())} move {i + 1} failed exact typed-trajectory readback";
        }
        return null;
    }
}
