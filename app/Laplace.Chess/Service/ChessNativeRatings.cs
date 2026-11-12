using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>
/// The turn engine's view of the Native substrate for chess: a position is the Engine's position id
/// (<see cref="ChessEngineIdentity"/>), a move played from it is the [position, move, position] claim a
/// recorded game attests, and that claim's rating is its Native standing (<c>public.laplace_standings</c>),
/// shrunk toward the neutral prior by the games folded into it. A claim no game attested is unrated.
/// </summary>
public sealed class ChessNativeRatings(NpgsqlDataSource ds) : IContentAddresser, IEdgeRatings
{
    private readonly NpgsqlDataSource _ds = ds ?? throw new ArgumentNullException(nameof(ds));

    /// <summary>The Engine's id of a position surface; zero for rules its identity does not cover.</summary>
    public Hash128 Address(string canonicalSurface)
    {
        if (!PositionContent.TryFenFromSurface(canonicalSurface, out var fen)) return Hash128.Zero;
        return ChessEngineIdentity.TryPositionId(Board.FromFen(fen), out var id) ? id : Hash128.Zero;
    }

    /// <summary>The claim a move is: [position, move, position], or zero where the rules have no Engine identity.</summary>
    public static Hash128 Claim(ChessState state, ChessMove move, ChessState next)
        => ChessEngineIdentity.TryPositionId(state.Board, out var before)
           && ChessEngineIdentity.TryPositionId(next.Board, out var after)
            ? ChessEngineIdentity.TransitionId(before, ChessEngineIdentity.MoveId(state.Board, move), after)
            : Hash128.Zero;

    public async Task<double[]> EffMuAsync(IReadOnlyList<Hash128> edgeIds, CancellationToken ct = default)
    {
        var claims = edgeIds.Where(static id => id != Hash128.Zero).Distinct().ToArray();
        var standings = await NpgsqlChessTransitionStandings.ReadAsync(_ds, claims, ct).ConfigureAwait(false);
        var effMu = new double[edgeIds.Count];
        for (int i = 0; i < edgeIds.Count; i++)
            effMu[i] = standings.TryGetValue(edgeIds[i], out var row)
                ? ChessShrink.Apply(row.Confidence * 1e9, row.Matches)
                : GlickoPriors.UnratedEffMu;
        return effMu;
    }
}
