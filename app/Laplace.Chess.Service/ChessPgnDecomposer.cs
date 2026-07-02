using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

public sealed class ChessPgnDecomposer : IDecomposer
{
    public Hash128 SourceId => ChessVocabulary.PgnSourceId;
    public string SourceName => "ChessPgn";
    public int LayerOrder => 20;
    public Hash128 TrustClassId => ChessVocabulary.PgnTrustClass;






    private IReadOnlyCollection<string> _canonicalNames = Array.Empty<string>();

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => _canonicalNames = await ChessVocabulary.BootstrapAsync(
            context.Writer, ChessVocabulary.PgnSourceId, SourceName, ChessVocabulary.PgnTrustClass, ct);

    // Everything needed to compute a game's content-addressed GameId, cheaply enough to do for
    // an entire chunk before committing to the expensive per-ply work below.
    internal sealed record ParsedGame(
        string GameText, PgnMovetext.PgnWalkResult Walk, List<string> Moves, GameOutcome Result, Hash128 GameId);

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var modality = new ChessModality();
        // options.BatchSize is LAPLACE_INGEST_BATCH, a global knob sized for cheap flat
        // records (WordNet synsets, ConceptNet triples). A chess game is not a flat record —
        // it explodes into dozens-to-hundreds of entity/physicality/attestation rows per game.
        // Inheriting the global batch size directly (confirmed live at 65536 via env.cmd)
        // collapsed an 88,760-game file into ~1-2 giant intents, which in turn made every
        // IngestRunner commit-threshold check moot (nothing to flush until the one giant
        // intent finally dequeues) and forced a single ~20-minute apply_batch call plus a
        // single monolithic consensus fold at the very end. Capped independent of the global
        // knob so IngestRunner's row/intent flush thresholds can actually fire mid-run.
        int batch = Math.Clamp(options.BatchSize > 1 ? options.BatchSize : 512, 1, 512);

        await foreach (var change in DecomposerBatch.RunAsync(
            StreamNovelGamesAsync(context.EcosystemPath, context.Reader, batch, ct),
            (parsed, b) => ComposeParsedGame(parsed, b, modality),
            ChessVocabulary.PgnSourceId, "chess/pgn", batch, context.Reader, options, ct))
            yield return change;
    }

    // Trunk-to-leaf short-circuit: a game's GameId is itself an entity (EmitGame adds it, tier
    // Document). Parsing far enough to compute that id is cheap relative to full per-ply
    // decomposition (no board replay, no position composition, no attestation construction) —
    // so a whole chunk's ids get bulk-probed against the DB in ONE round trip before any of that
    // expensive work runs, and games already known are skipped outright instead of being fully
    // recomposed and then silently discarded by the final apply-time anti-join. Matters most
    // when ingesting overlapping corpora (the same famous games can appear in more than one of
    // chess.com exports / Lumbras / TWIC).
    internal static async IAsyncEnumerable<ParsedGame> StreamNovelGamesAsync(
        string ecosystemPath, ISubstrateReader? reader, int chunkSize,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var chunk = new List<ParsedGame>(chunkSize);
        await foreach (var gameText in StreamAllGamesAsync(ecosystemPath, ct))
        {
            if (TryParseGame(gameText) is { } parsed) chunk.Add(parsed);
            if (chunk.Count < chunkSize) continue;
            await foreach (var novel in FilterNovelAsync(chunk, reader, ct)) yield return novel;
            chunk.Clear();
        }
        await foreach (var novel in FilterNovelAsync(chunk, reader, ct)) yield return novel;
    }

    internal static async IAsyncEnumerable<ParsedGame> FilterNovelAsync(
        List<ParsedGame> chunk, ISubstrateReader? reader, [EnumeratorCancellation] CancellationToken ct)
    {
        if (chunk.Count == 0) yield break;
        if (reader is null) { foreach (var g in chunk) yield return g; yield break; }

        var toProbe = new List<int>(chunk.Count);
        for (int i = 0; i < chunk.Count; i++)
        {
            if (reader.IsProvenPresent(chunk[i].GameId)) continue;
            toProbe.Add(i);
        }

        bool[] present = new bool[chunk.Count];
        if (toProbe.Count > 0)
        {
            var ids = new Hash128[toProbe.Count];
            for (int k = 0; k < toProbe.Count; k++) ids[k] = chunk[toProbe[k]].GameId;
            byte[] bm = await reader.EntitiesExistBitmapAsync(ids, ct).ConfigureAwait(false);
            long bits = (long)bm.Length * 8;
            var proven = new List<Hash128>(toProbe.Count);
            for (int k = 0; k < toProbe.Count; k++)
            {
                if (k >= bits || (bm[k >> 3] & (1 << (k & 7))) == 0) continue;
                present[toProbe[k]] = true;
                proven.Add(ids[k]);
            }
            if (proven.Count > 0) reader.MarkProven(proven);
        }

        for (int i = 0; i < chunk.Count; i++)
            if (!present[i] && !reader.IsProvenPresent(chunk[i].GameId))
                yield return chunk[i];
    }

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

    internal static ParsedGame? TryParseGame(string gameText)
    {
        var gameBytes = Encoding.UTF8.GetBytes(gameText);
        PgnMovetext.PgnWalkResult walk;
        using (var ast = GrammarDecomposer.Parse(gameBytes, "pgn"))
            walk = PgnMovetext.Walk(ast, gameBytes);
        if (walk.Result is null || walk.Mainline.Count == 0) return null;

        var moves = walk.Mainline.Select(p => p.San).ToList();
        var result = walk.Result.Value;
        var (whiteName, blackName) = ParseNames(gameText);
        string date = PgnGames.TagStr(gameText, "Date");
        var gameId = ChessVocabulary.GameId(whiteName, blackName, date, moves);
        return new ParsedGame(gameText, walk, moves, result, gameId);
    }

    private static void ComposeParsedGame(ParsedGame parsed, SubstrateChangeBuilder b, ChessModality modality)
    {
        var (gameText, walk, moves, result, gameId) = parsed;

        var (whiteElo, blackElo) = ParseElos(gameText);
        var (whiteName, blackName) = ParseNames(gameText);
        var whitePlayer = EmitPlayer(b, whiteName);
        var blackPlayer = EmitPlayer(b, blackName);

        EmitGame(b, gameId, gameText, result, whitePlayer, blackPlayer, whiteElo, blackElo);

        var (initial, standardStart) = InitialState(gameText, modality);
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

        EmitGameOpeningMeta(b, gameId, gameText, moves, modality, standardStart);

        AppendGame(b, modality, initial, walk.Mainline, result,
                   whiteElo, blackElo, whitePlayer, blackPlayer, clocks, medianDrop, gameId,
                   evals, clockTokens, evalTokens);

        if (string.Equals(Environment.GetEnvironmentVariable("LAPLACE_CHESS_VARIATIONS"), "1", StringComparison.Ordinal))
            AppendVariations(b, modality, initial, walk.AllPlies, whitePlayer, blackPlayer);
    }

    // A PGN game defaults to the standard starting array, but chess.com/lichess puzzle exports
    // and some OTB archives specify a real, different starting position via [SetUp "1"]/[FEN].
    // Previously this was ignored entirely — every game was decomposed as if [FEN] didn't exist,
    // silently misattributing whatever moves followed to the wrong starting position.
    internal static (ChessState Initial, bool StandardStart) InitialState(string gameText, ChessModality m)
    {
        if (PgnGames.TagStr(gameText, "SetUp") != "1") return (m.Initial(), true);
        string fen = PgnGames.TagStr(gameText, "FEN");
        if (string.IsNullOrWhiteSpace(fen)) return (m.Initial(), true);
        try { return (m.FromFen(fen), false); }
        catch (FormatException) { return (m.Initial(), true); }
    }

    private static SubstrateChangeBuilder NewBuilder(IDecomposerContext ctx)
        => new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "chess/pgn");

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



    private const double PgnWitnessWeight = 0.7;

    private static void EmitGame(
    SubstrateChangeBuilder b, Hash128 gameId, string gameText, GameOutcome result,
    Hash128? whitePlayer, Hash128? blackPlayer, int whiteElo, int blackElo)
    {
        var src = ChessVocabulary.PgnSourceId;
        b.AddEntity(gameId, EntityTier.Document, ChessVocabulary.GameType, src);

        if (whitePlayer is { } wp) b.AddAttestation(NativeAttestation.Categorical(gameId, "HAS_WHITE", wp, src, null, PgnWitnessWeight));
        if (blackPlayer is { } bp) b.AddAttestation(NativeAttestation.Categorical(gameId, "HAS_BLACK", bp, src, null, PgnWitnessWeight));

        Meta(b, gameId, "HAS_EVENT", PgnGames.TagStr(gameText, "Event"), src);
        Meta(b, gameId, "ON_DATE", PgnGames.TagStr(gameText, "Date"), src);
        Meta(b, gameId, "HAS_ECO", PgnGames.TagStr(gameText, "ECO"), src);
        Meta(b, gameId, "HAS_TERMINATION", PgnGames.TagStr(gameText, "Termination"), src);
        Meta(b, gameId, "HAS_RESULT", result.IsDraw ? "1/2-1/2" : result.Winner == 0 ? "1-0" : "0-1", src);

        string tc = PgnGames.TagStr(gameText, "TimeControl");
        Meta(b, gameId, "HAS_TIME_CONTROL", tc, src);
        Meta(b, gameId, "HAS_TC_CLASS", TcClass(tc), src);

        if (whitePlayer is { } wp2 && whiteElo > 0) Rating(b, wp2, whiteElo, gameId, src);
        if (blackPlayer is { } bp2 && blackElo > 0) Rating(b, bp2, blackElo, gameId, src);
    }

    private static void EmitGameOpeningMeta(
    SubstrateChangeBuilder b, Hash128 gameId, string gameText,
    IReadOnlyList<string> sans, ChessModality modality, bool standardStart)
    {
        var src = ChessVocabulary.PgnSourceId;
        string ecoHeader = ChessCanonical.Eco(PgnGames.TagStr(gameText, "ECO")) ?? "";
        if (ecoHeader.Length > 0)
            ChessGraph.AppendGameMeta(b, gameId, "GAME_HAS_ECO", ecoHeader, PgnWitnessWeight, src);

        string openingHeader = ChessCanonical.OpeningName(PgnGames.TagStr(gameText, "Opening")) ?? "";
        if (openingHeader.Length > 0)
            ChessGraph.AppendGameMeta(b, gameId, "GAME_HAS_OPENING", openingHeader, PgnWitnessWeight, src);

        // ECO/opening-book classification and motif detection both match against SAN sequences
        // recorded from the standard starting array — meaningless (and a real false-match risk)
        // for a game that actually started from a different position via [SetUp "1"]/[FEN].
        if (!standardStart) return;

        var classified = OpeningClassifier.Classify(sans, modality);
        if (classified.Eco is { } eco && eco != ecoHeader)
            ChessGraph.AppendGameMeta(b, gameId, "GAME_HAS_ECO", eco, PgnWitnessWeight, src);
        if (classified.Name is { } name && name != openingHeader)
            ChessGraph.AppendGameMeta(b, gameId, "GAME_HAS_OPENING", name, PgnWitnessWeight, src);

        if (ChessMotifs.DetectNamedTrap(sans) is { } motif)
            ChessGraph.AppendGameMeta(b, gameId, "GAME_HAS_MOTIF", motif, PgnWitnessWeight, src);
    }

    private static void Meta(SubstrateChangeBuilder b, Hash128 game, string rel, string value, Hash128 src)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "?" || value == "-" || value == "????.??.??") return;
        if (ContentEmitter.Emit(b, value, src) is { } vid)
            b.AddAttestation(NativeAttestation.Categorical(game, rel, vid, src, null, PgnWitnessWeight));
    }

    private static void Rating(SubstrateChangeBuilder b, Hash128 player, int elo, Hash128 game, Hash128 src)
    {
        if (ContentEmitter.Emit(b, elo.ToString(), src) is { } rid)
            b.AddAttestation(NativeAttestation.Categorical(player, "HAS_RATING", rid, src, game, PgnWitnessWeight));
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

    private static void AppendGame(
        SubstrateChangeBuilder b, ChessModality m, ChessState initial, IReadOnlyList<PgnMovetext.PgnMoveStream> plies,
        GameOutcome result, int whiteElo, int blackElo, Hash128? whitePlayer, Hash128? blackPlayer,
        double[] clocks, double medianDrop, Hash128 gameId, int[]? evals,
        string[]? clockTokens, string[]? evalTokens)
    {
        bool mate = plies.Count > 0 && plies[^1].San.IndexOf('#') >= 0;
        int? winner = result.IsDraw ? null : result.Winner;
        const long evalGames = 2;
        const double evalWeight = 0.55;
        const double metaWeight = PgnWitnessWeight;

        var state = initial;
        for (int ply = 0; ply < plies.Count; ply++)
        {
            var plyStream = plies[ply];
            var mv = San.Resolve(state.Board, m.LegalActions(state), plyStream.San);
            if (mv is null) return;
            int mover = m.SideToMove(state);
            var next = m.Apply(state, mv.Value);
            string fromKey = m.StateKey(state);

            // Witness count stays a flat, ELO-independent "1 occurrence" — a player's Elo is a
            // separate, timestamped fact (Rating() below emits it as its own HAS_RATING
            // attestation, context-scoped to this game), not a multiplier smuggled into the
            // Glicko-2 fold's witness dimension. Elo and Glicko-2 are different rating systems;
            // conflating a raw Elo number into "how many games this counts as" was never a
            // meaningful unit conversion.
            long games = 1;
            if (mate && winner == mover) games += 1;
            // Think-time (tf) stays out of the witness count too, same reasoning as Elo above —
            // it's independently attested via HAS_CLOCK/HAS_THINK_CLASS below, not folded into
            // Glicko-2 evidence weight. moveChoiceGames is omitted so AppendMoveEdge defaults it
            // to `games`.
            double tf = PgnClocks.ThinkFactor(clocks, medianDrop, ply);

            ChessGraph.AppendMoveEdge(
                b, fromKey, m.StateKey(next), result.ForMover(mover), games, PgnWitnessWeight,
                sourceId: ChessVocabulary.PgnSourceId,
                moverPlayerId: mover == 0 ? whitePlayer : blackPlayer,
                contextId: gameId,
                ply: ply + 1);

            foreach (var tag in ChessMotifs.DetectAtPly(state.Board, mv.Value, next.Board))
                ChessGraph.AppendGameMeta(b, gameId, "GAME_HAS_MOTIF", tag, PgnWitnessWeight, ChessVocabulary.PgnSourceId);

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

    private static void AppendVariations(
    SubstrateChangeBuilder b, ChessModality m, ChessState initial, IReadOnlyList<PgnMovetext.PgnMoveStream> allPlies,
    Hash128? whitePlayer, Hash128? blackPlayer)
    {
        const double varWeight = 0.35;
        var mainState = initial;
        var varState = initial;
        bool inVar = false;

        foreach (var plyStream in allPlies)
        {
            if (plyStream.InVariation)
            {
                if (!inVar) { varState = CloneState(m, mainState); inVar = true; }
                if (TryPlay(m, ref varState, plyStream.San, out var fromKey, out var toKey, out int mover))
                {
                    ChessGraph.AppendMoveEdge(
                        b, fromKey, toKey, PlyOutcome.Draw, games: 1, varWeight,
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

    private static (int White, int Black) ParseElos(string game)
        => (PgnGames.TagInt(game, "WhiteElo"), PgnGames.TagInt(game, "BlackElo"));

    private static (string White, string Black) ParseNames(string game)
        => (PgnGames.TagStr(game, "White"), PgnGames.TagStr(game, "Black"));

    private static Hash128? EmitPlayer(SubstrateChangeBuilder b, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "?") return null;
        var canonicalId = ChessVocabulary.PlayerId(name);
        ChessVocabulary.EmitPlayer(b, canonicalId, name, ChessVocabulary.PgnSourceId);
        var legacyId = ChessVocabulary.LegacyPlayerId(name);
        if (legacyId != canonicalId)
            // CORRESPONDS_TO is the manifest's existing symmetric cross-naming-alignment relation
            // (rank=equivalence) — reused here instead of a chess-only "SAME_AS" so this gets a
            // real highway_mask bit instead of hashing to a relation type the manifest never saw.
            b.AddAttestation(NativeAttestation.Categorical(
                canonicalId, "CORRESPONDS_TO", legacyId, ChessVocabulary.PgnSourceId, null, PgnWitnessWeight));
        return canonicalId;
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        long games = 0;
        foreach (var f in EnumerateFiles(context.EcosystemPath))
        {
            try
            {

                using var r = new StreamReader(f);
                string? line;
                while ((line = r.ReadLine()) is not null)
                    if (line.StartsWith("[Event ", StringComparison.Ordinal)) games++;
            }
            catch { }
        }
        return Task.FromResult<long?>(games == 0 ? null : games);
    }




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
