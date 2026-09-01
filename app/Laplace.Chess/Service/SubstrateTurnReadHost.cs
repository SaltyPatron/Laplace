using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>
/// Read-only substrate adapter for chess inference.  This type deliberately has no
/// ConsensusAccumulatingWriter and does not implement ITurnLearner: scoring a legal
/// frontier, evaluating a position, exploring continuations, or reading the learned PST
/// cannot acquire the ingest/write spine by construction.
/// </summary>
public sealed class SubstrateTurnReadHost : IContentAddresser, IEdgeRatings, IStateValuer
{
    private readonly NpgsqlDataSource _ds;
    private readonly SubstrateStateValuer _valuer;

    public SubstrateTurnReadHost(NpgsqlDataSource ds)
    {
        _ds = ds ?? throw new ArgumentNullException(nameof(ds));
        _valuer = new SubstrateStateValuer(ds);
    }

    public Hash128 Address(string canonicalSurface)
        => ChessCompose.PositionId(canonicalSurface);

    public async Task<double[]> EffMuAsync(
        IReadOnlyList<Hash128> edgeIds, CancellationToken ct = default)
    {
        var byId = await NpgsqlConsensusByIds.ReadAsync(
            _ds, edgeIds, ChessVocabulary.MoveType, ct).ConfigureAwait(false);

        var values = new double[edgeIds.Count];
        for (int i = 0; i < edgeIds.Count; i++)
            values[i] = byId.TryGetValue(edgeIds[i], out var row)
                ? ChessShrink.Apply(row.EffMu, row.Witnesses)
                : GlickoPriors.UnratedEffMu;
        return values;
    }

    public Task<double[]> ValueStatesAsync(
        IReadOnlyList<string> stateSurfaces, CancellationToken ct = default)
        => _valuer.ValueStatesAsync(stateSurfaces, ct);
}
