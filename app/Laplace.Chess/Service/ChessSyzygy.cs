using System.Runtime.InteropServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Chess.Service;

/// <summary>
/// A Syzygy tablebase verdict for one position, side-to-move POV.
/// <see cref="Wdl"/> is 0..4 (loss / blessed-loss / draw / cursed-win / win —
/// <see cref="SyzygyNative"/> order); <see cref="Dtz"/> is plies to the next zeroing
/// move under optimal play.
/// </summary>
public readonly record struct SyzygyVerdict(
    int Wdl, int Dtz, int From = -1, int To = -1, int Promotes = 0);

public readonly record struct SyzygyChunkRef(Hash128 Id, double[] Coord);

public readonly record struct SyzygyGraphNode(ChessNode Node, Hash128 TypeId);

public readonly record struct SyzygyPositionVerdict(Hash128 PositionId, int Wdl, int Dtz);

/// <summary>
/// One fully composed storage leaf. Preparing it once at extraction time discards the
/// thousands of FEN strings that produced it and keeps only the content-addressed graph
/// trajectory needed by apply.
/// </summary>
public sealed record SyzygyTransitionChunk(
    Hash128 Id, double[] Coord, double[] Trajectory, int NConstituents,
    IReadOnlyList<SyzygyGraphNode> Nodes,
    IReadOnlyList<SyzygyPositionVerdict> Verdicts)
{
    public SyzygyChunkRef Reference => new(Id, Coord);
}

/// <summary>Probe boundary: the native kernel in production, a fake in tests.</summary>
public interface ISyzygyProber
{
    /// <summary>Largest man count the loaded table set covers (0 = nothing loaded).</summary>
    int Largest { get; }

    /// <summary>
    /// Thread-safe WDL-only probe (Fathom <c>tb_probe_wdl</c>). Null when no table answers.
    /// Prefer this in the parallel unpack fan-out.
    /// </summary>
    int? ProbeWdl(Board board);

    /// <summary>WDL+DTZ root probe (process-locked — Fathom root is not thread-safe).
    /// Null when no table answers or the position is terminal.</summary>
    SyzygyVerdict? Probe(Board board);
}

/// <summary>The in-process native prober (Fathom kernel via <see cref="SyzygyNative"/>).</summary>
public sealed class SyzygyNativeProber : ISyzygyProber
{
    public int Largest => SyzygyNative.Largest();

    public int? ProbeWdl(Board board)
    {
        var bb = ChessSyzygy.ToBitboards(board);
        int wdl = SyzygyNative.ProbeWdl(
            bb.White, bb.Black, bb.Kings, bb.Queens, bb.Rooks,
            bb.Bishops, bb.Knights, bb.Pawns, bb.Ep, board.WhiteToMove);
        return wdl < 0 ? null : wdl;
    }

    public SyzygyVerdict? Probe(Board board)
    {
        var bb = ChessSyzygy.ToBitboards(board);
        return SyzygyNative.ProbeRootTransition(
                   bb.White, bb.Black, bb.Kings, bb.Queens, bb.Rooks,
                   bb.Bishops, bb.Knights, bb.Pawns, bb.Ep, board.WhiteToMove)
               is { } v
            ? new SyzygyVerdict(v.Wdl, v.Dtz, v.From, v.To, v.Promotes)
            : null;
    }
}

/// <summary>
/// Syzygy tablebase ingest. Fathom decodes package entries into exact optimal transitions;
/// the substrate stores them as content-addressed position → typed move → position graph
/// segments with WDL/DTZ on the vertices. The local mapped package is the decoding/search
/// perfcache, not the persisted knowledge model. The v2 single-position method remains for
/// already-recorded game evidence; material packages use the compact graph format.
/// </summary>
public static class ChessSyzygy
{
    public const int Version = 2;
    // v2 pins material roots to canonical placement-order chunks. v1 grouped parallel
    // probe completion order, so worker timing changed chunk boundaries and a re-run minted
    // a different graph behind the same material physicality identity.
    public const int MaterialGraphVersion = 2;

    public const string SourceName = "ChessSyzygy";
    public static readonly Hash128 SourceId = SubstrateCanonicalIds.Source(SourceName);
    public static readonly Hash128 TrustClassId = ChessVocabulary.SyzygyTrustClass;

    /// <summary>Witness weight of the oracle's testimony (the StandardsDerived trust band).</summary>
    public const double Weight = TC.StandardsDerived;

    /// <summary>Versioned per-POSITION marker — each board state is probed/deposited once.</summary>
    public static Hash128 MarkerId(Hash128 positionId, int version)
        => Hash128.OfCanonical($"chess/syzygy/{positionId}/{version}");

    public static Hash128 MaterialId(string material) =>
        Hash128.OfCanonical($"chess/syzygy/material/{material}/{MaterialGraphVersion}");

    public static Hash128 EndgameLineId(Hash128 lineId) =>
        Hash128.OfCanonical($"chess/syzygy/endgame/{lineId}/{MaterialGraphVersion}");

    /// <summary>Five-valued WDL content token, side-to-move POV (Fathom order 0..4).</summary>
    public static string WdlToken(int wdl) => wdl switch
    {
        SyzygyNative.Loss => "loss",
        SyzygyNative.BlessedLoss => "blessed-loss",
        SyzygyNative.Draw => "draw",
        SyzygyNative.CursedWin => "cursed-win",
        SyzygyNative.Win => "win",
        _ => throw new ArgumentOutOfRangeException(nameof(wdl), wdl, "WDL is 0..4"),
    };

    public readonly record struct Bitboards(
        ulong White, ulong Black, ulong Kings, ulong Queens, ulong Rooks,
        ulong Bishops, ulong Knights, ulong Pawns, uint Ep);

    /// <summary>
    /// 0x88 board → Fathom bitboards (a1=bit0..h8=bit63). Ep is the en-passant square
    /// only when a legal capture exists (the same canonical-ep fact position identity
    /// uses — <see cref="ChessModality.CapturableEpSquare"/>), else 0.
    /// </summary>
    public static Bitboards ToBitboards(Board b)
    {
        ulong white = 0, black = 0, kings = 0, queens = 0, rooks = 0,
              bishops = 0, knights = 0, pawns = 0;
        for (int sq = 0; sq < 128; sq++)
        {
            if ((sq & 0x88) != 0) { sq += 7; continue; }
            var p = b.Squares[sq];
            if (p == Piece.Empty) continue;
            ulong bit = 1UL << (Board.RankOf(sq) * 8 + Board.FileOf(sq));
            if (Board.IsWhite(p)) white |= bit; else black |= bit;
            switch (Board.TypeOf(p))
            {
                case Piece.WKing: kings |= bit; break;
                case Piece.WQueen: queens |= bit; break;
                case Piece.WRook: rooks |= bit; break;
                case Piece.WBishop: bishops |= bit; break;
                case Piece.WKnight: knights |= bit; break;
                case Piece.WPawn: pawns |= bit; break;
            }
        }
        int ep = ChessModality.CapturableEpSquare(b);
        uint ep64 = ep < 0 ? 0u : (uint)(Board.RankOf(ep) * 8 + Board.FileOf(ep));
        return new Bitboards(white, black, kings, queens, rooks, bishops, knights, pawns, ep64);
    }

    public static int MenCount(Board b)
    {
        int men = 0;
        for (int sq = 0; sq < 128; sq++)
        {
            if ((sq & 0x88) != 0) { sq += 7; continue; }
            if (b.Squares[sq] != Piece.Empty) men++;
        }
        return men;
    }

    /// <summary>
    /// Compose one unpacked product: position entity + HAS_WDL + HAS_DTZ (ctx null) +
    /// versioned position marker.
    /// </summary>
    public static void DeriveProduct(SubstrateChangeBuilder b, SyzygyProduct product)
    {
        var node = ChessGraph.EmitComposed(b, product.Surface, SourceId);
        if (ContentEmitter.Emit(b, WdlToken(product.Wdl), SourceId) is { } wdlId)
            b.AddAttestation(NativeAttestation.Categorical(
                node.Position.Id, "HAS_WDL", wdlId, SourceId, contextId: null, Weight));
        if (ContentEmitter.Emit(b, product.Dtz.ToString(), SourceId) is { } dtzId)
            b.AddAttestation(NativeAttestation.Categorical(
                node.Position.Id, "HAS_DTZ", dtzId, SourceId, contextId: null, Weight));

        b.AddEntity(MarkerId(product.PositionId, Version), EntityTier.Document,
            ChessVocabulary.AnalysisMarkerType, SourceId);
        if (ContentEmitter.Emit(b, Version.ToString(), SourceId) is { } vId)
            b.AddAttestation(NativeAttestation.Categorical(
                product.PositionId, "ANALYZED_AT", vId, SourceId, null, Weight));
    }

    internal const int TransitionsPerChunk = 2_048;

    /// <summary>
    /// Compact material-class representation: each decoded table entry is the shared
    /// position → typed move → position transition, packed into bounded trajectory chunks.
    /// WDL/DTZ ride the pre-state vertex flags.  A material with 500k positions therefore
    /// emits hundreds of chunk/root rows, not millions of entity/physicality/attestation rows.
    /// </summary>
    public static void DeriveMaterial(
        SubstrateChangeBuilder b, string material, IReadOnlyList<SyzygyProduct> products,
        Hash128 materialId)
    {
        ArgumentNullException.ThrowIfNull(b);
        if (products.Count == 0) return;

        var chunks = new List<SyzygyChunkRef>((products.Count + TransitionsPerChunk - 1) / TransitionsPerChunk);

        for (int offset = 0; offset < products.Count; offset += TransitionsPerChunk)
        {
            int take = Math.Min(TransitionsPerChunk, products.Count - offset);
            var slice = products.Skip(offset).Take(take).ToArray();
            var chunk = PrepareTransitionChunk(slice);
            if (chunk is not null)
            {
                DeriveTransitionChunk(b, chunk);
                chunks.Add(chunk.Reference);
            }
        }

        DeriveMaterialRoot(b, chunks, materialId);
    }

    internal static SyzygyTransitionChunk? PrepareTransitionChunk(
        IReadOnlyList<SyzygyProduct> products)
    {
        var facts = new List<Hash128>(products.Count);
        var ids = new List<Hash128>(products.Count * 3);
        var coords = new List<double>(products.Count * 12);
        var flags = new List<ulong>(products.Count * 3);
        var nodes = new Dictionary<Hash128, SyzygyGraphNode>(products.Count * 2);
        var verdicts = new List<SyzygyPositionVerdict>(products.Count);
        foreach (var product in products)
        {
            if (!TryTransition(product, out var from, out var move, out var to))
                throw InvalidTransition(product);
            facts.Add(Hash128.OfCanonical(
                $"chess/syzygy/transition/{from.Position.Id}/{move.Move.Id}/{to.Position.Id}/{product.Wdl}/{product.Dtz}"));
            ids.Add(from.Position.Id); ids.Add(move.Move.Id); ids.Add(to.Position.Id);
            coords.AddRange(from.Position.Coord);
            coords.AddRange(move.Move.Coord);
            coords.AddRange(to.Position.Coord);
            flags.Add(PackTransitionFlags(0, product.Wdl, product.Dtz));
            flags.Add(PackTransitionFlags(1, product.Wdl, product.Dtz));
            flags.Add(PackTransitionFlags(2, product.Wdl, product.Dtz));
            verdicts.Add(new SyzygyPositionVerdict(
                from.Position.Id, product.Wdl, product.Dtz));

            // A packed transition is an index over reusable Laplace objects, not an
            // opaque sidecar.  Persist every referenced position/move and the bounded
            // atom vocabulary that physically composes it in the SAME change as the
            // chunk.  The builder deduplicates repeated atoms and transpositions within
            // the chunk; content identity deduplicates them across chunks/materials.
            AddGraphNode(nodes, from.Position, ChessVocabulary.PositionType);
            AddGraphNode(nodes, to.Position, ChessVocabulary.PositionType);
            AddGraphNode(nodes, move.Move, ChessVocabulary.MoveType);
            foreach (var node in from.Substructures)
                AddGraphNode(nodes, node, ChessVocabulary.SubstructureType);
            foreach (var node in to.Substructures)
                AddGraphNode(nodes, node, ChessVocabulary.SubstructureType);
            foreach (var node in move.Fields)
                AddGraphNode(nodes, node, ChessVocabulary.SubstructureType);
        }
        if (facts.Count == 0) return null;
        return new SyzygyTransitionChunk(
            Hash128.Merkle(ChessCompose.SegmentTier, CollectionsMarshal.AsSpan(facts)),
            Math4d.KarcherMean(CollectionsMarshal.AsSpan(coords)),
            Trajectory.Build(CollectionsMarshal.AsSpan(ids), CollectionsMarshal.AsSpan(flags)),
            ids.Count,
            nodes.Values.ToArray(),
            verdicts);
    }

    internal static void DeriveTransitionChunk(
        SubstrateChangeBuilder b, SyzygyTransitionChunk chunk)
    {
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        foreach (var graphNode in chunk.Nodes)
            DeriveGraphNode(b, graphNode, nowUs);

        // The vertex flags are the compact exact-read accelerator.  They do not replace
        // governed, source-separable position-grain testimony: consensus and ordinary
        // substrate reads must see the tablebase verdict without decoding a projection.
        // Emit each small value vocabulary once per chunk, then attach the resolved typed
        // facts to every pre-state.  Repeated values and reruns converge by content/id.
        var wdlValues = new Dictionary<int, Hash128>();
        var dtzValues = new Dictionary<int, Hash128>();
        foreach (var verdict in chunk.Verdicts)
        {
            Hash128 wdlId = ResolveValue(
                b, wdlValues, verdict.Wdl, static value => WdlToken(value));
            Hash128 dtzId = ResolveValue(
                b, dtzValues, verdict.Dtz,
                static value => value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.AddAttestation(NativeAttestation.CategoricalResolved(
                verdict.PositionId, ChessVocabulary.HasWdlType, wdlId,
                SourceId, contextId: null, Weight));
            b.AddAttestation(NativeAttestation.CategoricalResolved(
                verdict.PositionId, ChessVocabulary.HasDtzType, dtzId,
                SourceId, contextId: null, Weight));
        }

        b.AddEntity(chunk.Id, ChessCompose.SegmentTier, ChessVocabulary.AnalysisMarkerType, SourceId);
        b.AddPhysicality(new PhysicalityRow(
            PhysicalityId.Compute(chunk.Id, PhysicalityType.Projection),
            chunk.Id, SourceId, PhysicalityType.Projection,
            chunk.Coord[0], chunk.Coord[1], chunk.Coord[2], chunk.Coord[3],
            Hilbert128.Encode(chunk.Coord), chunk.Trajectory,
            chunk.NConstituents, null, null, nowUs));
    }

    private static Hash128 ResolveValue(
        SubstrateChangeBuilder b, Dictionary<int, Hash128> values, int value,
        Func<int, string> surface)
    {
        if (values.TryGetValue(value, out var id)) return id;
        id = ContentEmitter.Emit(b, surface(value), SourceId)
             ?? throw new InvalidOperationException("Syzygy value composition produced no root");
        values.Add(value, id);
        return id;
    }

    private static void AddGraphNode(
        Dictionary<Hash128, SyzygyGraphNode> nodes, ChessNode node, Hash128 typeId)
    {
        if (nodes.TryGetValue(node.Id, out var prior))
        {
            if (prior.TypeId != typeId)
                throw new InvalidDataException(
                    $"Syzygy graph node {node.Id} resolved as two types ({prior.TypeId}, {typeId}).");
            return;
        }
        nodes.Add(node.Id, new SyzygyGraphNode(node, typeId));
    }

    private static void DeriveGraphNode(
        SubstrateChangeBuilder b, SyzygyGraphNode graphNode, long nowUs)
    {
        var node = graphNode.Node;
        b.AddEntity(node.Id, node.Tier, graphNode.TypeId, SourceId);
        if (!b.TrySeePhysicality(node.PhysId)) return;
        b.AddPhysicalityPreSeen(new PhysicalityRow(
            node.PhysId, node.Id, SourceId, PhysicalityType.Content,
            node.Coord[0], node.Coord[1], node.Coord[2], node.Coord[3], node.Hb,
            node.Trajectory, node.NConstituents, null, null, nowUs));
    }

    private static InvalidDataException InvalidTransition(SyzygyProduct product) => new(
        $"Syzygy returned a transition that does not resolve to a legal typed move: "
        + $"from={product.From}, to={product.To}, promotes={product.Promotes}, "
        + $"position={product.Surface}");

    internal static void DeriveMaterialRoot(
        SubstrateChangeBuilder b, IReadOnlyList<SyzygyChunkRef> chunks, Hash128 materialId)
    {
        if (chunks.Count == 0) return;
        var chunkIds = new Hash128[chunks.Count];
        var chunkCoords = new double[chunks.Count * 4];
        for (int i = 0; i < chunks.Count; i++)
        {
            chunkIds[i] = chunks[i].Id;
            chunks[i].Coord.CopyTo(chunkCoords, i * 4);
        }
        double[] rootCentroid = Math4d.KarcherMean(chunkCoords);
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        b.AddEntity(materialId, ChessCompose.LineTier, ChessVocabulary.AnalysisMarkerType, SourceId);
        b.AddPhysicality(new PhysicalityRow(
            PhysicalityId.Compute(materialId, PhysicalityType.Projection),
            materialId, SourceId, PhysicalityType.Projection,
            rootCentroid[0], rootCentroid[1], rootCentroid[2], rootCentroid[3],
            Hilbert128.Encode(rootCentroid), Trajectory.Build(chunkIds),
            chunkIds.Length, null, null, nowUs));
    }

    internal static ulong PackTransitionFlags(int role, int wdl, int dtz)
    {
        ulong zigzagDtz = unchecked((ulong)((dtz << 1) ^ (dtz >> 31)));
        return Trajectory.VertexFlags(ChessCompose.PositionTier, hasAtom: false, atom: 0)
               | ((ulong)(role & 0x3) << 8)
               | ((ulong)(wdl & 0x7) << 10)
               | ((zigzagDtz & 0xFFFFFFFFUL) << 16);
    }

    internal static (int Role, int Wdl, int Dtz) UnpackTransitionFlags(ulong flags)
    {
        int role = (int)((flags >> 8) & 0x3);
        int wdl = (int)((flags >> 10) & 0x7);
        uint zigzag = (uint)(flags >> 16);
        int dtz = (int)(zigzag >> 1) ^ -((int)zigzag & 1);
        return (role, wdl, dtz);
    }

    private static bool TryTransition(
        SyzygyProduct product, out ChessComposed from, out ChessMoveComposed move,
        out ChessComposed to)
    {
        from = to = null!;
        move = null!;
        if (product.From < 0 || product.To < 0) return false;
        Board board;
        if (!PositionContent.TryFenFromSurface(product.Surface, out string fen)) return false;
        try { board = Board.FromFen(fen); }
        catch (FormatException) { return false; }
        int fromSq = Board.Sq(product.From & 7, product.From >> 3);
        int toSq = Board.Sq(product.To & 7, product.To >> 3);
        ChessMove? selected = null;
        foreach (var candidate in MoveGen.Legal(board))
        {
            if (candidate.From != fromSq || candidate.To != toSq) continue;
            if (PromotionCode(candidate) != product.Promotes) continue;
            selected = candidate;
            break;
        }
        if (selected is not { } best) return false;
        Piece moving = board.Squares[best.From];
        from = ChessCompose.Position(board);
        move = ChessCompose.Move(moving, best);
        var next = board.Clone();
        MoveApply.Make(next, best);
        to = ChessCompose.Position(next);
        return true;
    }

    private static int PromotionCode(ChessMove move)
    {
        if (!move.IsPromotion) return 0;
        return Board.TypeOf(move.Promotion) switch
        {
            Piece.WQueen => 1,
            Piece.WRook => 2,
            Piece.WBishop => 3,
            Piece.WKnight => 4,
            _ => 0,
        };
    }

    /// <summary>
    /// Replay a witnessed line and persist every tablebase-covered state as the same exact
    /// transition primitive used by material ingest. One-transition chunks deduplicate across
    /// games by content; the line root preserves the observed endgame sequence.
    /// </summary>
    public static void DeriveGame(SubstrateChangeBuilder b, ChessWitnessedGame game, ISyzygyProber prober)
    {
        var m = new ChessModality();
        if (ChessAnalyze.InitialState(game.StartFen, m) is not { } start) return;
        var cur = start.Initial;
        var chunks = new List<SyzygyChunkRef>();
        int largest = prober.Largest;
        int n = game.Moves.Count;
        for (int ply = 0; ply <= n; ply++)
        {
            if (largest > 0
                && m.Terminal(cur) is null
                && cur.Board.Castle == CastleRights.None
                && MenCount(cur.Board) <= largest
                && prober.Probe(cur.Board) is { } verdict)
            {
                string surface = m.StateKey(cur);
                var product = new SyzygyProduct(
                    surface, ChessCompose.PositionId(surface), verdict.Wdl, verdict.Dtz,
                    verdict.From, verdict.To, verdict.Promotes);
                var chunk = PrepareTransitionChunk([product]);
                if (chunk is not null)
                {
                    DeriveTransitionChunk(b, chunk);
                    chunks.Add(chunk.Reference);
                }
            }

            if (ply == n) break;
            var mv = San.Resolve(cur.Board, m.LegalActions(cur), game.Moves[ply]);
            if (mv is null) break;
            cur = m.Apply(cur, mv.Value);
        }
        DeriveMaterialRoot(b, chunks, EndgameLineId(game.LineId));
    }
}
