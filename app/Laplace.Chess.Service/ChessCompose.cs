using System.Collections.Concurrent;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

/// <summary>One composed content node: id, S³ coord (centroid of children — falls inward off the
/// surface, so radius = compositional depth), Hilbert index, the lossless constituent trajectory, the
/// physicality id, child count, and tier.</summary>
public readonly record struct ChessNode(
    Hash128    Id,
    double[]   Coord,
    Hilbert128 Hb,
    double[]   Trajectory,
    Hash128    PhysId,
    int        NConstituents,
    byte       Tier);

/// <summary>A composed position plus the substructure nodes it folds from (the evidence carriers).</summary>
public sealed record ChessComposed(ChessNode Position, IReadOnlyList<ChessNode> Substructures);

/// <summary>
/// Composes a chess position from its bounded SUBSTRUCTURE tokens — directly, via the native
/// merkle+centroid primitive (the same one <see cref="Laplace.Decomposers.Abstractions.NgramTrajectory"/>
/// uses), <b>never</b> through the universal TEXT decomposer. A position's canonical surface
/// (<c>PositionContent.Surface</c>) already <i>is</i> a space-separated token sequence
/// (<c>stm:w cr:KQkq ep:- Pe2 Nf3 … wpawns:… mat:…</c>); we split it and compose each token from its
/// codepoints as one tier-1 node, then compose the position as a tier-2 node over those substructures.
///
/// <para>This is the lookup-table + flat-composition fix: evidence (OUTCOME) accrues at the shared
/// substructure nodes, so a novel position inherits value from the substructures it shares with seen
/// positions. Routing the ~150-char surface through the text composer instead exploded it into hundreds
/// of grapheme nodes per position (the row blow-up + the native heap race) and keyed all evidence on one
/// whole-position root (the lookup table). A board is not prose.</para>
///
/// <para>The bounded base tokens (768 piece-square atoms, the side/castle/ep/material/pawn tokens) recur
/// across nearly every position, so their geometry is memoized once and thereafter looked up — the
/// in-memory "chess perfcache". (The offline <c>laplace_chess_perfcache.bin</c> + loader + named-opening
/// replay are a serialization layer over this same compute; not required for correctness here.)</para>
/// </summary>
public static class ChessCompose
{
    /// <summary>Substructure tokens sit one tier above codepoints; the position one above those.</summary>
    public const byte SubstructureTier = 1;
    public const byte PositionTier      = 2;

    // token -> composed substructure node (bounded base recurs → computed once, looked up forever).
    private static readonly ConcurrentDictionary<string, ChessNode> TokenMemo = new(StringComparer.Ordinal);

    private static readonly char[] Sep = { ' ' };

    /// <summary>Compose the position and every substructure it folds from.</summary>
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

    /// <summary>The position id only — the light path for rating lookups (<c>SubstrateTurnHost.Address</c>).
    /// Identical id to <see cref="Position"/>, so ingest and recall agree.</summary>
    public static Hash128 PositionId(string surface)
    {
        EnsureLoaded();
        var tokens = surface.Split(Sep, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) throw new ArgumentException("empty position surface", nameof(surface));
        var ids = new Hash128[tokens.Length];
        for (int i = 0; i < tokens.Length; i++) ids[i] = TokenMemo.GetOrAdd(tokens[i], ComposeToken).Id;
        return Hash128.Merkle(PositionTier, ids);
    }

    // Compose one substructure token directly from its codepoints (bypasses UAX29 word-break, which
    // would split e.g. "Pe2" into "Pe"+"2"). All chess tokens are ASCII; each rune is one codepoint.
    private static ChessNode ComposeToken(string token)
    {
        var recs = CodepointPerfcache.Records;
        // Codepoint count (runes); chess tokens are short ASCII so this is cheap.
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

    // The shared compose: merkle id over children, centroid coord, hilbert, lossless constituent
    // trajectory, physicality id. Mirrors NgramTrajectory.Compose exactly.
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
