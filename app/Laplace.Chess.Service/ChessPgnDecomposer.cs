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
/// <c>MOVE</c> edges by the game result. The substrate fills with chess positions + rated moves — not
/// PGN-notation text — so these edges fold into the same graph self-play uses.
/// </summary>
public sealed class ChessPgnDecomposer : IDecomposer
{
    public Hash128 SourceId     => ChessVocabulary.SourceId;
    public string  SourceName   => "ChessPgn";
    public int     LayerOrder   => 20;
    public Hash128 TrustClassId => ChessVocabulary.SourceId;

    private const double Weight = 0.7;     // human game corpus
    private const int GamesPerBatch = 64;

    public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => ChessVocabulary.BootstrapAsync(context.Writer, ct);

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var modality = new ChessModality();
        foreach (var file in EnumerateFiles(context.EcosystemPath))
        {
            ct.ThrowIfCancellationRequested();
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, ct); } catch { continue; }
            if (bytes.Length == 0) continue;

            using var ast = GrammarDecomposer.Parse(bytes, "pgn");
            var builder = NewBuilder(context);
            int inBatch = 0;

            foreach (var (moves, result) in EnumerateGames(ast, bytes))
            {
                ct.ThrowIfCancellationRequested();
                AppendGame(builder, modality, moves, result);
                if (++inBatch >= GamesPerBatch)
                {
                    if (!options.DryRun) yield return await builder.SetInputUnitsConsumed(inBatch).BuildAsync(ct);
                    builder = NewBuilder(context);
                    inBatch = 0;
                }
            }
            if (inBatch > 0 && !options.DryRun)
                yield return await builder.SetInputUnitsConsumed(inBatch).BuildAsync(ct);
        }
    }

    private static SubstrateChangeBuilder NewBuilder(IDecomposerContext ctx)
        => new SubstrateChangeBuilder(ChessVocabulary.SourceId, "chess/pgn").EnableDeferredContent(ctx.Reader);

    /// <summary>Replay one game's SAN moves, emitting MOVE edges scored by result. Aborts on an unresolved move.</summary>
    private static void AppendGame(
        SubstrateChangeBuilder b, ChessModality m, List<string> sans, GameOutcome? result)
    {
        if (result is null || sans.Count == 0) return;
        var state = m.Initial();
        foreach (var san in sans)
        {
            var mv = San.Resolve(state.Board, m.LegalActions(state), san);
            if (mv is null) return; // malformed/illegal token → skip the rest of this game
            int mover = m.SideToMove(state);
            var next = m.Apply(state, mv.Value);
            ChessGraph.AppendMoveEdge(b, m.StateKey(state), m.StateKey(next), result.Value.ForMover(mover), Weight);
            state = next;
        }
    }

    /// <summary>Walk the parse tree (document order), yielding each game's ordered mainline SAN + result.</summary>
    private static IEnumerable<(List<string> Moves, GameOutcome? Result)> EnumerateGames(GrammarAst ast, byte[] utf8)
    {
        var moves = new List<string>();
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
                yield return (moves, ParseResult(Text(utf8, node)));
                moves = new List<string>();
            }
        }
        if (moves.Count > 0) yield return (moves, null);
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
                var t = File.ReadAllText(f);
                int i = 0;
                while ((i = t.IndexOf("[Event ", i, StringComparison.Ordinal)) >= 0) { games++; i += 7; }
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
