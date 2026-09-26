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
public sealed record ChessMoveComposed(ChessNode Move, IReadOnlyList<ChessNode> Fields);

public static class ChessCompose
{
    public const byte SubstructureTier = 1;
    public const byte PositionTier = 2;

    /// <summary>
    /// Tier between a single board and the full line (a segment of play). Also the tier of
    /// the Syzygy fact chunks ChessSyzygy composes.
    /// </summary>
    public const byte SegmentTier = 3;

    // Tier of a LINE: the whole game's content (start position plus ordered moves).
    public const byte LineTier = 4;

    /// <summary>
    /// Line identity: Merkle over the start position then the ordered move ids. Replayed boards
    /// are not constituents. Identical play is one line whatever its SAN/PGN spelling or source.
    /// </summary>
    public static Hash128 LineId(Hash128 startPositionId, ReadOnlySpan<Hash128> orderedMoveIds)
    {
        // A one-child composition is the child: a line with no moves is its start position.
        if (orderedMoveIds.IsEmpty) return startPositionId;
        Span<Hash128> constituents = orderedMoveIds.Length + 1 <= 256
            ? stackalloc Hash128[orderedMoveIds.Length + 1]
            : new Hash128[orderedMoveIds.Length + 1];
        constituents[0] = startPositionId;
        orderedMoveIds.CopyTo(constituents[1..]);
        return Hash128.Merkle(LineTier, constituents);
    }

    /// <summary>
    /// Resolved-move content id: piece × from × to × flags × promotion, one entity across all
    /// games. With a position it forms the <see cref="TransitionKey"/>.
    /// </summary>
    public static Hash128 MoveId(Piece moving, ChessMove mv)
        => ChessPositionIdentity.MoveId(moving, mv);

    /// <summary>
    /// A move is a bounded reusable physical action, not a position and not testimony.
    /// Its transition is addressed separately by <see cref="TransitionKey"/>.
    /// </summary>
    public static ChessMoveComposed Move(Piece moving, ChessMove move)
    {
        EnsureLoaded();
        Span<ChessPositionIdentity.Atom> atoms = stackalloc ChessPositionIdentity.Atom[5];
        int count = ChessPositionIdentity.FillMoveAtoms(moving, move, atoms);
        var fields = new ChessNode[count];
        var ids = new Hash128[count];
        var coords = new double[count * 4];
        for (int i = 0; i < count; i++)
        {
            var node = AtomMemo.GetOrAdd(atoms[i], ComposeAtom);
            fields[i] = node;
            ids[i] = node.Id;
            node.Coord.CopyTo(coords, i * 4);
        }
        return new ChessMoveComposed(ComposeOver(ids, coords, count, PositionTier), fields);
    }

    /// <summary>Typed sentinel for a missing value in an ordinal-aligned annotation lane.</summary>
    internal static ChessNode AnnotationMissing()
    {
        EnsureLoaded();
        return AtomMemo.GetOrAdd(
            ChessPositionIdentity.Atom.Scalar(ChessPositionIdentity.AnnotationMissingDomain, 0),
            ComposeAtom);
    }

    /// <summary>
    /// Domain separator for the transition key, not a containment tier, although it equals
    /// <see cref="SegmentTier"/>. It is a separate constant because every persisted
    /// ChessTransitionFloor key is hashed under it; changing SegmentTier must not change them.
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
    /// Position composition from a canonical interchange surface. Geometry for ids in the
    /// <see cref="ChessPositionFloor"/> perfcache comes from that ROM; nothing is memoized by string.
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
        Span<ChessPositionIdentity.Atom> atoms =
            stackalloc ChessPositionIdentity.Atom[ChessPositionIdentity.MaxAtoms];
        int count = ChessPositionIdentity.FillAtoms(
            board, rules ?? ChessVariantRules.Standard, atoms);
        var subs = new ChessNode[count];
        var ids = new Hash128[count];
        for (int i = 0; i < count; i++)
        {
            ChessNode node = AtomMemo.GetOrAdd(atoms[i], ComposeAtom);
            subs[i] = node;
            ids[i] = node.Id;
        }

        Hash128 id = Hash128.Merkle(PositionTier, ids);
        if (ChessPositionFloor.TryLookup(id, out var x, out var y, out var z, out var m,
                out var hb, out var n, out var tier))
        {
            return new ChessComposed(
                new ChessNode(id, [x, y, z, m], hb, Trajectory.Build(ids),
                    PhysicalityId.Compute(id, PhysicalityType.Content),
                    n == 0 ? count : checked((int)n), tier == 0 ? PositionTier : tier), subs);
        }
        // Child coordinates are gathered only on a perfcache miss; identity and trajectory
        // are not recomputed.
        var childCoords = new double[count * 4];
        for (int i = 0; i < count; i++) subs[i].Coord.CopyTo(childCoords, i * 4);
        return new ChessComposed(ComposeMissing(id, ids, childCoords, count, PositionTier), subs);
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

    /// <summary>
    /// The composed node for one piece-square constituent: the same atom, through the same
    /// memo, that <see cref="Position(Board)"/> composes for that piece on that square. (A
    /// position's Substructures[0] is the side-to-move header atom, not a piece-square.)
    /// </summary>
    public static ChessNode PieceSquareNode(Piece piece, int file, int rank)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)file, 7u, nameof(file));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)rank, 7u, nameof(rank));
        EnsureLoaded();
        // Same packing as FillAtoms: ordinal in the high bits, (rank<<3)|file square bit below.
        ushort packed = (ushort)((ChessPositionIdentity.PieceOrdinal(piece) << 6) | (rank << 3) | file);
        return AtomMemo.GetOrAdd(
            ChessPositionIdentity.Atom.Scalar(ChessPositionIdentity.PieceSquareDomain, packed),
            ComposeAtom);
    }

    private static ChessNode ComposeAtom(ChessPositionIdentity.Atom atom)
    {
        Span<byte> bytes = stackalloc byte[33];
        int count = ChessPositionIdentity.FillAtomBytes(atom, bytes);
        var ids = new Hash128[count];
        for (int i = 0; i < count; i++)
        {
            byte value = bytes[i];
            ids[i] = ByteAtoms.Id(value);
        }
        Hash128 id = Hash128.Merkle(SubstructureTier, ids);
        if (ChessPositionFloor.TryLookup(id, out var x, out var y, out var z, out var m,
                out var hb, out var n, out var tier))
            return new ChessNode(id, [x, y, z, m], hb, Trajectory.Build(ids),
                PhysicalityId.Compute(id, PhysicalityType.Content),
                n == 0 ? count : checked((int)n), tier == 0 ? SubstructureTier : tier);
        var coords = new double[count * 4];
        for (int i = 0; i < count; i++) ByteAtoms.Coord(bytes[i]).CopyTo(coords.AsSpan(i * 4, 4));
        return ComposeMissing(id, ids, coords, count, SubstructureTier);
    }

    private static ChessNode ComposeOver(Hash128[] childIds, double[] childCoords, int n, byte tier)
    {
        Hash128 id = Hash128.Merkle(tier, childIds);
        double[] traj = Trajectory.Build(childIds);
        Hash128 physId = PhysicalityId.Compute(id, PhysicalityType.Content);

        // Perfcache hit: the ROM geometry is used as is.
        if (ChessPositionFloor.TryLookup(id, out var x, out var y, out var z, out var m,
                out var hb, out var nFloor, out var tierFloor))
        {
            return new ChessNode(id, new[] { x, y, z, m }, hb, traj, physId,
                nFloor != 0 ? (int)nFloor : n, tierFloor != 0 ? tierFloor : tier);
        }

        return ComposeMissing(id, childIds, childCoords, n, tier, traj, physId);
    }

    private static ChessNode ComposeMissing(Hash128 id, Hash128[] childIds, double[] childCoords,
        int n, byte tier, double[]? trajectory = null, Hash128? physicalityId = null)
    {
        double[] traj = trajectory ?? Trajectory.Build(childIds);
        Hash128 physId = physicalityId ?? PhysicalityId.Compute(id, PhysicalityType.Content);
        // Geometry for a perfcache miss is the intrinsic (Karcher) mean of the children's
        // coordinates.
        double[] coord = Math4d.KarcherMean(childCoords);
        Hilbert128 hbEnc = Hilbert128.Encode(coord);
        return new ChessNode(id, coord, hbEnc, traj, physId, n, tier);
    }

    private static volatile bool _composeReady;
    private static readonly object ComposeReadyGate = new();

    /// <summary>Loads the position and transition perfcaches composition reads.</summary>
    public static void InitializePerfcaches() => EnsureLoaded();

    /// <summary>
    /// Loads both perfcaches once. The volatile flag is the fast path; the lock with a re-check
    /// keeps concurrent compose workers on a cold start from mapping ChessTransitionFloor,
    /// which has no gate of its own, over the same static fields at once.
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
