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

public sealed class ChessPgnDecomposer(bool recursive = false, bool analyzeInline = true)
    : ComposeDecomposerMultiFile<ChessGameRecord>, IIngestInventoryProvider, IIngestNoOpExplainer
{
    private readonly SearchOption _scope =
        recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
    private readonly bool _analyzeInline = analyzeInline;

    public override Hash128 SourceId => ChessVocabulary.PgnSourceId;
    public override string SourceName => "ChessPgn";
    public override int LayerOrder => 20;
    public override Hash128 TrustClassId => ChessVocabulary.PgnTrustClass;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "chess/pgn";

    public override int EstimatedBytesPerRecord => IngestSourceProfile.ChessPgn.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => IngestSourceProfile.ChessPgn.EstComposeUnitsPerRecord;
    public override IngestSourceProfile SizingProfile => IngestSourceProfile.ChessPgn;

    private IReadOnlyCollection<string> _canonicalNames = Array.Empty<string>();
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        _canonicalNames = await ChessVocabulary.BootstrapManyAsync(context.Writer,
        [
            new(ChessVocabulary.PgnSourceId, SourceName, ChessVocabulary.PgnTrustClass),
            new(ChessVocabulary.AnalysisSourceId, "ChessAnalysis", ChessVocabulary.AnalysisTrustClass),
            new(ChessTransitions.SourceId, "ChessTransitions", ChessTransitions.TrustClassId),
            new(ChessPositionOutcomes.SourceId, ChessPositionOutcomes.SourceName,
                ChessPositionOutcomes.TrustClassId),
            new(ChessSyzygy.SourceId, ChessSyzygy.SourceName, ChessSyzygy.TrustClassId),
        ], ct, context.Reader);
        ChessDropLedger.Reset();
    }

    public override ValueTask DisposeAsync()
    {
        ChessDropLedger.Report(SourceName);
        return base.DisposeAsync();
    }

    protected override IReadOnlyList<(string Path, string Label)> ListFiles(
        string ecosystemPath, DecomposerOptions options)
    {
        bool rootIsFile = File.Exists(ecosystemPath);
        return EnumerateFiles(ecosystemPath, _scope)
            .Select(p =>
            {
                string rel = rootIsFile
                    ? Path.GetFileName(p)
                    : Path.GetRelativePath(ecosystemPath, p).Replace('\\', '/');
                return (p, $"{BatchLabelPrefix}/{rel}");
            })
            .ToArray();
    }

    protected override async IAsyncEnumerable<ChessGameRecord> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var ws = IngestPipelineDefaults.ResolveWorkingSet(PipelineProfile, options);
        if (options.ReObservePresent)
        {
            await foreach (var g in ExtractFileParseDirectAsync(filePath, ws.Batch, ct))
                yield return g;
            yield break;
        }
        await foreach (var g in ExtractFileSerialPeekAsync(
                           filePath, ws.Batch, reObservePresent: false, ct))
            yield return g;
    }

    private async IAsyncEnumerable<ChessGameRecord> ExtractFileParseDirectAsync(
        string filePath, int batch,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var chunk = new List<ChessGameRecord>(batch);
        await foreach (var gameText in StreamFileGamesAsync(filePath, ct))
        {
            if (TryParseGame(gameText) is { } parsed) chunk.Add(parsed);
            if (chunk.Count < batch) continue;
            foreach (var g in chunk) yield return g;
            chunk.Clear();
        }
        foreach (var g in chunk) yield return g;
    }

    private async IAsyncEnumerable<ChessGameRecord> ExtractFileSerialPeekAsync(
        string filePath, int batch, bool reObservePresent,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var peeks = new List<ChessPlayingPeek>(batch);
        Task<List<ChessGameRecord>>? pending = null;
        await foreach (var gameText in StreamFileGamesAsync(filePath, ct))
        {
            if (TryPeekPlaying(gameText) is { } peek) peeks.Add(peek);
            if (peeks.Count < batch) continue;
            var handoff = peeks;
            peeks = new List<ChessPlayingPeek>(batch);
            var next = MaterializeNovelAsync(handoff, ContainmentReader, reObservePresent, ct);
            if (pending is not null)
            {
                foreach (var g in await pending.ConfigureAwait(false))
                    yield return g;
            }
            pending = next;
        }
        if (pending is not null)
        {
            foreach (var g in await pending.ConfigureAwait(false))
                yield return g;
        }
        if (peeks.Count > 0)
        {
            foreach (var g in await MaterializeNovelAsync(
                         peeks, ContainmentReader, reObservePresent, ct).ConfigureAwait(false))
                yield return g;
        }
    }

    private static async Task<List<ChessGameRecord>> MaterializeNovelAsync(
        List<ChessPlayingPeek> peeks, ISubstrateReader? reader, bool reObservePresent,
        CancellationToken ct)
    {
        var list = new List<ChessGameRecord>();
        await foreach (var g in YieldNovelParsedAsync(peeks, reader, reObservePresent, ct)
                           .ConfigureAwait(false))
            list.Add(g);
        await ProbeLinePositionsAsync(list, reader, ct).ConfigureAwait(false);
        return list;
    }

    internal static async Task ProbeLinePositionsAsync(
        List<ChessGameRecord> games, ISubstrateReader? reader, CancellationToken ct)
    {
        if (reader is null || games.Count == 0) return;
        var unknown = new HashSet<Hash128>();
        for (int g = 0; g < games.Count; g++)
        {
            var positions = games[g].PositionIds;
            for (int i = 0; i < positions.Length; i++)
            {
                var id = positions[i];
                if (!reader.IsProvenPresent(id)) unknown.Add(id);
            }
        }
        if (unknown.Count == 0) return;
        int chunk = IngestSizing.ResolveApplyIo(
            IngestTopology.Current.ApplyPartitions).ProbeChunkIds;
        var ids = new Hash128[unknown.Count];
        unknown.CopyTo(ids);
        for (int i = 0; i < ids.Length; i += chunk)
        {
            int n = Math.Min(chunk, ids.Length - i);
            var slice = new Hash128[n];
            Array.Copy(ids, i, slice, 0, n);
            byte[] bm = await reader.TierBatchExistenceProbeAsync(
                slice, ChessCompose.PositionTier, ct).ConfigureAwait(false);
            var proven = new List<Hash128>(n);
            for (int j = 0; j < n; j++)
                if (BitmapBits.IsSet(bm, j)) proven.Add(slice[j]);
            if (proven.Count > 0) reader.MarkProven(proven);
        }
    }

    private static async IAsyncEnumerable<ChessGameRecord> YieldNovelParsedAsync(
        List<ChessPlayingPeek> peeks, ISubstrateReader? reader, bool reObservePresent,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (peeks.Count == 0) yield break;
        if (reObservePresent || reader is null)
        {
            foreach (var p in peeks) yield return p.Game;
            yield break;
        }

        var toProbe = new List<int>(peeks.Count);
        for (int i = 0; i < peeks.Count; i++)
        {
            if (reader.IsProvenPresent(peeks[i].PlayingId)) continue;
            toProbe.Add(i);
        }

        var present = new bool[peeks.Count];
        if (toProbe.Count > 0)
        {
            var ids = new Hash128[toProbe.Count];
            for (int k = 0; k < toProbe.Count; k++) ids[k] = peeks[toProbe[k]].PlayingId;
            byte[] bm = await reader.TierBatchExistenceProbeAsync(
                ids, (short)EntityTier.Document, ct).ConfigureAwait(false);
            var proven = new List<Hash128>(toProbe.Count);
            for (int k = 0; k < toProbe.Count; k++)
            {
                if (!BitmapBits.IsSet(bm, k)) continue;
                present[toProbe[k]] = true;
                proven.Add(ids[k]);
            }
            if (proven.Count > 0) reader.MarkProven(proven);
        }

        for (int i = 0; i < peeks.Count; i++)
        {
            if (present[i] || reader.IsProvenPresent(peeks[i].PlayingId)) continue;
            yield return peeks[i].Game;
        }
    }

    internal static ChessPlayingPeek? TryPeekPlaying(string gameText)
    {
        var game = TryParseGame(gameText);
        return game is null ? null : new ChessPlayingPeek(game, game.PlayingId);
    }

    protected override void Compose(ChessGameRecord record, SubstrateChangeBuilder b)
        => ComposeGame(record, b, _analyzeInline);

    internal static void ComposeGame(ChessGameRecord record, SubstrateChangeBuilder b, bool analyzeInline)
    {
        b.DeclareSourcePrior(ChessVocabulary.PgnSourceId, TC.StructuredCorpus);
        RecordGame(record, b);
        if (analyzeInline)
        {
            b.DeclareSourcePrior(ChessTransitions.SourceId, TC.StructuredCorpus)
                .DeclareSourcePrior(ChessPositionOutcomes.SourceId, TC.StructuredCorpus);
            var replay = MaterializeParsedReplay(record);
            ChessAnalyze.DeriveFromParsed(b, record, replay);
            ChessTransitions.DepositFromParsed(b, record);
            ChessPositionOutcomes.DepositFromParsed(b, record, replay);
            if (ChessTablebaseRuntime.Prober is { } prober)
                ChessSyzygy.DeriveGame(b, ChessAnalyze.WitnessedFromParsed(record), prober);
        }
    }

    internal static ChessParsedReplay MaterializeParsedReplay(ChessGameRecord game)
    {
        var modality = new ChessModality();
        if (ChessAnalyze.InitialState(game.StartFen, modality) is not { } start)
            return ChessParsedReplay.Empty;

        int moveCount = game.ResolvedMoves.Length;
        var boards = new Board[moveCount + 1];
        var positions = new ChessComposed[moveCount + 1];
        var board = start.Initial.Board;
        boards[0] = board;
        positions[0] = ChessCompose.Position(board);
        for (int ply = 0; ply < moveCount; ply++)
        {
            board = board.Clone();
            MoveApply.Make(board, game.ResolvedMoves[ply]);
            boards[ply + 1] = board;
            positions[ply + 1] = ChessCompose.Position(board);
        }
        return new ChessParsedReplay(boards, positions, game.ResolvedMoves, start.StandardStart);
    }

    private static async IAsyncEnumerable<ChessGameRecord> StreamNovelGamesAsync(
        string ecosystemPath, SearchOption scope, ISubstrateReader? reader, int chunkSize,
        bool reObservePresent, [EnumeratorCancellation] CancellationToken ct)
    {
        var chunk = new List<ChessGameRecord>(chunkSize);
        await foreach (var gameText in StreamAllGamesAsync(ecosystemPath, scope, ct))
        {
            if (TryParseGame(gameText) is { } parsed) chunk.Add(parsed);
            if (chunk.Count < chunkSize) continue;
            await foreach (var g in YieldChunkAsync(chunk, reader, reObservePresent, ct)) yield return g;
            chunk.Clear();
        }
        await foreach (var g in YieldChunkAsync(chunk, reader, reObservePresent, ct)) yield return g;
    }

    internal static async IAsyncEnumerable<ChessGameRecord> YieldChunkAsync(
        List<ChessGameRecord> chunk, ISubstrateReader? reader, bool reObservePresent,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (reObservePresent || reader is null)
        {
            foreach (var g in chunk) yield return g;
            yield break;
        }
        await foreach (var novel in FilterNovelAsync(chunk, reader, ct)) yield return novel;
    }

    internal static async IAsyncEnumerable<ChessGameRecord> FilterNovelAsync(
        List<ChessGameRecord> chunk, ISubstrateReader? reader, [EnumeratorCancellation] CancellationToken ct)
    {
        if (chunk.Count == 0) yield break;
        if (reader is null) { foreach (var g in chunk) yield return g; yield break; }

        var toProbe = new List<int>(chunk.Count);
        for (int i = 0; i < chunk.Count; i++)
        {
            if (reader.IsProvenPresent(chunk[i].PlayingId)) continue;
            toProbe.Add(i);
        }

        bool[] present = new bool[chunk.Count];
        if (toProbe.Count > 0)
        {
            var ids = new Hash128[toProbe.Count];
            for (int k = 0; k < toProbe.Count; k++) ids[k] = chunk[toProbe[k]].PlayingId;
            byte[] bm = await reader.TierBatchExistenceProbeAsync(
                ids, (short)EntityTier.Document, ct).ConfigureAwait(false);
            var proven = new List<Hash128>(toProbe.Count);
            for (int k = 0; k < toProbe.Count; k++)
            {
                if (!BitmapBits.IsSet(bm, k)) continue;
                present[toProbe[k]] = true;
                proven.Add(ids[k]);
            }
            if (proven.Count > 0) reader.MarkProven(proven);
        }

        for (int i = 0; i < chunk.Count; i++)
            if (!present[i] && !reader.IsProvenPresent(chunk[i].PlayingId))
                yield return chunk[i];
    }

    internal static async IAsyncEnumerable<string> StreamFileGamesAsync(
        string file, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var (_, reader) in ChessInput.OpenMembers(file))
        {
            ct.ThrowIfCancellationRequested();
            await foreach (var gameText in StreamGamesAsync(reader, ct).WithCancellation(ct))
                yield return gameText;
        }
    }

    internal static async IAsyncEnumerable<string> StreamAllGamesAsync(
        string ecosystemPath, SearchOption scope, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var file in EnumerateFiles(ecosystemPath, scope))
        {
            await foreach (var gameText in StreamFileGamesAsync(file, ct).WithCancellation(ct))
                yield return gameText;
        }
    }

    internal static ChessGameRecord? TryParseGame(string gameText, bool requireNormalCompletion = false)
    {
        var gameBytes = Encoding.UTF8.GetBytes(gameText);
        PgnMovetext.PgnWalkResult walk;
        using (var ast = GrammarDecomposer.Parse(gameBytes, "pgn"))
        {
            if (requireNormalCompletion && !ast.Diagnostics.SyntaxComplete)
                throw new InvalidDataException("normal recorded PGN requires a complete native syntax parse");
            if (requireNormalCompletion)
            {
                int games = 0;
                for (int i = 0; i < ast.NodeCount; i++)
                    if (ast.NodeTypeIs(ast.GetNode(i).NodeTypeId, "game"u8)) games++;
                if (games != 1)
                    throw new InvalidDataException("normal recorded PGN requires exactly one native game per recording input");
            }
            walk = PgnMovetext.Walk(ast, gameBytes);
        }
        if (walk.Result is null)
        {
            if (requireNormalCompletion)
                throw new InvalidDataException("normal recorded PGN requires a finished serialized result");
            ChessDropLedger.Drop(ChessDropLedger.NoResultOrMoves, Headline(gameText));
            return null;
        }

        var moves = walk.Mainline.Select(p => p.San).ToList();
        var result = walk.Result.Value;

        var (whiteName, blackName) = ParseNames(gameText);
        string date = PgnGames.TagStr(gameText, "Date");

        string? startFen = PgnGames.TagStr(gameText, "SetUp") == "1"
            ? PgnGames.TagStr(gameText, "FEN") : null;
        if (requireNormalCompletion && startFen is not null && string.IsNullOrWhiteSpace(startFen))
            throw new InvalidDataException("normal recorded PGN declares SetUp=1 without its FEN");
        if (requireNormalCompletion && startFen is null && !string.IsNullOrEmpty(PgnGames.TagStr(gameText, "FEN")))
            throw new InvalidDataException("normal recorded PGN has a FEN without SetUp=1");
        var replay = TryReplayLineDetailed(moves, startFen,
            expectedNormalOutcome: requireNormalCompletion ? result : null);
        if (replay is null)
        {
            if (requireNormalCompletion)
                throw new InvalidDataException("normal recorded PGN does not contain a legal complete move trajectory");
            ChessDropLedger.Drop(
                DropReason(gameText, startFen),
                $"{whiteName} vs {blackName} {date}"
                + (startFen is null ? "" : $" [FEN {startFen}]"));
            return null;
        }
        var lineId = ChessCompose.LineId(replay.PositionIds[0], replay.MoveIds);
        ChessDropLedger.Kept();
        string eventTag = PgnGames.TagStr(gameText, "Event");
        string siteTag = PgnGames.TagStr(gameText, "Site");
        string roundTag = PgnGames.TagStr(gameText, "Round");
        var eventId = ChessVocabulary.PgnEventId(eventTag, siteTag, date);
        var playingId = ChessVocabulary.PgnPlayingId(
            whiteName, blackName, date, eventTag, roundTag, siteTag, lineId, result.ResultToken);

        return new ChessGameRecord(gameText, moves, result, lineId, eventId, playingId)
        {
            Walk = walk,
            WhiteName = whiteName,
            BlackName = blackName,
            Date = date,
            StartFen = startFen,
            PositionIds = replay.PositionIds,
            ResolvedMoves = replay.Moves,
            MovingPieces = replay.MovingPieces,
            MoveIds = replay.MoveIds,
            NormalCompletionVerified = requireNormalCompletion,
        };
    }

    private static string DropReason(string gameText, string? startFen)
    {
        if (startFen is null) return ChessDropLedger.UnreadableSan;
        string variant = PgnGames.TagStr(gameText, "Variant");
        return string.IsNullOrWhiteSpace(variant) || variant == "?"
            ? ChessDropLedger.UnreadableStartPosition
            : ChessDropLedger.UnmodelledVariant;
    }

    private static string Headline(string gameText)
    {
        int nl = gameText.IndexOf('\n');
        var head = nl < 0 ? gameText : gameText[..nl];
        head = head.Trim();
        return head.Length <= 120 ? head : head[..120];
    }

    internal static Hash128[]? TryReplayLine(IReadOnlyList<string> sans, string? startFen)
        => TryReplayLineDetailed(sans, startFen)?.PositionIds;

    internal static ChessLineReplay? TryReplayLineDetailed(
        IReadOnlyList<string> sans, string? startFen, GameOutcome? expectedNormalOutcome = null)
    {
        var m = new ChessModality();
        if (ChessAnalyze.InitialState(startFen, m) is not { } start) return null;
        var board = start.Initial.Board.Clone();
        // The ordinary move owner supports kingless puzzle positions. They must
        // never certify a normal completed game, nor may the nonmoving king
        // already be in check when the supplied game starts.
        if (expectedNormalOutcome is not null
            && (System.Numerics.BitOperations.PopCount(board.PieceBB(Piece.WKing)) != 1
                || System.Numerics.BitOperations.PopCount(board.PieceBB(Piece.BKing)) != 1
                || MoveGen.InCheck(board, !board.WhiteToMove)))
            throw new InvalidDataException("normal recorded game has an invalid king configuration");
        var ids = new Hash128[sans.Count + 1];
        var moves = new ChessMove[sans.Count];
        var movingPieces = new Piece[sans.Count];
        var moveIds = new Hash128[sans.Count];
        ids[0] = ChessCompose.PositionId(board);
        // This optional proof stays in the existing legal SAN walk. Ordinary PGN
        // testimony may end by resignation/agreement or continue after a claimable
        // draw; measured normal CuteChess games must reach their declared outcome.
        var history = expectedNormalOutcome is null ? null : ImmutableList.Create(ids[0]);
        if (history is not null && sans.Count == 0)
            throw new InvalidDataException("normal recorded game must contain a legal move from a nonterminal start");
        var scratch = new List<ChessMove>(16);
        for (int ply = 0; ply < sans.Count; ply++)
        {
            if (history is not null && m.Terminal(new ChessState(board, history)) is not null)
                throw new InvalidDataException("normal recorded game contains a move after its terminal position");
            var mv = San.Resolve(board, sans[ply], scratch);
            if (mv is null) return null;
            Piece moving = board.Squares[mv.Value.From];
            moves[ply] = mv.Value;
            movingPieces[ply] = moving;
            var moveId = ChessCompose.MoveId(moving, mv.Value);
            moveIds[ply] = moveId;
            var tKey = ChessCompose.TransitionKey(ids[ply], moveId);
            MoveApply.Make(board, mv.Value);
            if (ChessTransitionFloor.TryLookup(tKey, out var toId))
            {
                ids[ply + 1] = toId;
            }
            else
            {
                toId = ChessCompose.PositionId(board);
                ids[ply + 1] = toId;
                ChessTransitionFloor.Remember(tKey, toId);
            }
            if (history is not null) history = history.Add(ids[ply + 1]);
        }
        if (history is not null && m.Terminal(new ChessState(board, history)) != expectedNormalOutcome)
            throw new InvalidDataException("normal recorded game's serialized terminal outcome differs from its result");
        return new ChessLineReplay(ids, moves, movingPieces, moveIds);
    }

    internal static void RecordGame(ChessGameRecord parsed, SubstrateChangeBuilder b, Hash128? sourceId = null)
    {
        var (gameText, _, result, lineId, eventId, playingId) = parsed;
        var src = sourceId ?? ChessVocabulary.PgnSourceId;

        var (whiteElo, blackElo) = ParseElos(gameText);
        var (whiteName, blackName) = parsed.WhiteName is { } wn
            ? (wn, parsed.BlackName!)
            : ParseNames(gameText);
        string date = parsed.Date ?? PgnGames.TagStr(gameText, "Date");
        var whitePlayer = EmitPlayer(b, whiteName, src);
        var blackPlayer = EmitPlayer(b, blackName, src);

        EmitGame(b, lineId, eventId, playingId, gameText, date, result, whitePlayer, blackPlayer, whiteElo, blackElo, src);
        if (parsed.MoveIds.Length > 0)
            ChessMoveOutcomes.AppendGame(
                b, lineId, parsed.MoveIds, result, src, PgnWitnessWeight);

        RecordStartPosition(b, lineId, playingId, gameText, src);
        RecordOpeningHeaders(b, lineId, gameText, src);
        RecordPlayingTrajectory(b, parsed, src);
    }

    private static void RecordStartPosition(
        SubstrateChangeBuilder b, Hash128 lineId, Hash128 eventId, string gameText, Hash128 src)
    {
        if (PgnGames.TagStr(gameText, "SetUp") != "1") return;
        string fen = PgnGames.TagStr(gameText, "FEN");
        if (string.IsNullOrWhiteSpace(fen)) return;
        Board board;
        try { board = Board.FromFen(fen); }
        catch (FormatException) { return; }
        Hash128 positionId = ChessGraph.EmitPosition(b, board, src);
        b.AddAttestation(NativeAttestation.Categorical(
            lineId, "HAS_SETUP", positionId, src, eventId, PgnWitnessWeight));
    }

    private static void RecordOpeningHeaders(SubstrateChangeBuilder b, Hash128 lineId, string gameText, Hash128 src)
    {
        string eco = ChessCanonical.Eco(PgnGames.TagStr(gameText, "ECO")) ?? "";
        if (eco.Length > 0) ChessGraph.AppendGameMeta(b, lineId, "GAME_HAS_ECO", eco, PgnWitnessWeight, src);
        string opening = ChessCanonical.OpeningName(PgnGames.TagStr(gameText, "Opening")) ?? "";
        if (opening.Length > 0) ChessGraph.AppendGameMeta(b, lineId, "GAME_HAS_OPENING", opening, PgnWitnessWeight, src);
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
        if (ChessAnalyze.InitialState(parsed.StartFen, modality) is not { } start)
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
    private const string HasEventRelation = "HAS_EVENT";

    private static void EmitGame(
        SubstrateChangeBuilder b, Hash128 lineId, Hash128 eventId, Hash128 playingId,
        string gameText, string date,
        GameOutcome result, Hash128? whitePlayer, Hash128? blackPlayer, int whiteElo, int blackElo,
        Hash128 src)
    {
        b.AddEntity(lineId, EntityTier.Document, ChessVocabulary.GameType, src);
        b.AddEntity(eventId, EntityTier.Document, ChessVocabulary.EventType, src);
        b.AddEntity(playingId, EntityTier.Document, ChessVocabulary.PlayingType, src);

        b.AddAttestation(NativeAttestation.CategoricalResolved(
            playingId, ChessVocabulary.PlaysLineType, lineId, src, null, PgnWitnessWeight));
        b.AddAttestation(NativeAttestation.Categorical(
            playingId, HasEventRelation, eventId, src, null, PgnWitnessWeight));

        if (whitePlayer is { } wp) b.AddAttestation(NativeAttestation.Categorical(lineId, "HAS_WHITE", wp, src, playingId, PgnWitnessWeight));
        if (blackPlayer is { } bp) b.AddAttestation(NativeAttestation.Categorical(lineId, "HAS_BLACK", bp, src, playingId, PgnWitnessWeight));

        if (whitePlayer is { } w2)
            ChessGraph.AppendPlayerResult(
                b, w2, blackPlayer, result.ForMover(0), PgnWitnessWeight, src, playingId);
        if (blackPlayer is { } b2)
            ChessGraph.AppendPlayerResult(
                b, b2, whitePlayer, result.ForMover(1), PgnWitnessWeight, src, playingId);

        Meta(b, lineId, HasEventRelation, PgnGames.TagStr(gameText, "Event"), src, playingId);
        Meta(b, lineId, "ON_DATE", date, src, playingId);
        Meta(b, lineId, "HAS_ECO", PgnGames.TagStr(gameText, "ECO"), src, playingId);
        Meta(b, lineId, "HAS_TERMINATION", PgnGames.TagStr(gameText, "Termination"), src, playingId);
        Meta(b, lineId, "HAS_RESULT", result.ResultToken, src, playingId);

        string tc = PgnGames.TagStr(gameText, "TimeControl");
        Meta(b, lineId, "HAS_TIME_CONTROL", tc, src, playingId);
        Meta(b, lineId, "HAS_TC_CLASS", TcClass(tc), src, playingId);

        if (whitePlayer is { } wp2 && whiteElo > 0) Rating(b, wp2, whiteElo, playingId, src);
        if (blackPlayer is { } bp2 && blackElo > 0) Rating(b, bp2, blackElo, playingId, src);
    }

    private static void Meta(
        SubstrateChangeBuilder b, Hash128 line, string rel, string value, Hash128 src, Hash128 eventId)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "?" || value == "-" || value == "????.??.??") return;
        if (ContentEmitter.Emit(b, value, src) is { } vid)
            b.AddAttestation(NativeAttestation.Categorical(line, rel, vid, src, eventId, PgnWitnessWeight));
    }

    private static void Rating(SubstrateChangeBuilder b, Hash128 player, int elo, Hash128 eventId, Hash128 src)
    {
        if (ContentEmitter.Emit(b, elo.ToString(), src) is { } rid)
            b.AddAttestation(NativeAttestation.Categorical(player, "HAS_RATING", rid, src, eventId, PgnWitnessWeight));
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
        var legacyId = ChessVocabulary.LegacyPlayerId(name);
        if (legacyId != canonicalId)
            b.AddAttestation(NativeAttestation.Categorical(
                canonicalId, "CORRESPONDS_TO", legacyId, src, null, PgnWitnessWeight));
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
    Hash128[] MoveIds);

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
    : ITrunkRootRecord
{
    internal PgnMovetext.PgnWalkResult Walk { get; init; } = null!;

    internal string? WhiteName { get; init; }
    internal string? BlackName { get; init; }
    internal string? Date { get; init; }
    internal string? StartFen { get; init; }

    internal Hash128[] PositionIds { get; init; } = [];
    internal ChessMove[] ResolvedMoves { get; init; } = [];
    internal Piece[] MovingPieces { get; init; } = [];
    internal Hash128[] MoveIds { get; init; } = [];
    internal bool NormalCompletionVerified { get; init; }

    public Hash128 TrunkRootId => PlayingId;
}
