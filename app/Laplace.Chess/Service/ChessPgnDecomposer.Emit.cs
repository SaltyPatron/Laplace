using System.Linq;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Chess.Service;

public sealed partial class ChessPgnDecomposer
{
    internal static void RecordGame(ChessGameRecord parsed, SubstrateChangeBuilder b, Hash128? sourceId = null)
    {
        var initialWhiteToMove = parsed.InitialWhiteToMove;
        if (parsed.MoveIds.Length > 0 && !initialWhiteToMove.HasValue)
            throw new InvalidDataException("parsed move outcomes lack the validated initial mover");

        var (gameText, _, result, lineId, eventId, playingId) = parsed;
        var src = RecordingSourceId(parsed, sourceId);
        if (sourceId is null && src != ChessVocabulary.PgnSourceId)
            b.DeclareSourcePrior(src, TC.StructuredCorpus);

        var (whiteElo, blackElo) = ParseElos(gameText);
        var (whiteName, blackName) = parsed.WhiteName is { } wn
            ? (wn, parsed.BlackName!)
            : ParseNames(gameText);
        string date = parsed.Date ?? PgnGames.TagStr(gameText, "Date");
        var whitePlayer = EmitPlayer(b, whiteName, src);
        var blackPlayer = EmitPlayer(b, blackName, src);

        byte lineFloor = ChessCompose.LineFloor(parsed.MoveIds.Length);
        string eventTag = PgnGames.TagStr(gameText, "Event");
        string siteTag = PgnGames.TagStr(gameText, "Site");
        string roundTag = PgnGames.TagStr(gameText, "Round");
        EmitGame(
            b, lineId, eventId, playingId, gameText, date, result,
            whitePlayer, blackPlayer, whiteElo, blackElo, src,
            lineFloor,
            ChessVocabulary.PgnEventFloor(eventTag, siteTag, date),
            ChessVocabulary.PgnPlayingFloor(
                whiteName, blackName, date, eventTag, roundTag, siteTag, lineFloor, result.ResultToken));
        if (parsed.MoveIds.Length > 0 && initialWhiteToMove.HasValue)
            ChessMoveOutcomes.AppendGame(
                b, lineId, parsed.MoveIds, result, initialWhiteToMove.Value,
                src, PgnWitnessWeight);

        RecordRetainedTags(b, lineId, playingId, gameText, src);
        RecordStartPosition(b, lineId, playingId, parsed.StartFen, src, parsed.Rules);
        RecordPlayingTrajectory(b, parsed, src);
    }

    private static void RecordStartPosition(
        SubstrateChangeBuilder b, Hash128 lineId, Hash128 eventId, string? fen, Hash128 src, ChessVariantRules? rules)
    {
        if (string.IsNullOrWhiteSpace(fen)) return;
        Board board;
        try { board = Board.FromFen(fen, rules); }
        catch (FormatException) { return; }
        Hash128 positionId = ChessGraph.EmitPosition(b, board, src);
        b.AddAttestation(NativeAttestation.Categorical(
            lineId, "FEN", positionId, src, eventId, PgnWitnessWeight));
    }

    private static void RecordPlayingTrajectory(
        SubstrateChangeBuilder b, ChessGameRecord parsed, Hash128 src)
    {
        if (parsed.ResolvedMoves.Length == 0
            || parsed.ResolvedMoves.Length != parsed.MovingPieces.Length) return;
        var points = new ChessNode[parsed.ResolvedMoves.Length];
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        for (int i = 0; i < points.Length; i++)
            points[i] = ChessGraph.EmitMove(
                b, parsed.MovingPieces[i], parsed.ResolvedMoves[i], src, nowUs);

        var modality = new ChessModality();
        if (ChessAnalyze.InitialState(parsed.StartFen, modality, parsed.Rules) is not { } start)
            throw new InvalidOperationException("PGN line lost its admitted start position");
        var startPoint = ChessGraph.ComposePositionPoint(start.Initial.Board);
        if (parsed.PositionIds.Length == 0 || startPoint.Id != parsed.PositionIds[0])
            throw new InvalidOperationException("PGN start-position identity diverged from parsed line preimage");

        ChessGraph.AppendLineTrajectory(
            b, parsed.LineId, startPoint, points, src, nowUs);
        RecordAlignedAnnotations(b, parsed, points, src, nowUs);
    }

    private static void RecordAlignedAnnotations(
        SubstrateChangeBuilder b, ChessGameRecord parsed, IReadOnlyList<ChessNode> movePoints,
        Hash128 src, long nowUs)
    {
        if (parsed.Walk.Mainline.Count != movePoints.Count) return;
        var missing = ChessGraph.EmitAnnotationMissing(b, src, nowUs);
        var comments = new Hash128[movePoints.Count];
        var annotations = new Hash128[movePoints.Count];
        bool anyComment = false, anyAnnotation = false;
        for (int i = 0; i < movePoints.Count; i++)
        {
            var ply = parsed.Walk.Mainline[i];
            comments[i] = missing.Id;
            annotations[i] = missing.Id;
            if (!string.IsNullOrWhiteSpace(ply.CommentText)
                && ContentEmitter.Emit(b, ply.CommentText, src) is { } commentId)
            {
                comments[i] = commentId;
                anyComment = true;
            }

            string annotation = SerializeAnnotations(ply);
            if (annotation.Length > 0
                && ContentEmitter.Emit(b, annotation, src) is { } annotationId)
            {
                annotations[i] = annotationId;
                anyAnnotation = true;
            }
        }
        if (anyComment)
            ChessGraph.AppendPlayingAnnotationTrajectory(
                b, parsed.PlayingId, comments, movePoints,
                PhysicalityType.ChessComment, src, nowUs);
        if (anyAnnotation)
            ChessGraph.AppendPlayingAnnotationTrajectory(
                b, parsed.PlayingId, annotations, movePoints,
                PhysicalityType.ChessAnnotation, src, nowUs);
    }

    private static string SerializeAnnotations(PgnMovetext.PgnMoveStream ply)
    {
        Span<string?> parts =
        [ply.Nag is { } nag ? $"${nag}" : null, ply.StandaloneAnnotation, ply.SuffixAnnotation];
        return string.Join(' ', parts.ToArray().Where(static p => !string.IsNullOrWhiteSpace(p))!);
    }

    private static async IAsyncEnumerable<string> StreamGamesAsync(
        TextReader reader, [EnumeratorCancellation] CancellationToken ct)
    {
        var sb = new StringBuilder(2048);
        var carry = new StringBuilder(256);
        bool inGame = false;
        var buf = new char[Math.Max(1,
            IngestSizing.ResolveSequentialIoBufferBytes() / sizeof(char))];
        int read;
        while ((read = await reader.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            int lineStart = 0;
            for (int i = 0; i < read; i++)
            {
                if (buf[i] != '\n') continue;
                int end = i > lineStart && buf[i - 1] == '\r' ? i - 1 : i;
                var tail = buf.AsSpan(lineStart, end - lineStart);
                if (carry.Length > 0)
                {
                    carry.Append(tail);
                    if (carry[^1] == '\r') carry.Length--;
                    ProcessLine(carry.ToString().AsSpan(), sb, ref inGame, out var completed);
                    carry.Clear();
                    if (completed is not null) yield return completed;
                }
                else
                {
                    ProcessLine(tail, sb, ref inGame, out var completed);
                    if (completed is not null) yield return completed;
                }
                lineStart = i + 1;
            }
            if (lineStart < read) carry.Append(buf.AsSpan(lineStart, read - lineStart));
        }
        if (carry.Length > 0)
        {
            var last = carry.ToString().TrimEnd('\r');
            ProcessLine(last.AsSpan(), sb, ref inGame, out var completedLast);
            if (completedLast is not null) yield return completedLast;
        }
        if (sb.Length > 0) yield return sb.ToString();

        static void ProcessLine(ReadOnlySpan<char> line, StringBuilder sb, ref bool inGame, out string? completed)
        {
            completed = null;
            if (line.StartsWith("[Event ", StringComparison.Ordinal))
            {
                if (inGame && sb.Length > 0) { completed = sb.ToString(); sb.Clear(); }
                inGame = true;
            }
            if (inGame) { sb.Append(line); sb.Append('\n'); }
        }
    }

    private const double PgnWitnessWeight = 0.7;

    private static void EmitGame(
        SubstrateChangeBuilder b, Hash128 lineId, Hash128 eventId, Hash128 playingId,
        string gameText, string date,
        GameOutcome result, Hash128? whitePlayer, Hash128? blackPlayer, int whiteElo, int blackElo,
        Hash128 src, byte lineFloor, byte eventFloor, byte playingFloor)
    {
        b.AddEntity(lineId, lineFloor, ChessVocabulary.GameType);
        b.AddEntity(eventId, eventFloor, ChessVocabulary.EventType);
        b.AddEntity(playingId, playingFloor, ChessVocabulary.PlayingType);

        b.AddAttestation(NativeAttestation.CategoricalResolved(
            playingId, ChessVocabulary.PlaysLineType, lineId, src, null, PgnWitnessWeight));
        b.AddAttestation(NativeAttestation.CategoricalResolved(
            playingId, ChessVocabulary.HasEventType, eventId, src, null, PgnWitnessWeight));

        // The tag name is the predicate. The object is the tag text, or the player it names.
        if (whitePlayer is { } wp) b.AddAttestation(NativeAttestation.Categorical(lineId, "White", wp, src, playingId, PgnWitnessWeight));
        if (blackPlayer is { } bp) b.AddAttestation(NativeAttestation.Categorical(lineId, "Black", bp, src, playingId, PgnWitnessWeight));

        if (whitePlayer is { } w2)
            ChessGraph.AppendPlayerResult(
                b, w2, blackPlayer, result.ForMover(0), PgnWitnessWeight, src, playingId);
        if (blackPlayer is { } b2)
            ChessGraph.AppendPlayerResult(
                b, b2, whitePlayer, result.ForMover(1), PgnWitnessWeight, src, playingId);

        Meta(b, lineId, "Event", PgnGames.TagStr(gameText, "Event"), src, playingId);
        Meta(b, lineId, "Site", PgnGames.TagStr(gameText, "Site"), src, playingId);
        Meta(b, lineId, "Date", date, src, playingId);
        Meta(b, lineId, "Round", PgnGames.TagStr(gameText, "Round"), src, playingId);
        Meta(b, lineId, "ECO", PgnGames.TagStr(gameText, "ECO"), src, playingId);
        Meta(b, lineId, "Opening", PgnGames.TagStr(gameText, "Opening"), src, playingId);
        Meta(b, lineId, "Termination", PgnGames.TagStr(gameText, "Termination"), src, playingId);
        Meta(b, lineId, "Result", result.ResultToken, src, playingId);
        Meta(b, lineId, "TimeControl", PgnGames.TagStr(gameText, "TimeControl"), src, playingId);

        if (whitePlayer is { } wp2 && whiteElo > 0) Rating(b, wp2, whiteElo, "WhiteElo", playingId, src);
        if (blackPlayer is { } bp2 && blackElo > 0) Rating(b, bp2, blackElo, "BlackElo", playingId, src);
    }

    private static void RecordRetainedTags(
        SubstrateChangeBuilder b, Hash128 lineId, Hash128 playingId, string gameText, Hash128 src)
    {
        // Site and Round stay with the header pass on this tree. UTC time, titles, and
        // FIDE ids are kept here so a game does not drop them.
        Keep("UTCDate", "UTCDate");
        Keep("UTCTime", "UTCTime");
        Keep("WhiteTitle", "WhiteTitle");
        Keep("BlackTitle", "BlackTitle");
        Keep("WhiteFideId", "WhiteFideId");
        Keep("BlackFideId", "BlackFideId");

        void Keep(string relation, string tag)
        {
            string value = PgnGames.TagStr(gameText, tag);
            if (value.Length == 0 || value.All(static ch => ch is '?' or ':' or '.')) return;
            Meta(b, lineId, relation, value, src, playingId);
        }
    }

    private static void Meta(
        SubstrateChangeBuilder b, Hash128 line, string rel, string value, Hash128 src, Hash128 eventId)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "?" || value == "-" || value == "????.??.??") return;
        if (ContentEmitter.Emit(b, value, src) is { } vid)
            b.AddAttestation(NativeAttestation.Categorical(line, rel, vid, src, eventId, PgnWitnessWeight));
    }

    private static void Rating(SubstrateChangeBuilder b, Hash128 player, int elo, string tag, Hash128 eventId, Hash128 src)
    {
        if (ContentEmitter.Emit(b, elo.ToString(), src) is { } rid)
            b.AddAttestation(NativeAttestation.Categorical(player, tag, rid, src, eventId, PgnWitnessWeight));
    }

    internal static string TcClass(string tc)
    {
        if (string.IsNullOrWhiteSpace(tc) || tc == "-") return "";
        if (tc.Contains('/')) return "classical";
        int plus = tc.IndexOf('+');
        string baseStr = plus >= 0 ? tc[..plus] : tc;
        if (!int.TryParse(baseStr, out int baseSec)) return "";
        return baseSec < 180 ? "bullet" : baseSec < 600 ? "blitz" : baseSec < 1500 ? "rapid" : "classical";
    }

    private static (int White, int Black) ParseElos(string game)
        => (PgnGames.TagInt(game, "WhiteElo"), PgnGames.TagInt(game, "BlackElo"));

    private static (string White, string Black) ParseNames(string game)
        => (PgnGames.TagStr(game, "White"), PgnGames.TagStr(game, "Black"));

    private static Hash128? EmitPlayer(SubstrateChangeBuilder b, string name, Hash128 src)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "?") return null;
        var canonicalId = ChessVocabulary.PlayerId(name);
        ChessVocabulary.EmitPlayer(b, canonicalId, name, src);
        return canonicalId;
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        long games = 0;
        foreach (var f in EnumerateFiles(context.EcosystemPath, _scope))
        {
            try
            {
                games += ChessInput.IsCompressed(f)
                    ? EstimateCompressedGameCount(f)
                    : CountEventHeaderLines(f, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"ChessPgnDecomposer: failed to estimate games in {f}: {ex.Message}");
            }
        }
        return Task.FromResult<long?>(games == 0 ? null : games);
    }

    private static long CountEventHeaderLines(string path, CancellationToken ct)
    {
        ReadOnlySpan<byte> prefix = "[Event "u8;
        long games = 0;
        using var fs = IngestIo.OpenSequentialRead(path);
        var buf = new byte[IngestSizing.ResolveSequentialIoBufferBytes()];
        int matched = 0;
        bool first = true;
        int read;
        while ((read = fs.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            int i = 0;
            if (first)
            {
                first = false;
                if (read >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) i = 3;
            }
            for (; i < read; i++)
            {
                byte c = buf[i];
                if (c == (byte)'\n' || c == (byte)'\r') { matched = 0; continue; }
                if (matched < 0) continue;
                if (c == prefix[matched])
                {
                    if (++matched == prefix.Length) { games++; matched = -1; }
                }
                else matched = -1;
            }
        }
        return games;
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var paths = EnumerateFiles(context.EcosystemPath, _scope);
        if (options.MaxInputUnits > 0)
            return Task.FromResult(IngestInventory.FromFiles("games", paths.ToList(), options.MaxInputUnits, ct));

        var files = new List<IngestFileSpec>(paths.Count);
        long total = 0;
        foreach (var p in paths)
        {
            long n = ChessInput.IsCompressed(p)
                ? EstimateCompressedGameCount(p)
                : EtlInventory.EstimatePgnGameCount(p, ct);
            files.Add(new IngestFileSpec(Path.GetFileName(p), p, n));
            total += n;
        }
        return Task.FromResult<IngestInventory?>(new IngestInventory("games", total, files));
    }

    private const long MeanCompressedGameBytes = 1_600;

    private static long EstimateCompressedGameCount(string path)
        => Math.Max(1, ChessInput.UncompressedLength(path) / MeanCompressedGameBytes);

    public (string Status, string Detail)? ExplainEmptyRun(long declaredInputUnits)
        => ChessDropLedger.ExplainEmptyRun(SourceName, declaredInputUnits);

    private static IReadOnlyList<string> EnumerateFiles(string path, SearchOption scope)
        => ChessInput.Resolve(path, scope, ChessInput.PgnExtensions, "chess");
}

internal readonly record struct ChessPlayingPeek(ChessGameRecord Game, Hash128 PlayingId);

internal sealed record ChessLineReplay(
    Hash128[] PositionIds,
    ChessMove[] Moves,
    Piece[] MovingPieces,
    Hash128[] MoveIds,
    bool InitialWhiteToMove)
{
    internal Board[]? Boards { get; init; }
}

internal sealed record ChessParsedReplay(
    Board[] Boards,
    ChessComposed[] Positions,
    ChessMove[] Moves,
    bool StandardStart)
{
    internal static readonly ChessParsedReplay Empty = new([], [], [], false);
    internal bool IsCompleteFor(ChessGameRecord game) =>
        Boards.Length == game.ResolvedMoves.Length + 1
        && Positions.Length == Boards.Length
        && Moves.Length == game.ResolvedMoves.Length;
}

public sealed record ChessGameRecord(
    string GameText,
    List<string> Moves,
    GameOutcome Result,
    Hash128 LineId,
    Hash128 EventId,
    Hash128 PlayingId)
{
    internal PgnMovetext.PgnWalkResult Walk { get; init; } = null!;

    /// <summary>Corpus name from the file path, when the extract pass recognized one.</summary>
    internal string? Corpus { get; init; }
    internal string? WhiteName { get; init; }
    internal string? BlackName { get; init; }
    internal string? Date { get; init; }
    internal string? StartFen { get; init; }
    internal ChessVariantRules? Rules { get; init; }
    // Admission evidence from the native legal replay, never an identity salt.
    internal bool? InitialWhiteToMove { get; init; }

    internal Hash128[] PositionIds { get; init; } = [];
    internal ChessMove[] ResolvedMoves { get; init; } = [];
    internal Piece[] MovingPieces { get; init; } = [];
    internal Hash128[] MoveIds { get; init; } = [];
    internal Board[]? ReplayBoards { get; init; }
    internal bool NormalCompletionVerified { get; init; }
    internal bool CompleteSourceVerified { get; init; }

    // Recording acceptance is attestation-based. Do not implement ITrunkRootRecord:
    // generic entity existence admission must not discard a partially recorded game.
}
