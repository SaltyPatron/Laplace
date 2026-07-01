using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

/// <summary>
/// Ingests PGN game files into the substrate. Uses our <c>pgn</c> tree-sitter grammar ONLY to extract
/// clean structure (ordered <c>san_move</c> tokens + the <c>game_result</c>, free of clocks/comments),
/// then supplies the chess semantics itself: replay each game through the perft-verified movegen
/// (<see cref="San.Resolve"/>), compose each position from its substructures, and score the resulting
/// edges by the game result. The substrate fills with chess positions + rated moves — not PGN-notation
/// text — so these edges fold into the same graph self-play uses.
///
/// <para><b>Stage 1 (this class) STREAMS</b>: it reads each file record-by-record (one game at a time),
/// never the whole file or its AST in bulk — so peak RAM is O(one game), independent of file size (the
/// 195 MB+ files ingest on a Pi). Per-game evidence is <b>Elo-weighted by the OPPONENT's rating</b> (the
/// anti-trap: a result against a strong defender is stronger evidence; Scholar's-Mate win-rate collapses
/// once weighted by defender Elo). Clock/criticality weighting + game-level dedup-before-compute (the
/// present-trunk skip) are the native O(tier) write path's job (Track 1), not done here.</para>
/// </summary>
public sealed class ChessPgnDecomposer : IDecomposer
{
    public Hash128 SourceId     => ChessVocabulary.PgnSourceId;
    public string  SourceName   => "ChessPgn";
    public int     LayerOrder   => 20;
    public Hash128 TrustClassId => ChessVocabulary.PgnTrustClass;

    // NOTE: NO game-level skip. Dedup is STRUCTURAL — a position/move composes deterministically + losslessly
    // to a content id (g2g3·f1g2 = the fianchetto, the same way [c,a,t] = "cat"), so it is RECORDED ONCE and
    // WITNESSED every time it's played; the witness count IS the run-length. Skipping a repeated game would
    // suppress a real witness (and discard that play's provenance). Repetition is the signal, not waste.

    private IReadOnlyCollection<string> _canonicalNames = Array.Empty<string>();

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => _canonicalNames = await ChessVocabulary.BootstrapAsync(
            context.Writer, ChessVocabulary.PgnSourceId, SourceName, ChessVocabulary.PgnTrustClass, ct);

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var modality = new ChessModality();
        int batch    = options.BatchSize > 1 ? options.BatchSize : 512;

        await foreach (var change in DecomposerBatch.RunAsync(
            StreamAllGamesAsync(context.EcosystemPath, ct),
            (gameText, b) => ComposeGame(gameText, b, modality),
            ChessVocabulary.PgnSourceId, "chess/pgn", batch, context.Reader, options, ct))
            yield return change;
    }

    // Streams every game text string across all PGN files under the ecosystem path.
    private static async IAsyncEnumerable<string> StreamAllGamesAsync(
        string ecosystemPath, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var file in EnumerateFiles(ecosystemPath))
        {
            ct.ThrowIfCancellationRequested();
            await foreach (var gameText in StreamGamesAsync(file, ct).WithCancellation(ct))
                yield return gameText;
        }
    }

    private static void ComposeGame(string gameText, SubstrateChangeBuilder b, ChessModality modality)
    {
        var gameBytes = Encoding.UTF8.GetBytes(gameText);
        PgnMovetext.PgnWalkResult walk;
        using (var ast = GrammarDecomposer.Parse(gameBytes, "pgn"))
            walk = PgnMovetext.Walk(ast, gameBytes);
        if (walk.Result is null || walk.Mainline.Count == 0) return;

        var moves = walk.Mainline.Select(p => p.San).ToList();
        var result = walk.Result.Value;

        var (whiteElo, blackElo) = ParseElos(gameText);
        var (whiteName, blackName) = ParseNames(gameText);
        var whitePlayer = EmitPlayer(b, whiteName);
        var blackPlayer = EmitPlayer(b, blackName);

        string date = PgnGames.TagStr(gameText, "Date");
        var gameId = ChessVocabulary.GameId(whiteName, blackName, date, moves);
        EmitGame(b, gameId, gameText, result, whitePlayer, blackPlayer, whiteElo, blackElo);

        int moveCount = moves.Count;
        var clockTokens = PgnClocks.ClockTokens(gameText, moveCount);
        var clocks = clockTokens is not null
            ? clockTokens.Select(t =>
            {
                var p = t.Split(':');
                return int.Parse(p[0]) * 3600 + int.Parse(p[1]) * 60
                    + double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture);
            }).ToArray()
            : Array.Empty<double>();
        double medianDrop = PgnClocks.MedianDrop(clocks);
        var evalTokens = PgnEvals.EvalTokens(gameText, moveCount);
        var evals = evalTokens is not null
            ? evalTokens.Select(PgnEvals.ParseToken).ToArray()
            : PgnEvals.Centipawns(gameText, moveCount);

        EmitGameOpeningMeta(b, gameId, gameText, moves, modality);

        AppendGame(b, modality, walk.Mainline, result,
                   whiteElo, blackElo, whitePlayer, blackPlayer, clocks, medianDrop, gameId,
                   evals, clockTokens, evalTokens);

        if (string.Equals(Environment.GetEnvironmentVariable("LAPLACE_CHESS_VARIATIONS"), "1", StringComparison.Ordinal))
            AppendVariations(b, modality, walk.AllPlies, whiteElo, blackElo, whitePlayer, blackPlayer);
    }

    private static SubstrateChangeBuilder NewBuilder(IDecomposerContext ctx)
        => new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "chess/pgn");

    /// <summary>
    /// Stream a PGN file game-by-game: read lines, accumulate from one <c>[Event </c> tag to the next,
    /// yield each game's text, discard. Peak RAM = O(one game), never the whole file. UTF-8.
    /// </summary>
    private static async IAsyncEnumerable<string> StreamGamesAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var sb = new StringBuilder(2048);
        bool inGame = false;
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (line.StartsWith("[Event ", StringComparison.Ordinal))
            {
                if (inGame && sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                inGame = true;
            }
            if (inGame) { sb.Append(line); sb.Append('\n'); }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    // Constant witness weight across the whole run → constant φ per relation (the fold/accumulator
    // invariant). Trust (Elo, confirmed mate) is encoded in the GAME-COUNT, not the weight.
    private const double PgnWitnessWeight = 0.7;

    /// <summary>Replay one game's SAN moves, emitting edges scored by result. Trust is the Glicko
    /// observation count: weighted by the OPPONENT (defender) Elo — the anti-trap — and boosted for the
    /// side that delivered a CONFIRMED mate (terminal <c>#</c>) vs a bare result (resignation/time, the
    /// opponent's judgment). Aborts on an unresolved move (malformed/illegal token).</summary>
    /// <summary>Emit the Chess_Game node + its full conventional metadata: white/black player, event, date,
    /// ECO, time control + class, termination, result, and per-game HAS_RATING (the rating tied to THIS game
    /// via contextId). Each value is a content entity so identical metadata across games converges.</summary>
    private static void EmitGame(
        SubstrateChangeBuilder b, Hash128 gameId, string gameText, GameOutcome result,
        Hash128? whitePlayer, Hash128? blackPlayer, int whiteElo, int blackElo)
    {
        var src = ChessVocabulary.PgnSourceId;
        b.AddEntity(gameId, EntityTier.Document, ChessVocabulary.GameType, src);

        if (whitePlayer is { } wp) b.AddAttestation(NativeAttestation.Categorical(gameId, "HAS_WHITE", wp, src, null, PgnWitnessWeight));
        if (blackPlayer is { } bp) b.AddAttestation(NativeAttestation.Categorical(gameId, "HAS_BLACK", bp, src, null, PgnWitnessWeight));

        Meta(b, gameId, "HAS_EVENT",       PgnGames.TagStr(gameText, "Event"), src);
        Meta(b, gameId, "ON_DATE",         PgnGames.TagStr(gameText, "Date"), src);
        Meta(b, gameId, "HAS_ECO",         PgnGames.TagStr(gameText, "ECO"), src);
        Meta(b, gameId, "HAS_TERMINATION", PgnGames.TagStr(gameText, "Termination"), src);
        Meta(b, gameId, "HAS_RESULT",      result.IsDraw ? "1/2-1/2" : result.Winner == 0 ? "1-0" : "0-1", src);

        string tc = PgnGames.TagStr(gameText, "TimeControl");
        Meta(b, gameId, "HAS_TIME_CONTROL", tc, src);
        Meta(b, gameId, "HAS_TC_CLASS",     TcClass(tc), src);

        if (whitePlayer is { } wp2 && whiteElo > 0) Rating(b, wp2, whiteElo, gameId, src);
        if (blackPlayer is { } bp2 && blackElo > 0) Rating(b, bp2, blackElo, gameId, src);
    }

    /// <summary>Game-level opening/ECO/motif from PGN headers + classifier.</summary>
    private static void EmitGameOpeningMeta(
        SubstrateChangeBuilder b, Hash128 gameId, string gameText,
        IReadOnlyList<string> sans, ChessModality modality)
    {
        var src = ChessVocabulary.PgnSourceId;
        string ecoHeader = ChessCanonical.Eco(PgnGames.TagStr(gameText, "ECO")) ?? "";
        if (ecoHeader.Length > 0)
            ChessGraph.AppendGameMeta(b, gameId, "GAME_HAS_ECO", ecoHeader, PgnWitnessWeight, src);

        string openingHeader = ChessCanonical.OpeningName(PgnGames.TagStr(gameText, "Opening")) ?? "";
        if (openingHeader.Length > 0)
            ChessGraph.AppendGameMeta(b, gameId, "GAME_HAS_OPENING", openingHeader, PgnWitnessWeight, src);

        var classified = OpeningClassifier.Classify(sans, modality);
        if (classified.Eco is { } eco && eco != ecoHeader)
            ChessGraph.AppendGameMeta(b, gameId, "GAME_HAS_ECO", eco, PgnWitnessWeight, src);
        if (classified.Name is { } name && name != openingHeader)
            ChessGraph.AppendGameMeta(b, gameId, "GAME_HAS_OPENING", name, PgnWitnessWeight, src);

        if (ChessMotifs.Detect(sans) is { } motif)
            ChessGraph.AppendGameMeta(b, gameId, "GAME_HAS_MOTIF", motif, PgnWitnessWeight, src);
    }

    /// <summary>Game → metadata-value content edge (skips empty/"?"/"-" placeholders).</summary>
    private static void Meta(SubstrateChangeBuilder b, Hash128 game, string rel, string value, Hash128 src)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "?" || value == "-" || value == "????.??.??") return;
        if (ContentEmitter.Emit(b, value, src) is { } vid)
            b.AddAttestation(NativeAttestation.Categorical(game, rel, vid, src, null, PgnWitnessWeight));
    }

    /// <summary>Player HAS_RATING (rating value), contextId = the game it applied to (ratings are per-game).</summary>
    private static void Rating(SubstrateChangeBuilder b, Hash128 player, int elo, Hash128 game, Hash128 src)
    {
        if (ContentEmitter.Emit(b, elo.ToString(), src) is { } rid)
            b.AddAttestation(NativeAttestation.Categorical(player, "HAS_RATING", rid, src, game, PgnWitnessWeight));
    }

    /// <summary>Classify a PGN <c>TimeControl</c> tag into bullet/blitz/rapid/classical by base seconds
    /// (the move-evidence axis: a blitz move is weaker testimony than a classical one). "" when unknown.</summary>
    internal static string TcClass(string tc)
    {
        if (string.IsNullOrWhiteSpace(tc) || tc == "-") return "";
        if (tc.Contains('/')) return "classical";                  // "40/7200" tournament form
        int plus = tc.IndexOf('+');
        string baseStr = plus >= 0 ? tc[..plus] : tc;
        if (!int.TryParse(baseStr, out int baseSec)) return "";
        return baseSec < 180 ? "bullet" : baseSec < 600 ? "blitz" : baseSec < 1500 ? "rapid" : "classical";
    }

    private static void AppendGame(
        SubstrateChangeBuilder b, ChessModality m, IReadOnlyList<PgnMovetext.PgnMoveStream> plies,
        GameOutcome result, int whiteElo, int blackElo, Hash128? whitePlayer, Hash128? blackPlayer,
        double[] clocks, double medianDrop, Hash128 gameId, int[]? evals,
        string[]? clockTokens, string[]? evalTokens)
    {
        bool mate = plies.Count > 0 && plies[^1].San.IndexOf('#') >= 0;
        int? winner = result.IsDraw ? null : result.Winner;
        const long evalGames = 2;
        const double evalWeight = 0.55;
        const double metaWeight = PgnWitnessWeight;

        var state = m.Initial();
        for (int ply = 0; ply < plies.Count; ply++)
        {
            var plyStream = plies[ply];
            var mv = San.Resolve(state.Board, m.LegalActions(state), plyStream.San);
            if (mv is null) return;
            int mover = m.SideToMove(state);
            var next = m.Apply(state, mv.Value);
            string fromKey = m.StateKey(state);

            long games = EloGames(mover == 0 ? blackElo : whiteElo);
            if (mate && winner == mover) games += games / 2;
            double tf = PgnClocks.ThinkFactor(clocks, medianDrop, ply);
            long moveChoiceGames = Math.Max(1, (long)Math.Round(EloGames(mover == 0 ? whiteElo : blackElo) * tf));

            ChessGraph.AppendMoveEdge(
                b, fromKey, m.StateKey(next), result.ForMover(mover), games, PgnWitnessWeight,
                sourceId: ChessVocabulary.PgnSourceId,
                moverPlayerId: mover == 0 ? whitePlayer : blackPlayer,
                moveChoiceGames: moveChoiceGames,
                contextId: gameId,
                ply: ply + 1);

            if (clockTokens is not null && ply < clockTokens.Length)
            {
                ChessGraph.AppendClock(b, fromKey, clockTokens[ply], metaWeight,
                    ChessVocabulary.PgnSourceId, gameId);
                if (clocks.Length > 0)
                    ChessGraph.AppendThinkClass(b, fromKey, ChessCanonical.ThinkClass(tf), metaWeight,
                        ChessVocabulary.PgnSourceId, gameId);
            }

            if (evalTokens is not null && ply < evalTokens.Length)
                ChessGraph.AppendEvalToken(b, fromKey, evalTokens[ply], metaWeight,
                    ChessVocabulary.EvalPgnSourceId, gameId);

            if (evals is not null && ply < evals.Length)
            {
                int cp = mover == 0 ? evals[ply] : -evals[ply];
                ChessGraph.AppendEval(b, fromKey, cp, evalGames, evalWeight, ChessVocabulary.EvalPgnSourceId, gameId);
            }

            if (MoveQuality.FromStream(plyStream) is { } quality)
                ChessGraph.AppendMoveQuality(
                    b, fromKey, quality, 1, PgnWitnessWeight * 0.5, ChessVocabulary.PgnSourceId, gameId);

            state = next;
        }
    }

    /// <summary>Variation plies: MOVE edges only, no game OUTCOME chain, lower trust, no game context.</summary>
    private static void AppendVariations(
        SubstrateChangeBuilder b, ChessModality m, IReadOnlyList<PgnMovetext.PgnMoveStream> allPlies,
        int whiteElo, int blackElo, Hash128? whitePlayer, Hash128? blackPlayer)
    {
        const double varWeight = 0.35;
        var mainState = m.Initial();
        var varState = m.Initial();
        bool inVar = false;

        foreach (var plyStream in allPlies)
        {
            if (plyStream.InVariation)
            {
                if (!inVar) { varState = CloneState(m, mainState); inVar = true; }
                if (TryPlay(m, ref varState, plyStream.San, out var fromKey, out var toKey, out int mover))
                {
                    long games = Math.Max(1, EloGames(mover == 0 ? blackElo : whiteElo) / 2);
                    ChessGraph.AppendMoveEdge(
                        b, fromKey, toKey, PlyOutcome.Draw, games, varWeight,
                        sourceId: ChessVocabulary.PgnSourceId,
                        moverPlayerId: mover == 0 ? whitePlayer : blackPlayer,
                        contextId: null);
                }
            }
            else
            {
                inVar = false;
                TryPlay(m, ref mainState, plyStream.San, out _, out _, out _);
            }
        }
    }

    private static ChessState CloneState(ChessModality m, ChessState s) => m.FromFen(s.Board.ToFen());

    private static bool TryPlay(
        ChessModality m, ref ChessState state, string san,
        out string fromKey, out string toKey, out int mover)
    {
        fromKey = toKey = "";
        mover = 0;
        var mv = San.Resolve(state.Board, m.LegalActions(state), san);
        if (mv is null) return false;
        mover = m.SideToMove(state);
        fromKey = m.StateKey(state);
        state = m.Apply(state, mv.Value);
        toKey = m.StateKey(state);
        return true;
    }

    /// <summary>Defender Elo → Glicko observation count (1..12): unknown → a neutral middle; rises with
    /// strength so master games dominate the fold and weak games barely move it.</summary>
    private static long EloGames(int elo)
        => elo <= 0 ? 3 : Math.Clamp((long)Math.Round((elo - 600) / 200.0), 1, 12);

    private static (int White, int Black) ParseElos(string game)
        => (PgnGames.TagInt(game, "WhiteElo"), PgnGames.TagInt(game, "BlackElo"));

    private static (string White, string Black) ParseNames(string game)
        => (PgnGames.TagStr(game, "White"), PgnGames.TagStr(game, "Black"));

    /// <summary>Mint (dedup) the player entity and bind its display name via HAS_NAME_ALIAS. Returns null
    /// for unknown players (""/"?"), so no PLAYED_BY is attributed. The rating's EFFECT lands per-move via
    /// the move-choice observation count; an explicit per-game HAS_RATING is a later refinement.</summary>
    private static Hash128? EmitPlayer(SubstrateChangeBuilder b, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "?") return null;
        var canonicalId = ChessVocabulary.PlayerId(name);
        ChessVocabulary.EmitPlayer(b, canonicalId, name, ChessVocabulary.PgnSourceId);
        var legacyId = ChessVocabulary.LegacyPlayerId(name);
        if (legacyId != canonicalId)
            b.AddAttestation(NativeAttestation.Categorical(
                canonicalId, "SAME_AS", legacyId, ChessVocabulary.PgnSourceId, null, PgnWitnessWeight));
        return canonicalId;
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        long games = 0;
        foreach (var f in EnumerateFiles(context.EcosystemPath))
        {
            try
            {
                // Stream-count [Event tags — never read the whole file (the 195 MB+ files would OOM).
                using var r = new StreamReader(f);
                string? line;
                while ((line = r.ReadLine()) is not null)
                    if (line.StartsWith("[Event ", StringComparison.Ordinal)) games++;
            }
            catch { /* skip unreadable */ }
        }
        return Task.FromResult<long?>(games == 0 ? null : games);
    }

    // The bootstrap's declared type/relation names (Chess_Position, Chess_Player, MOVE, PLAYED_BY, …);
    // RegisterDynamicCanonicalsAsync auto-adds substrate/source/ChessPgn/v1 from SourceName. So the types
    // are queryable by name, not only legible via the slow HAS_NAME_ALIAS traversal.
    public IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static IEnumerable<string> EnumerateFiles(string path)
    {
        if (string.IsNullOrEmpty(path)) yield break;
        if (File.Exists(path)) { yield return Path.GetFullPath(path); yield break; }
        if (!Directory.Exists(path)) yield break;
        foreach (var f in Directory.EnumerateFiles(path, "*.pgn", SearchOption.AllDirectories)
                                   .OrderBy(p => p, StringComparer.Ordinal))
            yield return f;
    }
}
