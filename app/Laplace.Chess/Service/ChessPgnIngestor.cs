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
/// Novelty-gated on content-addressed game ids, so re-ingesting an artifact is a no-op.
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
        var names = new HashSet<string>();
        names.UnionWith(await ChessVocabulary.BootstrapAsync(
            writer, ChessVocabulary.PgnSourceId, "ChessPgn", ChessVocabulary.PgnTrustClass,
            ct, reader));
        names.UnionWith(await ChessVocabulary.BootstrapAsync(
            writer, ChessVocabulary.AnalysisSourceId, "ChessAnalysis", ChessVocabulary.AnalysisTrustClass,
            ct, reader));
        names.UnionWith(await ChessVocabulary.BootstrapAsync(
            writer, ChessTransitions.SourceId, "ChessTransitions", ChessTransitions.TrustClassId,
            ct, reader));
        await NpgsqlCanonicalRegistry.RegisterCanonicalsAsync(ds, names, ct);
    }

    public async Task<Result> IngestFileAsync(
        string pgnPath, Action<string>? log = null, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            int parsed = 0, novel = 0, applied = 0;
            var chunk = new List<ChessGameRecord>(ChunkSize);

            foreach (var gameText in PgnGames.StreamGames(pgnPath))
            {
                ct.ThrowIfCancellationRequested();
                if (ChessPgnDecomposer.TryParseGame(gameText) is not { } game) continue;
                parsed++;
                chunk.Add(game);
                if (chunk.Count < ChunkSize) continue;
                (int n, int a) = await ApplyChunkAsync(chunk, ct);
                novel += n; applied += a;
                chunk.Clear();
            }
            if (chunk.Count > 0)
            {
                (int n, int a) = await ApplyChunkAsync(chunk, ct);
                novel += n; applied += a;
            }

            log?.Invoke($"ingested {applied}/{parsed} games from {Path.GetFileName(pgnPath)}"
                        + (parsed > novel ? $" ({parsed - novel} already present)" : ""));
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
            foreach (var profile in profiles)
            {
                var (sourceId, sourceName, trustClass, weight) = ProfileSource(profile.Provider);
                var names = await ChessVocabulary.BootstrapAsync(
                    _writer, sourceId, sourceName, trustClass, ct, _reader);
                await NpgsqlCanonicalRegistry.RegisterCanonicalsAsync(_ds, names, ct);

                var b = new SubstrateChangeBuilder(sourceId,
                    $"chess/player-profile/{profile.Provider}/{ChessGameFetcher.Sanitize(profile.ProviderId)}");
                string identityName = profile.Provider.Equals("fide", StringComparison.OrdinalIgnoreCase)
                    ? profile.DisplayName : profile.ProviderId;
                var playerId = ChessVocabulary.PlayerId(identityName);
                ChessVocabulary.EmitPlayer(b, playerId, identityName, sourceId, weight);
                players++;

                foreach (string alias in new[] { profile.DisplayName, profile.RealName }
                             .OfType<string>().Where(static x => !string.IsNullOrWhiteSpace(x)))
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

                if (!string.IsNullOrWhiteSpace(profile.RealName)
                    && !PlayerAlias.Canonical(profile.RealName).Equals(
                        PlayerAlias.Canonical(identityName), StringComparison.Ordinal))
                {
                    var realId = ChessVocabulary.PlayerId(profile.RealName);
                    ChessVocabulary.EmitPlayer(b, realId, profile.RealName, sourceId, weight);
                    if (identityLinks.Add((playerId, realId, sourceId)))
                        b.AddAttestation(NativeAttestation.CategoricalResolved(
                            playerId, ChessVocabulary.CorrespondsToType, realId,
                            sourceId, null, weight));
                }

                planned.Add((profile, playerId, sourceId, weight, b));
            }

            // A provider account plus one explicitly selected FIDE identity is one ingest
            // fact. Put the bridge in the provider's original change so evidence, profile
            // metadata and association commit and fold together. The former second apply
            // reused the same source-unit label after the profile had already committed;
            // the bridge could disappear while the job still reported "2 profiles".
            var fideProfiles = planned.Where(static p =>
                p.Profile.Provider.Equals("fide", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (fideProfiles.Length == 1)
            {
                var fide = fideProfiles[0].PlayerId;
                foreach (var online in planned)
                {
                    if (online.Profile.Provider.Equals("fide", StringComparison.OrdinalIgnoreCase)) continue;
                    if (identityLinks.Add((online.PlayerId, fide, online.SourceId)))
                        online.Builder.AddAttestation(NativeAttestation.CategoricalResolved(
                            online.PlayerId, ChessVocabulary.CorrespondsToType, fide,
                            online.SourceId, null, online.Weight));
                }
            }

            var changes = new List<SubstrateChange>(planned.Count);
            foreach (var item in planned) changes.Add(await item.Builder.BuildAsync(ct));
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

    private async Task<(int Novel, int Applied)> ApplyChunkAsync(
        List<ChessGameRecord> chunk, CancellationToken ct)
    {
        // Batch the whole chunk into two builders — witnessed layer (ChessPgn) and calculated
        // layer (ChessAnalysis, derived from the in-memory parse) — and apply each ONCE.
        // Per-game round-trips against a bulk writer are the wrong point for that algorithm.
        var record = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "chess/lab/ingest");
        var analyze = new SubstrateChangeBuilder(ChessVocabulary.AnalysisSourceId, "chess/lab/ingest");
        int novel = 0;
        var observedPositions = new HashSet<Hash128>();
        await foreach (var game in ChessPgnDecomposer.FilterNovelAsync(chunk, _reader, ct))
        {
            novel++;
            ChessPgnDecomposer.RecordGame(game, record);
            ChessAnalyze.DeriveFromParsed(analyze, game);
            ChessTransitions.DepositFromParsed(analyze, game);
            for (int i = 0; i + 1 < game.PositionIds.Length; i++)
                observedPositions.Add(game.PositionIds[i]);
        }
        if (novel == 0) return (0, 0);

        await _writer.ApplyAsync(await record.BuildAsync(ct), ct);
        await _writer.ApplyAsync(await analyze.BuildAsync(ct), ct);
        ChessTransitionObservations.MarkObserved(observedPositions);
        return (novel, novel);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_ownsResources) return;
        await _writer.DisposeAsync();
        await _ds.DisposeAsync();
    }
}
