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
    public Hash128 SourceId     => ChessVocabulary.SourceId;
    public string  SourceName   => "ChessPgn";
    public int     LayerOrder   => 20;
    public Hash128 TrustClassId => ChessVocabulary.SourceId;

    private const int GamesPerBatch = 64;

    public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => ChessVocabulary.BootstrapAsync(context.Writer, ct);

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
                AppendGame(builder, modality, moves, result.Value, whiteElo, blackElo);

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
        => new SubstrateChangeBuilder(ChessVocabulary.SourceId, "chess/pgn").EnableDeferredContent(ctx.Reader);

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
    private static void AppendGame(
        SubstrateChangeBuilder b, ChessModality m, List<string> sans, GameOutcome result,
        int whiteElo, int blackElo)
    {
        bool mate = sans.Count > 0 && sans[^1].IndexOf('#') >= 0; // '#' = proven checkmate
        int? winner = result.IsDraw ? null : result.Winner;

        var state = m.Initial();
        foreach (var san in sans)
        {
            var mv = San.Resolve(state.Board, m.LegalActions(state), san);
            if (mv is null) return; // malformed/illegal token → skip the rest of this game
            int mover = m.SideToMove(state);
            var next = m.Apply(state, mv.Value);
            long games = EloGames(mover == 0 ? blackElo : whiteElo);
            if (mate && winner == mover) games += games / 2; // +50% for the confirmed-mating side
            ChessGraph.AppendMoveEdge(
                b, m.StateKey(state), m.StateKey(next), result.ForMover(mover), games, PgnWitnessWeight);
            state = next;
        }
    }

    /// <summary>Defender Elo → Glicko observation count (1..12): unknown → a neutral middle; rises with
    /// strength so master games dominate the fold and weak games barely move it.</summary>
    private static long EloGames(int elo)
        => elo <= 0 ? 3 : Math.Clamp((long)Math.Round((elo - 600) / 200.0), 1, 12);

    private static (int White, int Black) ParseElos(string game)
        => (TagInt(game, "WhiteElo"), TagInt(game, "BlackElo"));

    /// <summary>Read an integer PGN tag value (<c>[Tag "1234"]</c>) by a cheap scan; 0 if absent/blank.</summary>
    private static int TagInt(string game, string tag)
    {
        int i = game.IndexOf("[" + tag + " \"", StringComparison.Ordinal);
        if (i < 0) return 0;
        i += tag.Length + 3;
        int j = game.IndexOf('"', i);
        return j > i && int.TryParse(game.AsSpan(i, j - i), out var v) ? v : 0;
    }

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

    public IReadOnlyCollection<string> CanonicalNamesForReadback =>
        new[] { "substrate/source/ChessSelfPlay/v1" };

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
