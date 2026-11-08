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

public sealed partial class ChessPgnDecomposer(bool recursive = false, bool analyzeInline = true)
    : ComposeDecomposerMultiFile<ChessGameRecord>, IIngestInventoryProvider, IIngestNoOpExplainer
{
    private readonly SearchOption _scope =
        recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
    private readonly bool _analyzeInline = analyzeInline;

    public override Hash128 SourceId => ChessVocabulary.PgnSourceId;
    internal const string PgnSourceName = "ChessPgn";
    internal const int PgnLayerOrder = 20;

    public override string SourceName => PgnSourceName;
    public override int LayerOrder => PgnLayerOrder;
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
            ..ChessCorpus.Names.Select(name => new ChessVocabulary.BootstrapSource(
                ChessCorpus.SourceId(name), name, ChessVocabulary.PgnTrustClass)),
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
            if (TryParseGame(gameText) is { } parsed) chunk.Add(WithCorpus(parsed, filePath));
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
            if (TryPeekPlaying(gameText) is { } peek) peeks.Add(WithCorpus(peek, filePath));
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
            var presenceScope = reader.CapturePresenceScope();
            byte[] bm = await reader.TierBatchExistenceProbeAsync(
                slice, ChessCompose.PositionTier, ct).ConfigureAwait(false);
            var proven = new List<Hash128>(n);
            for (int j = 0; j < n; j++)
                if (BitmapBits.IsSet(bm, j)) proven.Add(slice[j]);
            if (proven.Count > 0) reader.MarkProven(proven, presenceScope);
        }
    }

    internal static async IAsyncEnumerable<ChessGameRecord> YieldNovelParsedAsync(
        List<ChessPlayingPeek> peeks, ISubstrateReader? reader, bool reObservePresent,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var games = peeks.Select(static p => p.Game).ToList();
        await foreach (var game in YieldChunkAsync(games, reader, reObservePresent, ct)
                           .ConfigureAwait(false))
            yield return game;
    }

    internal static ChessPlayingPeek? TryPeekPlaying(string gameText)
    {
        var game = TryParseGame(gameText);
        return game is null ? null : new ChessPlayingPeek(game, game.PlayingId);
    }

    internal static ChessGameRecord WithCorpus(ChessGameRecord game, string? filePath = null)
    {
        if (game.Corpus is not null) return game;
        string? name = ChessCorpus.FromPath(filePath) ?? ChessCorpus.FromSite(PgnGames.TagStr(game.GameText, "Site"));
        return name is null ? game : game with { Corpus = name };
    }

    private static ChessPlayingPeek WithCorpus(ChessPlayingPeek peek, string filePath)
        => peek with { Game = WithCorpus(peek.Game, filePath) };

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
        if (ChessAnalyze.InitialState(game.StartFen, modality, game.Rules) is not { } start)
            return ChessParsedReplay.Empty;

        int moveCount = game.ResolvedMoves.Length;
        var boards = new Board[moveCount + 1];
        var positions = new ChessComposed[moveCount + 1];
        if (game.ReplayBoards is { } replayBoards)
        {
            for (int ply = 0; ply <= moveCount; ply++) positions[ply] = ChessCompose.Position(replayBoards[ply]);
            return new ChessParsedReplay(replayBoards, positions, game.ResolvedMoves, start.StandardStart);
        }
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

    internal static Hash128 RecordingSourceId(ChessGameRecord game, Hash128? sourceId = null)
    {
        if (sourceId is { } explicitSource) return explicitSource;
        string? corpus = game.Corpus ?? ChessCorpus.FromSite(PgnGames.TagStr(game.GameText, "Site"));
        return corpus is null ? ChessVocabulary.PgnSourceId : ChessCorpus.SourceId(corpus);
    }

    internal static Hash128 RecordingWitnessId(ChessGameRecord game)
        => NativeAttestation.ComputeId(game.PlayingId, ChessVocabulary.PlaysLineType,
            game.LineId, RecordingSourceId(game), null);

    internal static async IAsyncEnumerable<ChessGameRecord> FilterNovelAsync(
        List<ChessGameRecord> chunk, ISubstrateReader? reader, [EnumeratorCancellation] CancellationToken ct)
    {
        if (chunk.Count == 0) yield break;
        if (reader is null) { foreach (var game in chunk) yield return game; yield break; }

        // A PLAYING entity may survive detached E/P COPY after the control transaction
        // rolls back. Only the exact PGN witness proves recording acceptance. It commits
        // with the accepted testimony, consensus fold and working-set receipt. This is
        // not a claim that older record-only ingests already contain every calculated lane.
        var ids = chunk.Select(RecordingWitnessId).ToArray();
        var accepted = await reader.PresentAttestationIdsAsync(
            ChessVocabulary.PlaysLineType, ids, ct).ConfigureAwait(false);
        for (int i = 0; i < chunk.Count; i++)
            if (!accepted.Contains(ids[i])) yield return chunk[i];
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

    internal static ChessGameRecord? TryParseGame(string gameText, bool requireNormalCompletion = false,
        bool requireCompleteSource = false)
    {
        try
        {
            string variant = PgnGames.TagStr(gameText, "Variant");
            var rules = ChessVariants.ByName(variant);
            if (!string.IsNullOrWhiteSpace(variant) && (rules is null || !NativeChessRules.Supports(rules)))
                throw new InvalidDataException(
                    $"recording replay is not implemented for variant '{DiagnosticTag(variant)}'; "
                    + "its positions and moves cannot be admitted under standard chess rules");
            return ParseGameCore(gameText, requireNormalCompletion, requireCompleteSource);
        }
        catch (InvalidDataException ex)
        {
            string link = PgnGames.TagStr(gameText, "Link");
            if (string.IsNullOrWhiteSpace(link)) link = PgnGames.TagStr(gameText, "Site");
            throw new InvalidDataException(
                $"PGN {DiagnosticTag(PgnGames.TagStr(gameText, "White"))} vs "
                + $"{DiagnosticTag(PgnGames.TagStr(gameText, "Black"))} "
                + $"({DiagnosticTag(PgnGames.TagStr(gameText, "Date"))}; {DiagnosticTag(link)}): {ex.Message}", ex);
        }
    }

    private static string DiagnosticTag(string value)
        => new(value.Take(240).Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    private static ChessGameRecord? ParseGameCore(string gameText, bool requireNormalCompletion,
        bool requireCompleteSource)
    {
        bool strict = requireNormalCompletion || requireCompleteSource;
        var gameBytes = Encoding.UTF8.GetBytes(gameText);
        PgnMovetext.PgnWalkResult walk;
        using (var ast = GrammarDecomposer.Parse(gameBytes, "pgn"))
        {
            if (strict && !ast.Diagnostics.SyntaxComplete)
            {
                var diagnostics = ast.Diagnostics;
                string location = "";
                for (int i = 0; i < ast.NodeCount; i++)
                {
                    var node = ast.GetNode(i);
                    if (!ast.NodeTypeIs(node.NodeTypeId, "ERROR"u8)) continue;
                    location = $"; first syntax error at UTF-8 bytes {node.StartByte}..{node.EndByte}";
                    break;
                }
                throw new InvalidDataException(
                    $"recorded PGN requires a complete native syntax parse "
                    + $"(errors={diagnostics.ErrorNodeCount}, missing={diagnostics.MissingNodeCount}{location})");
            }
            if (strict)
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
            if (strict)
                throw new InvalidDataException("normal recorded PGN requires a finished serialized result");
            ChessDropLedger.Drop(ChessDropLedger.NoResultOrMoves, Headline(gameText));
            return null;
        }

        var moves = walk.Mainline.Select(p => p.San).ToList();
        var result = walk.Result.Value;
        if (strict && PgnGames.TagStr(gameText, "Result") != result.ResultToken)
            throw new InvalidDataException("complete recorded PGN header and movetext result disagree");
        string termination = PgnGames.TagStr(gameText, "Termination");
        if (requireCompleteSource && termination.ToLowerInvariant()
            is "unterminated" or "abandoned")
            throw new InvalidDataException("corpus source declares an unfinished game");
        bool requireSourceTerminal = requireCompleteSource && SourceDeclaresBoardTerminal(termination);

        string declaredPlies = PgnGames.TagStr(gameText, "PlyCount");
        if (strict && declaredPlies.Length > 0 && (!int.TryParse(declaredPlies,
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                out int plyCount) || plyCount != moves.Count))
            throw new InvalidDataException("complete recorded PGN PlyCount differs from its main line");
        var (whiteName, blackName) = ParseNames(gameText);
        string date = PgnGames.TagStr(gameText, "Date");

        string? startFen = PgnGames.TagStr(gameText, "SetUp") == "1"
            ? PgnGames.TagStr(gameText, "FEN") : null;
        if (strict && startFen is not null && string.IsNullOrWhiteSpace(startFen))
            throw new InvalidDataException("normal recorded PGN declares SetUp=1 without its FEN");
        if (strict && startFen is null && !string.IsNullOrEmpty(PgnGames.TagStr(gameText, "FEN")))
            throw new InvalidDataException("normal recorded PGN has a FEN without SetUp=1");
        var variantRules = ChessVariants.ByName(PgnGames.TagStr(gameText, "Variant")) ?? ChessVariants.Standard;
        if (!variantRules.IsStandard)
            startFen = Board.FromFen(startFen ?? ChessModality.StartFen, variantRules).ToFen();
        var replay = TryReplayLineDetailed(moves, startFen,
            expectedNormalOutcome: requireNormalCompletion ? result : null,
            expectedSourceOutcome: requireCompleteSource ? result : null,
            requireSourceTerminal: requireSourceTerminal, rules: variantRules);
        if (replay is null)
        {
            if (strict)
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
            Rules = variantRules,
            InitialWhiteToMove = replay.InitialWhiteToMove,
            PositionIds = replay.PositionIds,
            ResolvedMoves = replay.Moves,
            MovingPieces = replay.MovingPieces,
            MoveIds = replay.MoveIds,
            ReplayBoards = replay.Boards,
            NormalCompletionVerified = requireNormalCompletion,
            CompleteSourceVerified = strict,
        };
    }

    // The board ended the game only when the source says so as its ending ("FabianoCaruana won by checkmate",
    // "Game drawn by stalemate", or the bare word), never when a player's name merely contains the word: chess.com's
    // "Chasing_Checkmate won on time" is a time loss, and reading it as checkmate aborted a whole archive fetch.
    private static bool SourceDeclaresBoardTerminal(string termination)
    {
        string t = termination.Trim();
        return t.Equals("checkmate", StringComparison.OrdinalIgnoreCase)
            || t.Equals("stalemate", StringComparison.OrdinalIgnoreCase)
            || t.EndsWith(" by checkmate", StringComparison.OrdinalIgnoreCase)
            || t.EndsWith(" by stalemate", StringComparison.OrdinalIgnoreCase);
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
        IReadOnlyList<string> sans, string? startFen, GameOutcome? expectedNormalOutcome = null,
        GameOutcome? expectedSourceOutcome = null, bool requireSourceTerminal = false, ChessVariantRules? rules = null)
    {
        var m = new ChessModality();
        if (ChessAnalyze.InitialState(startFen, m, rules) is not { } start) return null;
        var board = start.Initial.Board.Clone();
        // The ordinary move owner supports kingless puzzle positions. They must
        // never certify a normal completed game, nor may the nonmoving king
        // already be in check when the supplied game starts.
        if ((expectedNormalOutcome is not null || expectedSourceOutcome is not null)
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
        var nativeReplay = !board.Rules.IsStandard ? NativeChessRules.Replay(board, sans)
            : ((Board[] Boards, ChessMove[] Moves)?)null;
        for (int ply = 0; ply < sans.Count; ply++)
        {
            if (history is not null && m.Terminal(new ChessState(board, history)) is not null)
                throw new InvalidDataException("normal recorded game contains a move after its terminal position");
            var mv = nativeReplay is { } replay ? replay.Moves[ply] : San.Resolve(board, sans[ply], scratch);
            if (mv is null) return null;
            Piece moving = board.MovingPiece(mv.Value);
            moves[ply] = mv.Value;
            movingPieces[ply] = moving;
            var moveId = ChessCompose.MoveId(moving, mv.Value);
            moveIds[ply] = moveId;
            var tKey = ChessCompose.TransitionKey(ids[ply], moveId);
            if (nativeReplay is { } native) board = native.Boards[ply + 1];
            else MoveApply.Make(board, mv.Value);
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
        // Human source games may end by resignation, timeout or agreement while legal
        // moves remain. But if the provider explicitly says checkmate/stalemate, the stored
        // source line itself must end at a board-terminal position with the declared result.
        // This rejects header-only "checkmates" that would otherwise create a PLAYING witness
        // with no move carrier and later surface as a game that cannot be replayed.
        if (expectedSourceOutcome is { } sourceOutcome)
        {
            var legal = MoveGen.Legal(board);
            var forced = legal.Count == 0
                ? m.Terminal(new ChessState(board, ImmutableList.Create(ids[^1])))
                : null;
            if (requireSourceTerminal && (legal.Count != 0 || forced != sourceOutcome))
                throw new InvalidDataException(
                    "complete source game declares a board-terminal finish not present in its move trajectory");
            if (!requireSourceTerminal && legal.Count == 0 && forced != sourceOutcome)
                throw new InvalidDataException("complete source game's forced terminal outcome differs from its result");
        }
        return new ChessLineReplay(ids, moves, movingPieces, moveIds, start.Initial.Board.WhiteToMove)
            { Boards = nativeReplay?.Boards };
    }
}
