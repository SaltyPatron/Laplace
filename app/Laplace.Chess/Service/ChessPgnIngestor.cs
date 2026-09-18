using System.Collections.Immutable;
using System.Diagnostics;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
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
public sealed class ChessPgnIngestor : IAsyncDisposable
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

    public readonly record struct Result(int Parsed, int Novel, int Applied);
    public readonly record struct ProfileResult(int Profiles, int Players, int Links);

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
                int parsed = 0, novel = 0, applied = 0, repaired = 0;
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
                        requireCompleteSource: requireCompleteSource || measurement?.IsCorpus == true) is not { } game)
                    {
                        if (measurement is not null) measurement.Work.ParseRejected++;
                        measurement?.Checkpoint("SourceReadParseAndValidation", "parse-progress", periodic: true);
                        continue;
                    }
                    measurement?.ObserveParsed(game);
                    parsed++;
                    chunk.Add(game);
                    measurement?.Checkpoint("SourceReadParseAndValidation", "parse-progress", periodic: true,
                        chunkGames: chunk.Count);
                    if (chunk.Count < parseWindow) continue;
                    measurement?.Checkpoint("SourceReadParseAndValidation", "chunk-parsed", chunkGames: chunk.Count);
                    (int n, int a, int r) = await ApplyChunkAsync(chunk, ct, experiment, measurement);
                    novel += n; applied += a; repaired += r;
                    chunk.Clear();
                    parseWindow = measurement?.NextReplayChunkGames ?? ChunkSize;
                }
                measurement?.Checkpoint("SourceReadParseAndValidation", "source-enumeration-complete", chunkGames: chunk.Count);
                if (chunk.Count > 0)
                {
                    (int n, int a, int r) = await ApplyChunkAsync(chunk, ct, experiment, measurement);
                    novel += n; applied += a; repaired += r;
                }

                log?.Invoke($"ingested {applied}/{parsed} new games from {sourceLabel}"
                            + (parsed > novel ? $" ({parsed - novel} already present)" : "")
                            + (repaired > 0
                                ? $"; repaired current testimony across {repaired} already-recorded games"
                                : ""));
                var result = new Result(parsed, novel, applied);
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

    public async Task<ProfileResult> IngestPlayerProfilesAsync(
        IReadOnlyList<ChessPlayerProfile> profiles, CancellationToken ct = default)
    {
        if (profiles.Count == 0) return default;
        await Gate.WaitAsync(ct);
        try
        {
            int players = 0;
            var identityLinks = new HashSet<(Hash128 Subject, Hash128 Object, Hash128 Source)>();
            var planned = new List<(ChessPlayerProfile Profile, Hash128 PlayerId,
                Hash128 SourceId, double Weight, SubstrateChangeBuilder Builder)>();

            // Bootstrap each provider source once, then register the union in one set call.
            // The former placement inside this loop performed the same source existence
            // probe and register_canonicals command once per player in a top-N ingest.
            var providerSources = profiles
                .Select(static profile => ProfileSource(profile.Provider))
                .DistinctBy(static source => source.SourceId)
                .ToArray();
            var canonicalNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in providerSources)
                canonicalNames.UnionWith(await ChessVocabulary.BootstrapAsync(
                    _writer, source.SourceId, source.Name, source.TrustClass, ct, _reader));
            await NpgsqlCanonicalRegistry.RegisterCanonicalsAsync(_ds, canonicalNames, ct);

            foreach (var profile in profiles)
            {
                if (profile.Provider.Equals("fide", StringComparison.OrdinalIgnoreCase)
                    && (profile.ProviderId.Length is < 4 or > 12 || !profile.ProviderId.All(char.IsDigit)))
                    throw new InvalidDataException(
                        $"FIDE provider identity must be a 4-12 digit FIDE id, got '{profile.ProviderId}'.");

                var (sourceId, _, _, weight) = ProfileSource(profile.Provider);

                var b = new SubstrateChangeBuilder(sourceId,
                    $"chess/player-profile/{profile.Provider}/{ChessGameFetcher.Sanitize(profile.ProviderId)}")
                    .DeclareSourcePrior(weight);
                string identityName = profile.Provider.Equals("fide", StringComparison.OrdinalIgnoreCase)
                    ? profile.DisplayName : profile.ProviderId;
                var playerId = ChessVocabulary.PlayerId(identityName);
                ChessVocabulary.EmitPlayer(b, playerId, identityName, sourceId, weight);
                players++;

                // Provider-reported display/real names are attributable aliases on THIS
                // provider identity. A matching human-readable name is candidate referential
                // evidence, not permission to mint a second player and assert identity.
                foreach (string alias in profile.Aliases
                             .Append(profile.DisplayName)
                             .Append(profile.RealName ?? "")
                             .Where(static x => !string.IsNullOrWhiteSpace(x))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                    ChessVocabulary.EmitPlayer(b, playerId, alias, sourceId, weight);

                AddProfileValue(b, playerId, ChessVocabulary.ExternalIdType,
                    $"{profile.Provider}:{profile.ProviderId}", sourceId, weight);
                AddProfileValue(b, playerId, ChessVocabulary.FeatureType, profile.Biography, sourceId, weight, "bio");
                AddProfileValue(b, playerId, ChessVocabulary.FeatureType, profile.Title, sourceId, weight, "title");
                AddProfileValue(b, playerId, ChessVocabulary.FeatureType, profile.Federation, sourceId, weight, "federation");
                AddProfileValue(b, playerId, ChessVocabulary.FeatureType, profile.AvatarUrl, sourceId, weight, "avatar");
                foreach (var link in profile.Links)
                    AddProfileValue(b, playerId, ChessVocabulary.FeatureType, link, sourceId, weight, "link");
                foreach (var (kind, rating) in profile.Ratings)
                    AddProfileValue(b, playerId, ChessVocabulary.HasRatingType,
                        $"{kind}:{rating}", sourceId, weight);
                foreach (var (kind, value) in profile.Facts)
                    AddProfileValue(b, playerId, ChessVocabulary.FeatureType,
                        value, sourceId, weight, $"fact:{kind}");

                planned.Add((profile, playerId, sourceId, weight, b));
            }

            // Cross-provider identity is asserted only when the caller explicitly supplied
            // one FIDE profile together with the online profile. Name equality / RealName
            // never creates this edge: equal names produce candidate referents, not identity.
            var fideProfiles = planned.Where(static p =>
                p.Profile.Provider.Equals("fide", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (fideProfiles.Length == 1)
            {
                var fide = fideProfiles[0].PlayerId;
                foreach (var online in planned)
                {
                    if (online.Profile.Provider.Equals("fide", StringComparison.OrdinalIgnoreCase)) continue;
                    if (identityLinks.Add((online.PlayerId, fide, online.SourceId)))
                    {
                        var associationContext = ContentEmitter.Emit(
                            online.Builder,
                            $"explicit-profile-association:{online.Profile.Provider}:{online.Profile.ProviderId}:fide:{fideProfiles[0].Profile.ProviderId}",
                            online.SourceId)
                            ?? throw new InvalidOperationException(
                                "could not materialize explicit profile-association context");
                        online.Builder.AddAttestation(NativeAttestation.CategoricalResolved(
                            online.PlayerId, ChessVocabulary.CorrespondsToType, fide,
                            online.SourceId, associationContext, online.Weight));
                    }
                }
            }

            var built = new List<SubstrateChange>(planned.Count);
            foreach (var item in planned) built.Add(await item.Builder.BuildAsync(ct));

            // Profile refresh is a deterministic observation, not another vote. The
            // accumulating writer quite correctly treats every submitted attestation as a
            // witness, so suppress exact evidence IDs already present before they reach it.
            // A changed provider fact/rating has a different content object and therefore a
            // different attestation ID; it is admitted while an identical button press is not.
            var candidateAttestations = built.SelectMany(static change => change.Attestations).ToArray();
            var present = await ReadPresentAttestationIdsAsync(candidateAttestations, ct);
            var changes = built.Select(change => change with
            {
                Attestations = change.Attestations.Where(row => !present.Contains(row.Id)).ToImmutableArray(),
                CountsAsUnit = false,
            }).ToArray();
            await _writer.ApplyManyAsync(changes, ct);
            return new ProfileResult(profiles.Count, players, identityLinks.Count);
        }
        finally { Gate.Release(); }
    }

    private static void AddProfileValue(
        SubstrateChangeBuilder b, Hash128 playerId, Hash128 typeId, string? value,
        Hash128 sourceId, double weight, string? prefix = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        string token = prefix is null ? value.Trim() : $"{prefix}:{value.Trim()}";
        if (ContentEmitter.Emit(b, token, sourceId) is { } valueId)
            b.AddAttestation(NativeAttestation.CategoricalResolved(
                playerId, typeId, valueId, sourceId, null, weight));
    }

    private static (Hash128 SourceId, string Name, Hash128 TrustClass, double Weight) ProfileSource(string provider)
        => provider.ToLowerInvariant() switch
        {
            "lichess" => (ChessVocabulary.LichessProfileSourceId, "LichessPlayerProfile",
                ChessVocabulary.OnlineProfileTrustClass, SourceTrust.StructuredCorpus),
            "chesscom" => (ChessVocabulary.ChessComProfileSourceId, "ChessComPlayerProfile",
                ChessVocabulary.OnlineProfileTrustClass, SourceTrust.StructuredCorpus),
            "fide" => (ChessVocabulary.FideProfileSourceId, "FidePlayerProfile",
                ChessVocabulary.FideProfileTrustClass, SourceTrust.StandardsDerived),
            _ => throw new ArgumentException($"unsupported chess profile provider '{provider}'"),
        };

    private async Task<(int Novel, int Applied, int Repaired)> ApplyChunkAsync(
        List<ChessGameRecord> chunk, CancellationToken ct, ChessExperimentEvidence? experiment = null,
        ChessRecordingMeasurement? measurement = null)
    {
        // Keep one set-sized novelty probe per parsed window. Admission is then split
        // using actual staged observations without parsing or composing a game twice.
        var novelIds = new HashSet<Hash128>();
        using (measurement?.MeasurePhase(ChessRecordingMeasurement.WorkPhase.CompositionAndNoveltyProbe))
        {
            await foreach (var game in ChessPgnDecomposer.FilterNovelAsync(chunk, _reader, ct))
                novelIds.Add(game.PlayingId);
        }
        int offset = 0, totalNovel = 0, totalApplied = 0, totalRepaired = 0;
        while (offset < chunk.Count)
        {
            using var compositionPhase = measurement?.MeasurePhase(
                ChessRecordingMeasurement.WorkPhase.CompositionAndNoveltyProbe);
            using var composed = ChessPgnChunk.ComposeNext(chunk, novelIds, ref offset,
                ChunkStagedBytes, measurement?.NextReplayChunkGames, measurement, ct);
            var result = await ApplyComposedChunkAsync(composed, ct, experiment, measurement);
            composed.ForgetCommittedNovelty(novelIds);
            totalNovel += result.Novel;
            totalApplied += result.Applied;
            totalRepaired += result.Repaired;
        }
        return (totalNovel, totalApplied, totalRepaired);
    }

    private async Task<(int Novel, int Applied, int Repaired)> ApplyComposedChunkAsync(
        ChessPgnChunk composed, CancellationToken ct, ChessExperimentEvidence? experiment,
        ChessRecordingMeasurement? measurement)
    {
        var chunk = composed.Games;
        var record = composed.Record;
        var analyze = composed.Analyze;
        var repair = composed.Repair;
        var repairAnalyze = composed.RepairAnalyze;
        int novel = composed.NovelGames;
        var repairPlayings = composed.RepairPlayings;
        var observedPositions = composed.ObservedPositions;
        var observedMoves = composed.ObservedMoves;
        measurement?.Checkpoint("CompositionAndNoveltyProbe", "composition-complete", chunkGames: chunk.Count);
        var ownedChanges = new List<SubstrateChange>(5);
        try
        {
            var changes = new List<SubstrateChange>(5);
            var expectedWitnesses = new List<AttestationRow>();
            var expectedCarriers = new List<PhysicalityRow>();
            var expectedEntities = new List<EntityRow>();
            SubstrateChange? experimentChange = null;
            int repairedGames = 0;
            using (measurement?.MeasurePhase(ChessRecordingMeasurement.WorkPhase.ChangeMaterializationAndWitnessPreparation))
            {
                var selectedPlayings = measurement is null ? null : chunk.Select(g => g.PlayingId).ToHashSet();
                var selectedLines = measurement is null ? null : chunk.Where(g => g.MoveIds.Length > 0).Select(g => g.LineId).ToHashSet();
                if (novel > 0)
                {
                    var recorded = await record.BuildAsync(ct);
                    ownedChanges.Add(recorded);
                    changes.Add(recorded);
                    if (selectedPlayings is not null)
                    {
                        if (measurement?.RetainedPgn == true) expectedEntities.AddRange(recorded.Entities);
                        expectedWitnesses.AddRange(recorded.Attestations.Where(a => ChessRecordingMeasurement.IsGameWitness(a, selectedPlayings)));
                        expectedCarriers.AddRange(recorded.Physicalities.Where(p => p.Type == PhysicalityType.Content && selectedLines!.Contains(p.EntityId)));
                    }
                    var analyzed = await analyze.BuildAsync(ct);
                    ownedChanges.Add(analyzed);
                    changes.Add(analyzed);
                }

                if (repairPlayings.Count > 0)
                {
                    var repairBuilt = await repair.BuildAsync(ct);
                    ownedChanges.Add(repairBuilt);
                    if (selectedPlayings is not null)
                    {
                        if (measurement?.RetainedPgn == true) expectedEntities.AddRange(repairBuilt.Entities);
                        expectedWitnesses.AddRange(repairBuilt.Attestations.Where(a => ChessRecordingMeasurement.IsGameWitness(a, selectedPlayings)));
                        expectedCarriers.AddRange(repairBuilt.Physicalities.Where(p => p.Type == PhysicalityType.Content && selectedLines!.Contains(p.EntityId)));
                    }
                    // Explicit retained-scope verification reads the original sealed game
                    // testimony. New calculated/completion metadata is outside that scope;
                    // ordinary ingestion still discovers and admits those repairs below.
                    if (measurement?.RequireNoWriterWork != true)
                    {
                        var repairAnalyzed = await repairAnalyze.BuildAsync(ct);
                        ownedChanges.Add(repairAnalyzed);
                        var present = await ReadPresentAttestationIdsAsync(
                            repairBuilt.Attestations.Concat(repairAnalyzed.Attestations).ToArray(), ct);
                        foreach (var candidate in new[] { repairBuilt, repairAnalyzed })
                        {
                            if (MissingWitnesses(candidate, present) is not { } repairChange) continue;
                            changes.Add(repairChange);
                            // Calculated testimony may be shared by several playings. Report
                            // the repaired window size without inventing per-game attribution.
                            repairedGames = repairPlayings.Count;
                        }
                    }
                }

                // Metadata belongs to every selected playing, including a re-ingest whose PGN
                // already exists. Exact attestation probes suppress repeated witnessing while
                // allowing older games to acquire their previously missing experiment receipt.
                if (experiment is not null)
                {
                    var evidence = await experiment.BuildChangeAsync(chunk, ct);
                    ownedChanges.Add(evidence);
                    experimentChange = evidence;
                    var present = await ReadPresentAttestationIdsAsync(evidence.Attestations, ct);
                    var missing = evidence.Attestations.Where(row => !present.Contains(row.Id)).ToImmutableArray();
                    if (missing.Length > 0) changes.Add(evidence with { Attestations = missing });
                }
            }
            measurement?.ObserveBuiltChanges(changes);
            measurement?.Checkpoint("ChangeMaterializationAndWitnessPreparation", "changes-built");

            ChessRecordingMeasurement.ScopeRequest? scope = null;
            if (measurement is { RetainedPgn: true })
            {
                using var scopePhase = measurement.MeasurePhase(ChessRecordingMeasurement.WorkPhase.BeforeScopeProbe);
                scope = measurement.RequireNoWriterWork
                    ? await measurement.ReadRetainedScopeBeforeAsync(_ds,
                        expectedEntities, expectedWitnesses, expectedCarriers, ct)
                    : await measurement.ReadScopeBeforeAsync(_ds,
                    expectedEntities.Concat(experimentChange is null
                        ? Enumerable.Empty<EntityRow>() : experimentChange.Entities).ToArray(),
                    expectedWitnesses.Concat(experimentChange is null
                        ? Enumerable.Empty<AttestationRow>() : experimentChange.Attestations).DistinctBy(a => a.Id).ToArray(),
                    expectedCarriers, ct);
            }

            RequireWritableAdmission(measurement?.RequireNoWriterWork == true, changes.Count);
            if (changes.Count > 0)
            {
                using var writerPhase = measurement?.MeasurePhase(ChessRecordingMeasurement.WorkPhase.WriterApply);
                if (measurement is not null) measurement.Work.WriterApplyAttempts++;
                measurement?.Checkpoint("WriterApply", "writer-entered");
                long commitStarted = Stopwatch.GetTimestamp();
                // Shared hosts may update the same writer outside this PGN lane.
                // Only an owned writer permits attribution to this apply window.
                var backendBefore = measurement is not null && _ownsResources
                    ? ChessRecordingMeasurement.ConsensusBackendSnapshot.Read(_writer)
                    : (ChessRecordingMeasurement.ConsensusBackendSnapshot?)null;
                try
                {
                    var result = await _writer.ApplyManyAsync(changes, ct);
                    measurement?.ObserveCommit(result);
                    measurement?.Checkpoint("WriterApply", "writer-acknowledged");
                }
                finally
                {
                    if (backendBefore is { } before)
                        measurement!.ObserveConsensusBackend(before,
                            ChessRecordingMeasurement.ConsensusBackendSnapshot.Read(_writer));
                    if (measurement is not null)
                        measurement.ElapsedSeconds.Commit += Stopwatch.GetElapsedTime(commitStarted).TotalSeconds;
                }
            }
            if (measurement?.RequireNoWriterWork != true
                && (observedPositions.Count > 0 || observedMoves.Count > 0))
                ChessTransitionObservations.MarkObserved(observedPositions, observedMoves);
            if (measurement is not null)
            {
                using var readbackPhase = measurement.MeasurePhase(ChessRecordingMeasurement.WorkPhase.ExactReadback);
                await measurement.VerifyChunkAsync(_ds, chunk, expectedWitnesses, expectedCarriers,
                    experimentChange, experiment, ct);
                measurement.Checkpoint("ExactReadback", "readback-returned");
            }
            if (scope is not null)
            {
                using var scopePhase = measurement!.MeasurePhase(ChessRecordingMeasurement.WorkPhase.AfterScopeProbe);
                await measurement.ObserveScopeAfterAsync(_ds, scope, ct);
            }
            if (measurement?.IsCorpus == true)
            {
                using var evidencePhase = measurement.MeasurePhase(ChessRecordingMeasurement.WorkPhase.ChunkEvidenceOutput);
                await measurement.FlushCorpusChunkAsync(novel, ct);
            }
            if (measurement is not null) measurement.Work.ChunksVerified++;
            measurement?.Checkpoint("ChunkEvidenceOutput", "chunk-sealed");
            return (novel, novel, repairedGames);
        }
        finally
        {
            // All writer and readback awaits have returned. Include repair changes
            // filtered down to no work: their native source stages still had an owner.
            foreach (var change in ownedChanges)
                foreach (var stage in change.IntentStages) stage.Dispose();
        }
    }

    internal static void RequireWritableAdmission(bool requireNoWriterWork, int changes)
    {
        if (requireNoWriterWork && changes != 0)
            throw new InvalidDataException("recorded selection is incomplete or requires repair; verification cannot write");
    }

    internal static SubstrateChange? MissingWitnesses(
        SubstrateChange change, IReadOnlySet<Hash128> present)
    {
        var missing = change.Attestations.Where(row => !present.Contains(row.Id)).ToImmutableArray();
        if (missing.Length == 0) return null;
        // Preserve every canonical carrier/stage and its owning source. Only accepted
        // testimony is suppressed; derived lanes are as recoverable as recorded headers.
        return change with { Attestations = missing, CountsAsUnit = false };
    }

    private async Task<HashSet<Hash128>> ReadPresentAttestationIdsAsync(
        IReadOnlyList<AttestationRow> rows, CancellationToken ct)
    {
        var present = new HashSet<Hash128>();
        foreach (var group in rows.GroupBy(static row => row.TypeId))
            present.UnionWith(await _reader.PresentAttestationIdsAsync(
                group.Key, group.Select(static row => row.Id).Distinct().ToArray(), ct));
        return present;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_ownsResources) return;
        await _writer.DisposeAsync();
        await _ds.DisposeAsync();
    }
}
