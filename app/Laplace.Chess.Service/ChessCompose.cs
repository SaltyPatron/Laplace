using System.Collections.Concurrent;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

public readonly record struct ChessNode(
    Hash128    Id,
    double[]   Coord,
    Hilbert128 Hb,
    double[]   Trajectory,
    Hash128    PhysId,
    int        NConstituents,
    byte       Tier);

public sealed record ChessComposed(ChessNode Position, IReadOnlyList<ChessNode> Substructures);

public static class ChessCompose
{
    public const byte SubstructureTier = 1;
    public const byte PositionTier      = 2;

    public static object Gate => LaplaceCoreGate.Native;

    private static readonly ConcurrentDictionary<string, ChessNode> TokenMemo = new(StringComparer.Ordinal);

    private static readonly char[] Sep = { ' ' };

    public static ChessComposed Position(string surface)
    {
        EnsureLoaded();
        var tokens = surface.Split(Sep, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) throw new ArgumentException("empty position surface", nameof(surface));

        var subs   = new ChessNode[tokens.Length];
        var ids    = new Hash128[tokens.Length];
        var coords = new double[(long)tokens.Length * 4];
        for (int i = 0; i < tokens.Length; i++)
        {
            var s = TokenMemo.GetOrAdd(tokens[i], ComposeToken);
            subs[i] = s;
            ids[i]  = s.Id;
            coords[i * 4 + 0] = s.Coord[0]; coords[i * 4 + 1] = s.Coord[1];
            coords[i * 4 + 2] = s.Coord[2]; coords[i * 4 + 3] = s.Coord[3];
        }

        var pos = ComposeOver(ids, coords, tokens.Length, PositionTier);
        return new ChessComposed(pos, subs);
    }

    public static Hash128 PositionId(string surface)
    {
        EnsureLoaded();
        var tokens = surface.Split(Sep, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) throw new ArgumentException("empty position surface", nameof(surface));
        var ids = new Hash128[tokens.Length];
        for (int i = 0; i < tokens.Length; i++) ids[i] = TokenMemo.GetOrAdd(tokens[i], ComposeToken).Id;
        return Hash128.Merkle(PositionTier, ids);
    }

    private static ChessNode ComposeToken(string token)
    {
        var recs = CodepointPerfcache.Records;
        int n = 0;
        foreach (var _ in token.EnumerateRunes()) n++;
        if (n == 0) throw new ArgumentException("empty token", nameof(token));

        var ids    = new Hash128[n];
        var coords = new double[(long)n * 4];
        int i = 0;
        foreach (var rune in token.EnumerateRunes())
        {
            ref readonly var rec = ref recs[rune.Value];
            ids[i] = rec.Hash;
            coords[i * 4 + 0] = rec.CoordX; coords[i * 4 + 1] = rec.CoordY;
            coords[i * 4 + 2] = rec.CoordZ; coords[i * 4 + 3] = rec.CoordM;
            i++;
        }
        return ComposeOver(ids, coords, n, SubstructureTier);
    }

    private static ChessNode ComposeOver(Hash128[] childIds, double[] childCoords, int n, byte tier)
    {
        Hash128    id     = Hash128.Merkle(tier, childIds);
        double[]   coord  = Math4d.Centroid(childCoords);
        Hilbert128 hb     = Hilbert128.Encode(coord);
        double[]   traj   = Trajectory.Build(childIds);
        Hash128    physId = PhysicalityId.Compute(
            id, PhysicalityType.Content, coord[0], coord[1], coord[2], coord[3], traj);
        return new ChessNode(id, coord, hb, traj, physId, n, tier);
    }

    private static void EnsureLoaded()
    {
        if (!CodepointPerfcache.IsLoaded) CodepointPerfcache.LoadDefault();
    }
}
