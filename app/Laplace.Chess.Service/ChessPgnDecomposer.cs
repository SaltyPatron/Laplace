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

    private const int GamesPerBatch = 64;

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
        if (options.DryRun) yield break;

        // Serial yielder: one tiny parse per streamed game, the deferred-content skip kept on. The
        // IngestRunner parallelizes the commit lanes + the consensus fold runs parallel partitions —
        // that's the parallelism; rolling our own on top double-enumerated and re-processed.
        var modality = new ChessModality();
        foreach (var file in EnumerateFiles(context.EcosystemPath))
        {
            ct.ThrowIfCancellationRequested();
            var builder = NewBuilder(context);
            int inBatch = 0;
            await foreach (var gameText in StreamGamesAsync(file, ct).WithCancellation(ct))
            {
                ct.ThrowIfCancellationRequested();
                var gameBytes = Encoding.UTF8.GetBytes(gameText);

                List<string> moves;
                GameOutcome? result;
                using (var ast = GrammarDecomposer.Parse(gameBytes, "pgn"))
                    (moves, result) = ExtractGame(ast, gameBytes);
                if (result is null || moves.Count == 0) continue;

                var (whiteElo, blackElo) = ParseElos(gameText);
                var (whiteName, blackName) = ParseNames(gameText);
                var whitePlayer = EmitPlayer(builder, whiteName);
                var blackPlayer = EmitPlayer(builder, blackName);

                // The conventional GAME tier: one Chess_Game node carrying the full metadata, content-addressed
                // by (players + date + moves) so the same game across DBs is recorded once + witnessed each time.
                string date = PgnGames.TagStr(gameText, "Date");
                var gameId = ChessVocabulary.GameId(whiteName, blackName, date, moves);
                EmitGame(builder, gameId, gameText, result.Value, whitePlayer, blackPlayer, whiteElo, blackElo);

                // Recover the per-move clocks the grammar strips → think-time evidence weight (no-op when absent).
                var clocks = PgnClocks.SecondsRemaining(gameText, moves.Count);
                double medianDrop = PgnClocks.MedianDrop(clocks);
                AppendGame(builder, modality, moves, result.Value,
                           whiteElo, blackElo, whitePlayer, blackPlayer, clocks, medianDrop, gameId);

                if (++inBatch >= GamesPerBatch)
                {
                    yield return await builder.SetInputUnitsConsumed(inBatch).BuildAsync(ct);
                    builder = NewBuilder(context);
                    inBatch = 0;
                }
            }
            if (inBatch > 0)
                yield return await builder.SetInputUnitsConsumed(inBatch).BuildAsync(ct);
        }
    }

    private static SubstrateChangeBuilder NewBuilder(IDecomposerContext ctx)
        // Deferred-content skip ON: chess openings/positions repeat massively across games, so the probe
        // lets repeated content stage once instead of re-emitting every occurrence. Serial enumeration
        // keeps the probe's connection use bounded.
        => new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "chess/pgn").EnableDeferredContent(ctx.Reader);

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
        b.AddEntity(gameId, EntityTier.Vocabulary, ChessVocabulary.GameType, src);

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
        SubstrateChangeBuilder b, ChessModality m, List<string> sans, GameOutcome result,
        int whiteElo, int blackElo, Hash128? whitePlayer, Hash128? blackPlayer,
        double[] clocks, double medianDrop, Hash128 gameId)
    {
        bool mate = sans.Count > 0 && sans[^1].IndexOf('#') >= 0; // '#' = proven checkmate
        int? winner = result.IsDraw ? null : result.Winner;

        var state = m.Initial();
        for (int ply = 0; ply < sans.Count; ply++)
        {
            var mv = San.Resolve(state.Board, m.LegalActions(state), sans[ply]);
            if (mv is null) return; // malformed/illegal token → skip the rest of this game
            int mover = m.SideToMove(state);
            var next = m.Apply(state, mv.Value);
            // OUTCOME weight = DEFENDER Elo (anti-trap: a result against a strong defender is stronger
            // evidence). MOVE-CHOICE weight = the MOVER's Elo ("Magnus's e4 here outweighs a 1200's"),
            // FURTHER scaled by think-time: a move played after a real think is stronger testimony of
            // intent than a pre-move/scramble (ThinkFactor∈[0.5,1.5]; 1.0 when the game has no clocks).
            long games = EloGames(mover == 0 ? blackElo : whiteElo);
            if (mate && winner == mover) games += games / 2; // +50% for the confirmed-mating side
            double tf = PgnClocks.ThinkFactor(clocks, medianDrop, ply);
            long moveChoiceGames = Math.Max(1, (long)Math.Round(EloGames(mover == 0 ? whiteElo : blackElo) * tf));
            ChessGraph.AppendMoveEdge(
                b, m.StateKey(state), m.StateKey(next), result.ForMover(mover), games, PgnWitnessWeight,
                sourceId: ChessVocabulary.PgnSourceId,
                moverPlayerId: mover == 0 ? whitePlayer : blackPlayer,
                moveChoiceGames: moveChoiceGames,
                contextId: gameId);
            state = next;
        }
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
        => string.IsNullOrWhiteSpace(name) || name == "?"
            ? null
            : ChessVocabulary.EmitPlayer(b, ChessVocabulary.PlayerId(name), name, ChessVocabulary.PgnSourceId);

    /// <summary>Walk one game's parse tree: ordered mainline SAN + the terminating result.</summary>
    private static (List<string> Moves, GameOutcome? Result) ExtractGame(GrammarAst ast, byte[] utf8)
    {
        var moves = new List<string>();
        GameOutcome? result = null;
        int n = ast.NodeCount;
        for (int i = 0; i < n; i++)
        {
            var node = ast.GetNode(i);
            var name = ast.NodeTypeName(node.NodeTypeId);
            if (name == "san_move")
            {
                if (!InsideVariation(ast, node)) moves.Add(Text(utf8, node));
            }
            else if (name == "game_result")
            {
                result = ParseResult(Text(utf8, node));
            }
        }
        return (moves, result);
    }

    private static bool InsideVariation(GrammarAst ast, LaplaceAstNode node)
    {
        uint p = node.Parent;
        while (p != GrammarAst.Root)
        {
            var pn = ast.GetNode((int)p);
            if (ast.NodeTypeName(pn.NodeTypeId) == "variation") return true;
            p = pn.Parent;
        }
        return false;
    }

    private static string Text(byte[] utf8, LaplaceAstNode node)
        => Encoding.UTF8.GetString(utf8, (int)node.StartByte, (int)(node.EndByte - node.StartByte)).Trim();

    private static GameOutcome? ParseResult(string r) => r switch
    {
        "1-0" => GameOutcome.WonBy(0),
        "0-1" => GameOutcome.WonBy(1),
        "1/2-1/2" => GameOutcome.Draw,
        _ => null, // "*" — unfinished, no outcome to learn from
    };

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
