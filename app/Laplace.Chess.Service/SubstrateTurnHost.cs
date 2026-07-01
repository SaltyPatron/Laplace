using global::Npgsql;
using NpgsqlTypes;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

public sealed class SubstrateTurnHost : IContentAddresser, IEdgeRatings, IStateValuer, ITurnLearner
{
    private readonly NpgsqlDataSource _ds;
    private readonly ConsensusAccumulatingWriter _writer;
    private readonly ISubstrateReader _reader;
    private readonly double _witnessWeight;
    private readonly SubstrateStateValuer _valuer;

    public SubstrateTurnHost(
        NpgsqlDataSource ds, ConsensusAccumulatingWriter writer, ISubstrateReader reader,
        double witnessWeight)
    {
        _ds = ds ?? throw new ArgumentNullException(nameof(ds));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _witnessWeight = witnessWeight;
        _valuer = new SubstrateStateValuer(ds);
    }





    public Hash128 Address(string canonicalSurface)
        => ChessCompose.PositionId(canonicalSurface);

    public async Task<double[]> EffMuAsync(IReadOnlyList<Hash128> edgeIds, CancellationToken ct = default)
    {
        var raw = new byte[edgeIds.Count][];
        for (int i = 0; i < edgeIds.Count; i++) raw[i] = edgeIds[i].ToBytes();

        var map = new Dictionary<Hash128, (double Mu, double W)>(edgeIds.Count);
        await using (var conn = await _ds.OpenConnectionAsync(ct))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT id, (rating - 2*rd)::double precision, witness_count::double precision " +
                "FROM laplace.consensus WHERE id = ANY($1)";
            cmd.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea,
                Value = raw,
            });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                map[Hash128.FromBytes((byte[])r[0])] = (r.GetDouble(1), r.GetDouble(2));
        }

        var outv = new double[edgeIds.Count];
        for (int i = 0; i < edgeIds.Count; i++)
            outv[i] = map.TryGetValue(edgeIds[i], out var v) ? Shrink(v.Mu, v.W) : GlickoPriors.UnratedEffMu;
        return outv;
    }





    private const double ShrinkK0 = 15000d;
    private static double Shrink(double effMu, double witness)
        => GlickoPriors.NeutralMu + (effMu - GlickoPriors.NeutralMu) * (witness / (witness + ShrinkK0));




    public Task<double[]> ValueStatesAsync(
        IReadOnlyList<string> stateSurfaces, CancellationToken ct = default)
        => _valuer.ValueStatesAsync(stateSurfaces, ct);




    private const long CheckmateGames = 3;

    public Task LearnGameAsync(IReadOnlyList<RecordedEdge> edges, CancellationToken ct = default)
    => LearnGameAsync(edges, adjudicated: false, ct);

    public async Task LearnGameAsync(
    IReadOnlyList<RecordedEdge> edges, bool adjudicated, CancellationToken ct = default)
    {
        if (edges.Count == 0) return;

        var b = new SubstrateChangeBuilder(ChessVocabulary.SourceId, "chess/selfplay/game");


        // SelfPlayTrustClass = "ResponseContent" — the player-name claim must carry the same low
        // trust as the rest of this self-play source, not the curated-corpus default.
        ChessVocabulary.EmitPlayer(
            b, ChessVocabulary.LaplacePlayerId, "Laplace", ChessVocabulary.SourceId, SourceTrust.Response);


        bool hasWin = false;
        foreach (var e in edges) if (e.MoverOutcome == PlyOutcome.Win) { hasWin = true; break; }
        bool checkmate = !adjudicated && hasWin;
        long games = checkmate ? CheckmateGames : 1;

        foreach (var e in edges)
        {
            var moverOutcome = adjudicated ? PlyOutcome.Draw : e.MoverOutcome;
            ChessGraph.AppendMoveEdge(b, e.SubjectKey, e.ObjectKey, moverOutcome, games, _witnessWeight,
                sourceId: ChessVocabulary.SourceId, moverPlayerId: ChessVocabulary.LaplacePlayerId);
        }

        var change = await b.BuildAsync(ct);
        await _writer.ApplyAsync(change, ct);
        await _writer.FoldIncrementalAsync(ct);
    }
}
