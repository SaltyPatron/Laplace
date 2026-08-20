using System.Collections.Concurrent;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

public readonly record struct ChessNode(
    Hash128 Id,
    double[] Coord,
    Hilbert128 Hb,
    double[] Trajectory,
    Hash128 PhysId,
    int NConstituents,
    byte Tier);

public sealed record ChessComposed(ChessNode Position, IReadOnlyList<ChessNode> Substructures);

public static class ChessCompose
{
    public const byte SubstructureTier = 1;
    public const byte PositionTier = 2;

    /// <summary>
    /// Intermediate containment between a single board and the full line (phase / motif
    /// window / opening segment — invent the order; do not leave this address empty by
    /// parking the line at Document). Until that order is defined and emitted, callers
    /// must not invent a fake rung. See W14: skip was shallow Document mapping, not law.
    /// </summary>
    public const byte SegmentTier = 3;

    // GH #736: the LINE — whole-game content. Lives at 4 today because game entities were
    // typed EntityTier.Document; that is not a reason SegmentTier stays vacant forever.
    public const byte LineTier = 4;

    /// <summary>
    /// The game/line CONTENT id (GH #736): the Merkle composition of the ordered position
    /// ids the game passes through (start position included) — the same composition law as
    /// every tier. Identical play = identical id, regardless of who played it, when, or how
    /// the source spelled the SAN ("O-O" vs "0-0"); provenance lives in attestation
    /// context, never in this hash. ONE definition — every lane (PGN, book, live) resolves
    /// line identity through it.
    /// </summary>
    public static Hash128 LineId(ReadOnlySpan<Hash128> orderedPositionIds)
        => Hash128.Merkle(LineTier, orderedPositionIds);

    /// <summary>
    /// Resolved-move content id (spec 11): piece × from × to × flags × promotion.
    /// Deduped across games; pairs with <see cref="TransitionKey"/> for state→state floor hits.
    /// </summary>
    public static Hash128 MoveId(Piece moving, ChessMove mv)
    {
        Span<byte> buf = stackalloc byte[8];
        buf[0] = (byte)moving;
        buf[1] = (byte)(mv.From & 0xFF);
        buf[2] = (byte)(mv.To & 0xFF);
        buf[3] = (byte)mv.Flags;
        buf[4] = (byte)mv.Promotion;
        buf[5] = 0; buf[6] = 0; buf[7] = 0;
        return Hash128.Blake3(buf);
    }

    /// <summary>
    /// Domain separator for the transition key — NOT a containment tier, despite sharing
    /// the value <see cref="SegmentTier"/> holds today. It is spelled separately because
    /// the two are free to diverge and must not drag each other: SegmentTier is a reserved
    /// rung waiting for a real phase/motif order to be defined, and moving it is expected
    /// work. If TransitionKey read that constant, defining SegmentTier would silently
    /// re-mint every transition key and invalidate every persisted ChessTransitionFloor
    /// blob — a content-addressed store answering for keys that no longer exist. The value
    /// is pinned here so today's blobs keep their identity and tomorrow's tier work is free.
    /// </summary>
    public const byte TransitionKeyDomain = 3;

    /// <summary>Lookup key for (from_position, move) → to_position transition floor.</summary>
    public static Hash128 TransitionKey(Hash128 fromPositionId, Hash128 moveId)
    {
        Span<Hash128> kids = stackalloc Hash128[2];
        kids[0] = fromPositionId;
        kids[1] = moveId;
        return Hash128.Merkle(TransitionKeyDomain, kids);
    }

    public static object Gate => LaplaceCoreGate.Native;

    private static readonly ConcurrentDictionary<ChessPositionIdentity.Atom, ChessNode> AtomMemo = new();

    /// <summary>
    /// Full position composition. Geometry for catalog ids uses native
    /// <see cref="ChessPositionFloor"/> when loaded (spec 33 / #822).
    /// No string→ChessComposed heap memo.
    /// </summary>
    public static ChessComposed Position(string surface)
    {
        if (!PositionContent.TryFenFromSurface(surface, out var fen))
            throw new ArgumentException("not a canonical standard-chess position interchange surface", nameof(surface));
        return Position(Board.FromFen(fen));
    }

    /// <summary>
    /// Compose a board directly from typed binary state atoms. No FEN/PGN/state-key string is
    /// admitted as content. The returned position physicality is the lossless ordered manifest
    /// of side, four castling-right bits, en-passant and occupied piece×square atoms.
    /// </summary>
    public static ChessComposed Position(Board board, ChessVariantRules? rules = null)
    {
        ArgumentNullException.ThrowIfNull(board);
        EnsureLoaded();
        Span<ChessPositionIdentity.Atom> atoms = stackalloc ChessPositionIdentity.Atom[40];
        int count = ChessPositionIdentity.FillAtoms(
            board, rules ?? ChessVariantRules.Standard, atoms);
        var subs = new ChessNode[count];
        var ids = new Hash128[count];
        var childCoords = new double[count * 4];
        for (int i = 0; i < count; i++)
        {
            ChessNode node = AtomMemo.GetOrAdd(atoms[i], ComposeAtom);
            subs[i] = node;
            ids[i] = node.Id;
            node.Coord.CopyTo(childCoords, i * 4);
        }

        Hash128 id = Hash128.Merkle(PositionTier, ids);
        double[] trajectory = Trajectory.Build(ids);
        Hash128 physicalityId = PhysicalityId.Compute(id, PhysicalityType.Content);
        if (ChessPositionFloor.TryLookup(id, out var x, out var y, out var z, out var m,
                out var hb, out var n, out var tier))
        {
            return new ChessComposed(
                new ChessNode(id, [x, y, z, m], hb, trajectory, physicalityId,
                    n == 0 ? count : checked((int)n), tier == 0 ? PositionTier : tier), subs);
        }
        return new ChessComposed(ComposeOver(ids, childCoords, count, PositionTier), subs);
    }

    public static Hash128 PositionId(string surface)
    {
        if (!PositionContent.TryFenFromSurface(surface, out var fen))
            throw new ArgumentException("not a canonical standard-chess position interchange surface", nameof(surface));
        return PositionId(Board.FromFen(fen));
    }

    /// <summary>
    /// Board → position id from typed state atoms, without materialising interchange text.
    /// </summary>
    public static Hash128 PositionId(Board board, ChessVariantRules? rules = null)
    {
        EnsureLoaded();
        return ChessPositionIdentity.PositionId(board, rules);
    }

    private static ChessNode ComposeAtom(ChessPositionIdentity.Atom atom)
    {
        Span<byte> bytes = stackalloc byte[33];
        int count = ChessPositionIdentity.FillAtomBytes(atom, bytes);
        var ids = new Hash128[count];
        var coords = new double[count * 4];
        for (int i = 0; i < count; i++)
        {
            byte value = bytes[i];
            ids[i] = ByteAtoms.Id(value);
            ByteAtoms.Coord(value).CopyTo(coords.AsSpan(i * 4, 4));
        }
        Hash128 id = Hash128.Merkle(SubstructureTier, ids);
        double[] trajectory = Trajectory.Build(ids);
        Hash128 physicalityId = PhysicalityId.Compute(id, PhysicalityType.Content);
        if (ChessPositionFloor.TryLookup(id, out var x, out var y, out var z, out var m,
                out var hb, out var n, out var tier))
            return new ChessNode(id, [x, y, z, m], hb, trajectory, physicalityId,
                n == 0 ? count : checked((int)n), tier == 0 ? SubstructureTier : tier);
        return ComposeOver(ids, coords, count, SubstructureTier);
    }

    private static ChessNode ComposeOver(Hash128[] childIds, double[] childCoords, int n, byte tier)
    {
        Hash128 id = Hash128.Merkle(tier, childIds);
        double[] traj = Trajectory.Build(childIds);
        Hash128 physId = PhysicalityId.Compute(id, PhysicalityType.Content);

        // Floor hit: deterministic lossless geometry already in the ROM — do not recompute.
        if (ChessPositionFloor.TryLookup(id, out var x, out var y, out var z, out var m,
                out var hb, out var nFloor, out var tierFloor))
        {
            return new ChessNode(id, new[] { x, y, z, m }, hb, traj, physId,
                nFloor != 0 ? (int)nFloor : n, tierFloor != 0 ? tierFloor : tier);
        }

        // Karcher, not Centroid — intrinsic mean, lands on S3 at norm 1. The floor
        // hit above returns ROM geometry untouched; only the computed branch moves.
        // Requires a reseed.
        double[] coord = Math4d.KarcherMean(childCoords);
        Hilbert128 hbEnc = Hilbert128.Encode(coord);
        return new ChessNode(id, coord, hbEnc, traj, physId, n, tier);
    }

    private static volatile bool _composeReady;
    private static readonly object ComposeReadyGate = new();

    /// <summary>
    /// One-time compose warmup. The <c>_composeReady</c> read is the fast path, but it
    /// only suppresses REPEAT work once someone has finished — it does not stop N compose
    /// workers entering together on a cold start and racing through Prime() and the two
    /// floor loads. ChessTransitionFloor carries no internal gate of its own, so that race
    /// is concurrent mmap setup over the same static fields, not merely duplicated effort.
    /// Lock and re-check inside.
    /// </summary>
    private static void EnsureLoaded()
    {
        if (_composeReady) return;
        lock (ComposeReadyGate)
        {
            if (_composeReady) return;
            ChessPositionFloor.LoadDefault();
            ChessTransitionFloor.LoadDefault();
            _composeReady = true;
        }
    }
}
