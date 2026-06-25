using global::Npgsql;
using NpgsqlTypes;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>
/// Bridges the generic <see cref="ModalityEngine{TState,TAction}"/> to the Laplace substrate:
/// <list type="bullet">
/// <item><see cref="IContentAddresser"/> — composes a position's canonical surface to its content id
///   (<c>ContentEmitter.RootId</c>); the engine never mints ids itself.</item>
/// <item><see cref="IEdgeRatings"/> — reads <c>eff_mu</c> of candidate MOVE edges from
///   <c>laplace.consensus</c>.</item>
/// <item><see cref="ITurnLearner"/> — composes the visited positions as content entities, writes one
///   <c>MOVE</c> attestation per ply whose <i>score is the game result</i>, then folds the touched
///   consensus edges in place (online, no drain).</item>
/// </list>
/// </summary>
public sealed class SubstrateTurnHost : IContentAddresser, IEdgeRatings, ITurnLearner
{
    private readonly NpgsqlDataSource _ds;
    private readonly ConsensusAccumulatingWriter _writer;
    private readonly ISubstrateReader _reader;
    private readonly double _witnessWeight;

    public SubstrateTurnHost(
        NpgsqlDataSource ds, ConsensusAccumulatingWriter writer, ISubstrateReader reader,
        double witnessWeight)
    {
        _ds = ds ?? throw new ArgumentNullException(nameof(ds));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _witnessWeight = witnessWeight;
    }

    // IContentAddresser: a position id is the Merkle root of composing its canonical surface — NOT an
    // OfCanonical blind hash. RootId computes the id without staging (used for rating lookups);
    // LearnGameAsync stages the same tree via ContentEmitter.Emit, which yields the identical root.
    public Hash128 Address(string canonicalSurface)
        => ContentEmitter.RootId(canonicalSurface)
           ?? throw new InvalidOperationException($"content address is empty for '{canonicalSurface}'");

    public async Task<double[]> EffMuAsync(IReadOnlyList<Hash128> edgeIds, CancellationToken ct = default)
    {
        var raw = new byte[edgeIds.Count][];
        for (int i = 0; i < edgeIds.Count; i++) raw[i] = edgeIds[i].ToBytes();

        var map = new Dictionary<Hash128, double>(edgeIds.Count);
        await using (var conn = await _ds.OpenConnectionAsync(ct))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT id, (rating - 2*rd)::double precision FROM laplace.consensus WHERE id = ANY($1)";
            cmd.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea,
                Value = raw,
            });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                map[Hash128.FromBytes((byte[])r[0])] = r.GetDouble(1);
        }

        var outv = new double[edgeIds.Count];
        for (int i = 0; i < edgeIds.Count; i++)
            outv[i] = map.TryGetValue(edgeIds[i], out var v) ? v : GlickoPriors.UnratedEffMu;
        return outv;
    }

    public async Task LearnGameAsync(IReadOnlyList<RecordedEdge> edges, CancellationToken ct = default)
    {
        if (edges.Count == 0) return;

        var b = new SubstrateChangeBuilder(ChessVocabulary.SourceId, "chess/selfplay/game")
            .EnableDeferredContent(_reader);

        foreach (var e in edges)
        {
            var subj = CategoryAnchor.Emit(
                b, e.SubjectKey, ChessVocabulary.PositionType, ChessVocabulary.SourceId, ChessVocabulary.Trust);
            var obj = CategoryAnchor.Emit(
                b, e.ObjectKey, ChessVocabulary.PositionType, ChessVocabulary.SourceId, ChessVocabulary.Trust);
            if (subj is null || obj is null) continue;

            long sumScoreFp1e9 = e.MoverOutcome switch
            {
                PlyOutcome.Win  => 1_000_000_000L,
                PlyOutcome.Draw =>   500_000_000L,
                _               =>             0L,
            };
            b.AddAttestation(NativeAttestation.Aggregated(
                subject: subj.Value,
                typeId: ChessVocabulary.MoveType,
                obj: obj.Value,
                sourceId: ChessVocabulary.SourceId,
                contextId: null,
                games: 1,
                sumScoreFp1e9: sumScoreFp1e9,
                witnessWeight: _witnessWeight));
        }

        var change = await b.BuildAsync(ct);
        await _writer.ApplyAsync(change, ct);
        await _writer.FoldIncrementalAsync(ct);
    }
}
