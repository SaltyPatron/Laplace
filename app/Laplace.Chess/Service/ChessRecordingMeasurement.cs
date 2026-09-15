using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>
/// Transport receipt for the existing play → PGN → shared writer → native readback route.
/// Timing is deliberately separate from the canonical experiment witness. Commit measures
/// ApplyManyAsync, including its consensus continuation, rather than PostgreSQL COMMIT alone.
/// No durable-success flag is set from the ingestor's Parsed/Novel/Applied counters.
/// </summary>
internal sealed class ChessRecordingMeasurement(string experimentId, int requestedGames)
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly HashSet<Hash128> _playingIds = [];
    private readonly Dictionary<(string? White, string? Black, string Result), int> _matchGames = [];
    private bool _normalMatchVerified;
    private bool _matchVerified;
    private bool _verified;
    public string Schema => "laplace.chess-recording/v1";
    public string ExperimentId { get; } = experimentId;
    public string PgnEvent => "chess-lab/cutechess/" + ExperimentId;
    public string Status { get; private set; } = "running";
    public string? Error { get; private set; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; private set; }
    public PhaseTimes ElapsedSeconds { get; } = new();
    public int RequestedGames { get; } = requestedGames;
    public int ParsedGames { get; private set; }
    public int NovelGames { get; private set; }
    public int AppliedGames { get; private set; }
    public int CommittedGames { get; private set; }
    public int ReadbackGames => Games.Count;
    public long ReadbackPlies => Games.Sum(g => (long)g.MoveIds.Length);
    public FileIdentity? Pgn { get; private set; }
    public string? ExperimentReceiptSha256 { get; private set; }
    public string? ExperimentArtifactSha256 { get; private set; }
    public VerificationState Verification => new(
        _verified, _verified, _verified, _verified, _verified && _normalMatchVerified);
    public List<GameIdentity> Games { get; } = [];
    public WriterCounts Writer { get; } = new();

    public sealed class PhaseTimes
    {
        public double Total { get; internal set; }
        public double Play { get; internal set; }
        public double Recording { get; internal set; }
        public double Commit { get; internal set; }
        public double Readback { get; internal set; }
        public double Overhead { get; internal set; }
    }
    public sealed record FileIdentity(long Bytes, string Sha256);
    public sealed record VerificationState(bool UniquePlayingIds, bool ExactGameBodies,
        bool ExactWitnessMembership, bool ExactExperimentBody, bool CompletedGames);
    public sealed record GameIdentity(string PlayingId, string LineId, string StartPositionId,
        string[] MoveIds, string Result, string? WhitePlayerId, string? BlackPlayerId,
        string Termination);
    internal sealed record ExpectedGame(ChessGameRecord Record, Hash128? WhitePlayer, Hash128? BlackPlayer);
    public sealed class WriterCounts
    {
        public long ApplyCalls { get; internal set; }
        public long EntitiesAttempted { get; internal set; }
        public long EntitiesInserted { get; internal set; }
        public long PhysicalitiesAttempted { get; internal set; }
        public long PhysicalitiesInserted { get; internal set; }
        public long AttestationsAttempted { get; internal set; }
        public long AttestationsInserted { get; internal set; }
        public long EntitiesSkippedAtMerge { get; internal set; }
        public long PhysicalitiesSkippedAtMerge { get; internal set; }
        public long RoundTrips { get; internal set; }
        public long JournalReplayHits { get; internal set; }
    }

    internal async Task IdentifyPgnAsync(string path, string experimentJson, CancellationToken ct)
    {
        var evidence = ChessExperimentEvidence.Parse(experimentJson);
        if (evidence.ExperimentId != ExperimentId || evidence.PgnEvent != PgnEvent)
            throw new InvalidDataException("recording experiment identity does not match the canonical receipt");
        ExperimentReceiptSha256 = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(evidence.ReceiptJson)));
        await using var file = File.OpenRead(path);
        Pgn = new(file.Length, Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)));
    }

    internal async Task VerifyPgnUnchangedAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        if (Pgn is null || file.Length != Pgn.Bytes
            || Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)) != Pgn.Sha256)
            throw new InvalidDataException("PGN input changed during recording/readback");
    }

    internal async Task IdentifyFinalExperimentAsync(string path)
    {
        await using var file = File.OpenRead(path);
        ExperimentArtifactSha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(file));
    }

    internal void ValidateMatch(CutechessExperimentReceipt receipt)
    {
        if (receipt.ExperimentId != ExperimentId || receipt.MatchState != ChessLabJobState.Completed
            || receipt.ArtifactIdentitiesUnchanged != true || receipt.Games.Count != RequestedGames
            || receipt.Games.Select(g => g.Index).Distinct().Count() != RequestedGames || receipt.Command is null)
            throw new InvalidDataException("recording requires a verified completed match and exact game inventory");
        _normalMatchVerified = !receipt.Command.Arguments.Any(a => a is "-maxmoves" or "-draw" or "-resign")
            && receipt.Games.All(g => IsNormalResult(g.Result));
        foreach (var game in receipt.Games)
        {
            var key = (game.White, game.Black, game.Result.Split(' ')[0]);
            _matchGames[key] = _matchGames.GetValueOrDefault(key) + 1;
        }
        _matchVerified = true;
    }

    // These are the standard-board result spellings emitted by the official CuteChess
    // WesternBoard::result. A timeout, crash, disconnection, resignation or adjudication
    // cannot silently count as a completed normal game in this measurement.
    internal static bool IsNormalResult(string result) => result is
        "1-0 (White mates)" or "0-1 (Black mates)"
        or "1/2-1/2 (Draw by stalemate)"
        or "1/2-1/2 (Draw by insufficient mating material)"
        or "1/2-1/2 (Draw by fifty moves rule)"
        or "1/2-1/2 (Draw by 3-fold repetition)";

    internal void ObserveParsed(ChessGameRecord game)
    {
        if (!_playingIds.Add(game.PlayingId))
            throw new InvalidDataException($"duplicate playing identity in recording input: {game.PlayingId}");
        if (game.PositionIds.Length != game.MoveIds.Length + 1
            || game.MoveIds.Length != game.Moves.Count)
            throw new InvalidDataException("recording input does not contain a complete legal move trajectory");
        var result = PgnGames.TagStr(game.GameText, "Result");
        if (result != game.Result.ResultToken)
            throw new InvalidDataException("recording PGN header and parsed movetext result disagree");
        var key = (game.WhiteName, game.BlackName, result);
        if (!_matchVerified || !_matchGames.TryGetValue(key, out int remaining) || remaining < 1)
            throw new InvalidDataException("PGN players/result do not match a completed gauntlet game");
        _matchGames[key] = remaining - 1;
        ParsedGames++;
    }

    internal void ObserveResult(ChessPgnIngestor.Result result)
    {
        if (result.Parsed != ParsedGames)
            throw new InvalidDataException("recording and normal ingestor parse counts disagree");
        NovelGames = result.Novel;
        AppliedGames = result.Applied;
    }

    internal void ObserveCommit(ApplyResult result)
    {
        Writer.ApplyCalls++;
        Writer.EntitiesAttempted += result.EntitiesAttempted;
        Writer.EntitiesInserted += result.EntitiesInserted;
        Writer.PhysicalitiesAttempted += result.PhysicalitiesAttempted;
        Writer.PhysicalitiesInserted += result.PhysicalitiesInserted;
        Writer.AttestationsAttempted += result.AttestationsAttempted;
        Writer.AttestationsInserted += result.AttestationsInserted;
        Writer.EntitiesSkippedAtMerge += result.EntitiesSkippedAtMerge;
        Writer.PhysicalitiesSkippedAtMerge += result.PhysicalitiesSkippedAtMerge;
        Writer.RoundTrips += result.RoundTrips;
        Writer.JournalReplayHits += result.JournalReplayHit ? 1 : 0;
    }

    internal static bool IsGameWitness(AttestationRow row, IReadOnlySet<Hash128> playings) =>
        (playings.Contains(row.SubjectId) && row.ContextId is null
            && (row.TypeId == ChessVocabulary.PlaysLineType || row.TypeId == ChessVocabulary.HasEventType))
        || (row.ContextId is { } context && playings.Contains(context)
            && (row.TypeId == ChessVocabulary.HasWhiteType || row.TypeId == ChessVocabulary.HasBlackType
                || row.TypeId == ChessVocabulary.HasResultType || row.TypeId == ChessVocabulary.HasTerminationType
                || row.TypeId == WitnessRelations.Setup));

    private static class WitnessRelations
    {
        internal static readonly Hash128 Setup = RelationTypeRegistry.RelationTypeId("HAS_SETUP");
    }

    internal async Task VerifyChunkAsync(NpgsqlDataSource ds, IReadOnlyList<ChessGameRecord> games,
        IReadOnlyList<AttestationRow> expected, IReadOnlyList<PhysicalityRow> expectedCarriers, SubstrateChange experimentChange,
        ChessExperimentEvidence experiment, CancellationToken ct)
    {
        CommittedGames += games.Count;
        long started = Stopwatch.GetTimestamp();
        try
        {
            var playingIds = games.Select(g => g.PlayingId).ToHashSet();
            var witnesses = expected.Concat(experimentChange.Attestations).DistinctBy(a => a.Id).ToArray();
            var stored = await NpgsqlAttestationReads.WitnessesAsync(ds,
                witnesses.Select(a => a.SubjectId.ToBytes()).Distinct(ByteArrayComparer.Instance).ToArray(),
                witnesses.Select(a => a.TypeId.ToBytes()).Distinct(ByteArrayComparer.Instance).ToArray(),
                witnesses.Select(a => a.SourceId.ToBytes()).Distinct(ByteArrayComparer.Instance).ToArray(),
                witnesses.Where(a => a.ContextId is not null).Select(a => a.ContextId!.Value.ToBytes())
                    .Distinct(ByteArrayComparer.Instance).ToArray(),
                witnesses.Where(a => a.ContextId is null).Select(a => a.SubjectId.ToBytes())
                    .Distinct(ByteArrayComparer.Instance).ToArray(), ct);
            ValidateWitnesses(witnesses, stored);

            var carriers = expectedCarriers.DistinctBy(p => p.EntityId).ToArray();
            var carrierVertices = await NpgsqlSubstrateReads.CanonicalContentVerticesAsync(ds,
                carriers.Select(p => p.EntityId.ToBytes()).ToArray(), carriers.Select(p => p.Id.ToBytes()).ToArray(), ct);
            ValidateCarriers(games, carrierVertices);

            // The existing hydrator owns native trajectory decoding, line identity, start
            // board identity and legal full-line replay. This consumer compares the batch.
            var hydrated = await ChessWitnessHydrator.TryHydrateChunkAsync(ds, playingIds.ToArray(), ct);
            var whiteByPlaying = expected.Where(a => a.TypeId == ChessVocabulary.HasWhiteType)
                .ToDictionary(a => a.ContextId!.Value, a => a.ObjectId);
            var blackByPlaying = expected.Where(a => a.TypeId == ChessVocabulary.HasBlackType)
                .ToDictionary(a => a.ContextId!.Value, a => a.ObjectId);
            var expectedGames = games.Select(g => new ExpectedGame(g,
                whiteByPlaying.GetValueOrDefault(g.PlayingId), blackByPlaying.GetValueOrDefault(g.PlayingId))).ToArray();
            ValidateGames(expectedGames, hydrated);

            var receipt = experimentChange.Attestations.First();
            var resultRows = expected.Where(a => a.TypeId == ChessVocabulary.HasResultType).ToArray();
            var textIds = new[] { receipt.ObjectId!.Value, receipt.ContextId!.Value }
                .Concat(resultRows.Select(a => a.ObjectId!.Value)).ToArray();
            var text = await NpgsqlSubstrateReads.RenderTextBatchAsync(ds, textIds.Select(id => id.ToBytes()).ToArray(), ct);
            if (text is null || text.Length != textIds.Length || text[0] != experiment.ReceiptJson || text[1] != experiment.PgnEvent)
                throw new InvalidDataException("committed experiment receipt/context did not reconstruct exactly");
            var resultByPlaying = games.ToDictionary(g => g.PlayingId, g => g.Result.ResultToken);
            for (int i = 0; i < resultRows.Length; i++)
                if (text[i + 2] != resultByPlaying[resultRows[i].ContextId!.Value])
                    throw new InvalidDataException("committed result body did not reconstruct exactly");

            var hydratedByPlaying = hydrated.ToDictionary(g => g.PlayingId);
            foreach (var game in games)
            {
                var actual = hydratedByPlaying[game.PlayingId];
                Games.Add(new(Hex(game.PlayingId), Hex(actual.LineId), Hex(actual.StartPositionId!.Value),
                    actual.MoveIds.Select(Hex).ToArray(), actual.Result.ResultToken,
                    actual.WhitePlayer is { } white ? Hex(white) : null,
                    actual.BlackPlayer is { } black ? Hex(black) : null, PgnGames.TagStr(game.GameText, "Termination")));
            }
        }
        finally { ElapsedSeconds.Readback += Stopwatch.GetElapsedTime(started).TotalSeconds; }
    }

    internal static void ValidateWitnesses(IReadOnlyList<AttestationRow> expected,
        IReadOnlyList<NpgsqlAttestationReads.WitnessRow> actual)
    {
        if (actual.Count != expected.Count || actual.Select(a => ReadId(a.Id)).Distinct().Count() != actual.Count)
            throw new InvalidDataException("committed witness set has missing, duplicate or conflicting members");
        var byId = actual.ToDictionary(a => ReadId(a.Id));
        foreach (var e in expected)
        {
            if (!byId.TryGetValue(e.Id, out var a) || ReadId(a.SubjectId) != e.SubjectId
                || ReadId(a.TypeId) != e.TypeId || NullableId(a.ObjectId) != e.ObjectId
                || ReadId(a.SourceId) != e.SourceId || NullableId(a.ContextId) != e.ContextId
                || a.Outcome != (short)e.Outcome || a.ObservationCount < 1)
                throw new InvalidDataException($"committed witness identity/body mismatch: {e.Id}");
        }
    }

    internal static void ValidateGames(IReadOnlyList<ExpectedGame> expected,
        IReadOnlyList<ChessWitnessedGame> actual)
    {
        if (expected.Count != actual.Count || actual.Select(g => g.PlayingId).Distinct().Count() != actual.Count)
            throw new InvalidDataException("committed game readback has missing or duplicate playings");
        var byId = actual.ToDictionary(g => g.PlayingId);
        foreach (var selected in expected)
        {
            var e = selected.Record;
            if (!byId.TryGetValue(e.PlayingId, out var a) || a.LineId != e.LineId
                || a.Result != e.Result || !a.MoveIds.SequenceEqual(e.MoveIds)
                || a.StartPositionId != e.PositionIds[0]
                || a.WhitePlayer != selected.WhitePlayer || a.BlackPlayer != selected.BlackPlayer)
                throw new InvalidDataException($"committed game body mismatch: {e.PlayingId}");
            // Native hydration already checked the manifest's start board and canonical
            // line. Compare setup state as well (normal start has null on both sides).
            if (!string.Equals(a.StartFen, e.StartFen, StringComparison.Ordinal))
                throw new InvalidDataException($"committed game start position mismatch: {e.PlayingId}");
        }
    }

    internal static void ValidateCarriers(IReadOnlyList<ChessGameRecord> games,
        IReadOnlyList<NpgsqlSubstrateReads.ContentCarrierVertex> actual)
    {
        var lines = games.Where(g => g.MoveIds.Length > 0).DistinctBy(g => g.LineId).ToDictionary(g => g.LineId);
        var byLine = actual.GroupBy(v => ReadId(v.ParentId)).ToDictionary(g => g.Key, g => g.ToArray());
        if (byLine.Count != lines.Count)
            throw new InvalidDataException("canonical Content carrier inventory differs from the selected nonempty lines");
        foreach (var (line, game) in lines)
        {
            if (!byLine.TryGetValue(line, out var vertices) || vertices.Length != game.MoveIds.Length + 1)
                throw new InvalidDataException("canonical Content carrier has missing or extra constituents");
            for (int i = 0; i < vertices.Length; i++)
            {
                var vertex = vertices[i];
                var expectedId = i == 0 ? game.PositionIds[0] : game.MoveIds[i - 1];
                if (vertex.ConstituentCount != vertices.Length || vertex.Ordinal != i + 1
                    || vertex.RunLength != 1 || vertex.Flags != 0 || ReadId(vertex.ChildId) != expectedId)
                    throw new InvalidDataException("canonical Content carrier differs in count, ordinal, run length, flags or child identity");
            }
        }
    }

    internal void Complete(string status, string? error = null)
    {
        if (status == "completed" && (!_matchVerified || _matchGames.Values.Any(n => n != 0)
            || ParsedGames != RequestedGames || ReadbackGames != RequestedGames
            || CommittedGames != RequestedGames || Pgn is null || ExperimentReceiptSha256 is null
            || ExperimentArtifactSha256 is null))
            throw new InvalidDataException("recording cannot complete without every requested committed game and exact readback");
        _verified = status == "completed";
        Status = status;
        Error = error;
        FinishedAt = DateTimeOffset.UtcNow;
        ElapsedSeconds.Total = Stopwatch.GetElapsedTime(_started).TotalSeconds;
        ElapsedSeconds.Overhead = Math.Max(0, ElapsedSeconds.Total - ElapsedSeconds.Play
            - ElapsedSeconds.Recording - ElapsedSeconds.Commit - ElapsedSeconds.Readback);
    }

    internal async Task WriteAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path + ".pending", JsonSerializer.Serialize(this,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.Move(path + ".pending", path, overwrite: true);
    }

    private static Hash128? NullableId(byte[]? bytes) => bytes is null ? null : ReadId(bytes);
    private static Hash128 ReadId(byte[] bytes) => bytes.Length == 16 ? Hash128.FromBytes(bytes)
        : throw new InvalidDataException("committed witness identity must contain exactly 16 bytes");
    private static string Hex(Hash128 id) => Convert.ToHexStringLower(id.ToBytes());
    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        internal static readonly ByteArrayComparer Instance = new();
        public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] value) => Hash128.FromBytes(value).GetHashCode();
    }
}
