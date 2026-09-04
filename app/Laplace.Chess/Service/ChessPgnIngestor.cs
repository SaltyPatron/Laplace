using System.Collections.Immutable;
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
/// Playing novelty controls whether immutable game content/calculated lanes need to be deposited.
/// It does NOT suppress schema/identity repair of the source record. An already-present playing
/// is replayed through the current ChessPgn recorder, then only exact playing-scoped attestations
/// missing from durable evidence are admitted. This is the migration law required when the
/// playing/header/player projection evolves: rows already present are never re-witnessed, while
/// missing current-shape testimony is repaired from the source artifact that originally asserted
/// the game.
/// </summary>
public sealed class ChessPgnIngestor : IAsyncDisposable
{
    // Serialize in-process ingests; bulk CLI ingests hold the command-line mutex, this holds the
    // API process's own lane. Lab artifacts are small (tens of games) so waiting is fine.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // Games per apply. The source profile separates measured serialized game bytes from
    // resident compose bytes; the shared resolver turns both plus live RAM/topology into
    // this value. No private chess batch literal or special "fat-record" branch remains.
    private static readonly int ChunkSize =
        IngestPipelineDefaults.ResolveBatch(IngestSourceProfile.ChessPgn, null);

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

    public static async Task<ChessPgnIngestor> CreateAsync(CancellationToken ct = default)
    {
        CodepointPerfcache.LoadDefault();
        var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest);
        var inner = new NpgsqlSubstrateWriter(ds);
        var writer = new ConsensusAccumulatingWriter(
            inner, ds, persistEvidence: true);
        var reader = new NpgsqlSubstrateReader(ds);

        await BootstrapSourcesAsync(ds, writer, reader, ct);
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

    public async Task<Result> IngestFileAsync(
        string pgnPath, Action<string>? log = null, CancellationToken ct = default)
        => await IngestGamesAsync(PgnGames.StreamGames(pgnPath), Path.GetFileName(pgnPath), log, ct);

    public async Task<Result> IngestGamesAsync(
        IEnumerable<string> games, string sourceLabel, Action<string>? log = null,
        CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            int parsed = 0, novel = 0, applied = 0, repaired = 0;
            var chunk = new List<ChessGameRecord>(ChunkSize);

            foreach (var gameText in games)
            {
                ct.ThrowIfCancellationRequested();
                if (ChessPgnDecomposer.TryParseGame(gameText) is not { } game) continue;
                parsed++;
                chunk.Add(game);
                if (chunk.Count < ChunkSize) continue;
                (int n, int a, int r) = await ApplyChunkAsync(chunk, ct);
                novel += n; applied += a; repaired += r;
                chunk.Clear();
            }
            if (chunk.Count > 0)
            {
                (int n, int a, int r) = await ApplyChunkAsync(chunk, ct);
                novel += n; applied += a; repaired += r;
            }

            log?.Invoke($"ingested {applied}/{parsed} new games from {sourceLabel}"
                        + (parsed > novel ? $" ({parsed - novel} already present)" : "")
                        + (repaired > 0
                            ? $"; repaired current playing testimony for {repaired} already-present games"
                            : ""));
            return new Result(parsed, novel, applied);
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
                    $"chess/player-profile/{profile.Provider}/{ChessGameFetcher.Sanitize(profile.ProviderId)}");
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
        List<ChessGameRecord> chunk, CancellationToken ct)
    {
        // Novel content still takes the fused record+calculated path. Already-present playings
        // take a separate repair lane: rebuild the CURRENT source-record projection in memory,
        // retain only exact playing-grain attestation ids absent from durable evidence, and apply
        // those rows once. This repairs schema/identity evolution without treating a replay of the
        // same PGN as a second observation.
        var record = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "chess/lab/ingest");
        var analyze = new SubstrateChangeBuilder(ChessVocabulary.AnalysisSourceId, "chess/lab/ingest");
        var repair = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "chess/lab/repair-playing");
        int novel = 0;
        var novelIds = new HashSet<Hash128>();
        var observedPositions = new HashSet<Hash128>();
        var observedMoves = new HashSet<Hash128>();
        await foreach (var game in ChessPgnDecomposer.FilterNovelAsync(chunk, _reader, ct))
        {
            novel++;
            novelIds.Add(game.PlayingId);
            ChessPgnDecomposer.RecordGame(game, record);
            ChessAnalyze.DeriveFromParsed(analyze, game);
            ChessTransitions.DepositFromParsed(analyze, game);
            ChessPositionOutcomes.DepositFromParsed(analyze, game);
            if (ChessTablebaseRuntime.Prober is { } prober)
                ChessSyzygy.DeriveGame(analyze, ChessAnalyze.WitnessedFromParsed(game), prober);
            for (int i = 0; i + 1 < game.PositionIds.Length; i++)
                observedPositions.Add(game.PositionIds[i]);
            foreach (var moveId in game.MoveIds)
                observedMoves.Add(moveId);
        }

        var repairPlayings = new HashSet<Hash128>();
        foreach (var game in chunk)
        {
            if (novelIds.Contains(game.PlayingId)) continue;
            repairPlayings.Add(game.PlayingId);
            // RecordGame is deliberately reused so the repair derives the exact same ids and
            // fold inputs as a fresh ingest. The filter below admits only playing-scoped rows;
            // line-grain opening/move testimony is not replayed.
            ChessPgnDecomposer.RecordGame(game, repair);
        }

        var changes = new List<SubstrateChange>(3);
        if (novel > 0)
        {
            changes.Add(await record.BuildAsync(ct));
            changes.Add(await analyze.BuildAsync(ct));
        }

        int repairedGames = 0;
        if (repairPlayings.Count > 0)
        {
            var repairBuilt = await repair.BuildAsync(ct);
            var filtered = await MissingPlayingWitnessesAsync(repairBuilt, repairPlayings, ct);
            if (filtered.Change is { } repairChange)
            {
                changes.Add(repairChange);
                repairedGames = filtered.Games;
            }
        }

        if (changes.Count == 0) return (novel, 0, 0);

        await _writer.ApplyManyAsync(changes, ct);
        ChessTransitionObservations.MarkObserved(observedPositions, observedMoves);
        return (novel, novel, repairedGames);
    }

    private async Task<(SubstrateChange? Change, int Games)> MissingPlayingWitnessesAsync(
        SubstrateChange change, HashSet<Hash128> playingIds, CancellationToken ct)
    {
        var candidates = change.Attestations
            .Where(a => IsPlayingWitness(a, playingIds))
            .ToArray();
        if (candidates.Length == 0) return (null, 0);

        var present = await ReadPresentAttestationIdsAsync(candidates, ct);
        var missing = candidates.Where(a => !present.Contains(a.Id)).ToArray();
        if (missing.Length == 0) return (null, 0);

        var repairedPlayings = new HashSet<Hash128>();
        foreach (var row in missing)
        {
            if (row.ContextId is { } context && playingIds.Contains(context))
                repairedPlayings.Add(context);
            else if (row.ContextId is null && playingIds.Contains(row.SubjectId))
                repairedPlayings.Add(row.SubjectId);
        }

        // Keep the builder's entities/physicalities/content stages: an old playing can be present
        // while one current projection object (player alias, metadata token, event handle) is not.
        // Those rows are content-addressed/idempotent. Only testimony needs the exact-presence
        // filter, because attestation merge is additive and re-witnessing would be corruption.
        var filtered = change with
        {
            Attestations = missing.ToImmutableArray(),
            CountsAsUnit = false,
        };
        return (filtered, repairedPlayings.Count);
    }

    private async Task<HashSet<Hash128>> ReadPresentAttestationIdsAsync(
        IReadOnlyList<AttestationRow> rows, CancellationToken ct)
    {
        var present = new HashSet<Hash128>();
        if (rows.Count == 0) return present;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        int probeChunk = Math.Max(1,
            IngestSizing.ResolveApplyIo(IngestTopology.Current.ApplyPartitions).ProbeChunkIds);

        // type_id is the LIST partition key. Group by it before probing so PostgreSQL opens one
        // relation family at a time rather than turning a migration read into an all-partition
        // scan. Chunk width comes from the same measured apply-I/O sizing used by ingest.
        foreach (var typeGroup in rows.GroupBy(static row => row.TypeId))
        {
            var group = typeGroup.ToArray();
            for (int offset = 0; offset < group.Length; offset += probeChunk)
            {
                int count = Math.Min(probeChunk, group.Length - offset);
                var ids = new byte[count][];
                for (int i = 0; i < count; i++) ids[i] = group[offset + i].Id.ToBytes();

                var found = await NpgsqlAttestationReads.PresentIdsAsync(
                    conn, typeGroup.Key.ToBytes(), ids, ct).ConfigureAwait(false);
                foreach (var id in found)
                    present.Add(Hash128.FromBytes(id));
            }
        }
        return present;
    }

    private static bool IsPlayingWitness(AttestationRow row, HashSet<Hash128> playingIds)
    {
        if (row.ContextId is { } context && playingIds.Contains(context))
        {
            var type = row.TypeId;
            return type == ChessVocabulary.HasWhiteType
                || type == ChessVocabulary.HasBlackType
                || type == ChessVocabulary.HasEventType
                || type == ChessVocabulary.OnDateType
                || type == ChessVocabulary.EcoCodeType
                || type == ChessVocabulary.HasTerminationType
                || type == ChessVocabulary.HasResultType
                || type == ChessVocabulary.HasTimeControlType
                || type == ChessVocabulary.HasTcClassType
                || type == ChessVocabulary.HasSetupType
                || type == ChessVocabulary.HasRatingType
                || type == ChessVocabulary.OutcomeType
                || type == ChessVocabulary.PlayedByType;
        }

        // Current record grain has two structural rows with no context because the PLAYING is
        // itself their subject: playing→line and playing→event. They are safe to repair by exact
        // attestation id for the same reason as the context-bound headers above.
        return row.ContextId is null
            && playingIds.Contains(row.SubjectId)
            && (row.TypeId == ChessVocabulary.PlaysLineType
                || row.TypeId == ChessVocabulary.HasEventType);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_ownsResources) return;
        await _writer.DisposeAsync();
        await _ds.DisposeAsync();
    }
}
