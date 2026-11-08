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

public sealed partial class ChessPgnIngestor
{
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
            await ApplyAdmittedAsync(changes, log: null, ct);
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
        ChessRecordingMeasurement? measurement = null, Action<string>? log = null)
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
            var result = await ApplyComposedChunkAsync(composed, ct, experiment, measurement, log);
            composed.ForgetCommittedNovelty(novelIds);
            totalNovel += result.Novel;
            totalApplied += result.Applied;
            totalRepaired += result.Repaired;
        }
        return (totalNovel, totalApplied, totalRepaired);
    }

    private async Task<(int Novel, int Applied, int Repaired)> ApplyComposedChunkAsync(
        ChessPgnChunk composed, CancellationToken ct, ChessExperimentEvidence? experiment,
        ChessRecordingMeasurement? measurement, Action<string>? log = null)
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
                        string? recordedTrajectoryMismatch = await PersistedTrajectoryMismatchAsync(
                            chunk.Where(game => repairPlayings.Contains(game.PlayingId)).ToArray(), ct);
                        foreach (var candidate in new[] { repairBuilt, repairAnalyzed })
                        {
                            // A PLAYING witness proves that the occurrence was accepted, not that
                            // its Content carrier survived an interrupted/older write. The old path
                            // only admitted repair work when testimony was missing, so a game could
                            // permanently retain headers/result while its move trajectory stayed
                            // absent. Force the recording owner back through ordinary admission when
                            // exact current readback disagrees with the source line.
                            bool structuralRepair = ReferenceEquals(candidate, repairBuilt)
                                && recordedTrajectoryMismatch is not null;
                            if (MissingWitnesses(candidate, present, structuralRepair) is not { } repairChange)
                                continue;
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
                    var result = await ApplyAdmittedAsync(changes, log, ct);
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
            if (measurement?.RequireNoWriterWork != true)
            {
                // Recording acceptance includes the durable line preimage, not only its
                // PLAYING/header testimony. One batched typed-trajectory read covers the
                // whole chunk and refuses a commit that lost start + ordered move ids.
                string? persistedMismatch = await PersistedTrajectoryMismatchAsync(chunk, ct);
                if (persistedMismatch is not null)
                    throw new InvalidDataException(persistedMismatch);
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

    /// <summary>
    /// Every admission through the shared writer. Apply transactions overlap by design
    /// (<c>laplace_apply_batch</c> takes no lock), and calculated chess testimony is shared
    /// by content: a Syzygy or position-outcome claim composed for a game is the same
    /// attestation id that a concurrent CLI material ingest, another Lab job, or a live
    /// game can land between this working set's presence probe and its COPY. The loser of
    /// that race sees 23505 on the attestation key. The CLI IngestRunner re-applies such a
    /// working set under the ingest retry policy; this lane does the same, so the re-run
    /// probes again, subtracts the row that won, and witnesses the claim once. A conflict
    /// that persists past the policy is a real defect and still surfaces.
    /// </summary>
    private async Task<ApplyResult> ApplyAdmittedAsync(
        IReadOnlyList<SubstrateChange> changes, Action<string>? log, CancellationToken ct)
    {
        var result = await TransientErrorRetryPolicy.ConcurrencyRetry.ExecuteAsync(
            token => _writer.ApplyManyAsync(changes, token), ct,
            (ex, failedAttempts, delay) =>
            {
                Interlocked.Increment(ref _concurrentLandingRetries);
                log?.Invoke($"concurrent substrate landing ({ConcurrencyDetail(ex)}); re-probing and "
                    + $"re-applying the working set (retry {failedAttempts}) after {delay.TotalMilliseconds:F0} ms");
            }).ConfigureAwait(false);
        Interlocked.Add(ref _entitiesInserted, result.EntitiesInserted);
        Interlocked.Add(ref _physicalitiesInserted, result.PhysicalitiesInserted);
        Interlocked.Add(ref _attestationsInserted, result.AttestationsInserted);
        return result;
    }

    private static string ConcurrencyDetail(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is PostgresException pg) return $"{pg.SqlState} {pg.ConstraintName ?? pg.TableName}";
        return ex.GetType().Name;
    }

    internal static void RequireWritableAdmission(bool requireNoWriterWork, int changes)
    {
        if (requireNoWriterWork && changes != 0)
            throw new InvalidDataException("recorded selection is incomplete or requires repair; verification cannot write");
    }

    internal static SubstrateChange? MissingWitnesses(
        SubstrateChange change, IReadOnlySet<Hash128> present, bool structuralRepair = false)
    {
        var missing = change.Attestations.Where(row => !present.Contains(row.Id)).ToImmutableArray();
        if (missing.Length == 0 && !structuralRepair) return null;
        // Preserve every canonical carrier/stage and its owning source. Only accepted
        // testimony is suppressed. structuralRepair deliberately permits a physicality-only
        // replay when exact source-line readback proves the accepted game carrier is incomplete.
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
