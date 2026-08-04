using System.Collections.Concurrent;
using System.Text;
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

    // Finite tier-1 / pawn-aggregate token nodes only. NOT a position floor (#822).
    private static readonly ConcurrentDictionary<string, ChessNode> TokenMemo = new(StringComparer.Ordinal);

    private static readonly char[] Sep = { ' ' };

    /// <summary>
    /// Full position composition. Geometry for catalog ids uses native
    /// <see cref="ChessPositionFloor"/> when loaded (spec 33 / #822).
    /// No string→ChessComposed heap memo.
    /// </summary>
    public static ChessComposed Position(string surface)
    {
        EnsureLoaded();
        // Span scan, not Split: Split allocates a string[] plus one string PER TOKEN, on a
        // path that runs once per position composed. The finite piece/square alphabet is
        // served by ChessVocabularyCache from a ReadOnlySpan<char> with no string at all;
        // only a token outside that alphabet has to be materialised for the TokenMemo key.
        // Same two-pass shape as PositionId(string), which never regressed to Split.
        int tokenCount = 0;
        for (int i = 0; i < surface.Length; i++)
            if (surface[i] != ' ' && (i == 0 || surface[i - 1] == ' ')) tokenCount++;
        if (tokenCount == 0) throw new ArgumentException("empty position surface", nameof(surface));

        var subs = new ChessNode[tokenCount];
        var ids = new Hash128[tokenCount];
        var childCoords = new double[(long)tokenCount * 4];
        int t = 0;
        int start = -1;
        for (int i = 0; i <= surface.Length; i++)
        {
            bool end = i == surface.Length || surface[i] == ' ';
            if (!end) { if (start < 0) start = i; continue; }
            if (start < 0) continue;
            var tok = surface.AsSpan(start, i - start);
            start = -1;

            var s = ChessVocabularyCache.TryGet(tok, out var vocab)
                ? vocab
                : TokenMemo.GetOrAdd(new string(tok), ComposeToken);
            subs[t] = s;
            ids[t] = s.Id;
            childCoords[t * 4 + 0] = s.Coord[0]; childCoords[t * 4 + 1] = s.Coord[1];
            childCoords[t * 4 + 2] = s.Coord[2]; childCoords[t * 4 + 3] = s.Coord[3];
            t++;
        }

        Hash128 id = Hash128.Merkle(PositionTier, ids);
        double[] traj = Trajectory.Build(ids);
        Hash128 physId = PhysicalityId.Compute(id, PhysicalityType.Content);

        if (ChessPositionFloor.TryLookup(id, out var x, out var y, out var z, out var m,
                out var hb, out var n, out var tier))
        {
            var coord = new[] { x, y, z, m };
            return new ChessComposed(
                new ChessNode(id, coord, hb, traj, physId, (int)n, tier), subs);
        }

        return new ChessComposed(ComposeOver(ids, childCoords, tokenCount, PositionTier), subs);
    }

    internal static ChessNode TokenNode(string token)
        => ChessVocabularyCache.TryGet(token, ComposeToken, out var v) ? v : TokenMemo.GetOrAdd(token, ComposeToken);

    internal static ChessComposed ComposeUncached(string surface)
    {
        EnsureLoaded();
        var tokens = surface.Split(Sep, StringSplitOptions.RemoveEmptyEntries);
        var subs = new ChessNode[tokens.Length];
        var ids = new Hash128[tokens.Length];
        var coords = new double[(long)tokens.Length * 4];
        for (int i = 0; i < tokens.Length; i++)
        {
            var s = ChessVocabularyCache.TryGet(tokens[i], ComposeToken, out var v)
                ? v : TokenMemo.GetOrAdd(tokens[i], ComposeToken);
            subs[i] = s; ids[i] = s.Id;
            coords[i * 4 + 0] = s.Coord[0]; coords[i * 4 + 1] = s.Coord[1];
            coords[i * 4 + 2] = s.Coord[2]; coords[i * 4 + 3] = s.Coord[3];
        }
        return new ChessComposed(ComposeOver(ids, coords, tokens.Length, PositionTier), subs);
    }

    public static Hash128 PositionId(string surface)
    {
        EnsureLoaded();
        // Span-scan the surface — no Split string[] of ~30 tokens per ply.
        int tokenCount = 0;
        for (int i = 0; i < surface.Length; i++)
            if (surface[i] != ' ' && (i == 0 || surface[i - 1] == ' ')) tokenCount++;
        if (tokenCount == 0) throw new ArgumentException("empty position surface", nameof(surface));

        Span<Hash128> ids = tokenCount <= 64
            ? stackalloc Hash128[tokenCount]
            : new Hash128[tokenCount];
        int n = 0;
        int start = -1;
        for (int i = 0; i <= surface.Length; i++)
        {
            bool end = i == surface.Length || surface[i] == ' ';
            if (!end) { if (start < 0) start = i; continue; }
            if (start < 0) continue;
            var tok = surface.AsSpan(start, i - start);
            ids[n++] = TokenId(tok);
            start = -1;
        }
        return Hash128.Merkle(PositionTier, ids[..n]);
    }

    /// <summary>
    /// Board → position id without materialising <see cref="PositionContent.Surface"/>.
    /// Token order and ids are bit-identical to <see cref="PositionId(string)"/> on that surface.
    /// </summary>
    public static Hash128 PositionId(Board board, ChessVariantRules? rules = null)
    {
        EnsureLoaded();
        rules ??= ChessVariantRules.Standard;
        var bb = Bitboards.FromBoard(board);
        int epSq = ChessModality.CapturableEpSquare(board);
        string ep = epSq < 0 ? "-" : Board.SquareToAlgebraic(epSq);

        // Header + ≤32 pieces + pawn aggregates + features — well under 64 for standard.
        Span<Hash128> ids = stackalloc Hash128[64];
        int n = 0;

        string ruleSurface = rules.Surface();
        if (ruleSurface.Length > 0)
            ids[n++] = TokenId(("rules:" + ruleSurface).AsSpan());

        ids[n++] = TokenId(board.WhiteToMove ? "stm:w".AsSpan() : "stm:b".AsSpan());
        ids[n++] = TokenId(("cr:" + board.CastleString()).AsSpan());
        ids[n++] = TokenId(("ep:" + ep).AsSpan());

        foreach (int bit in Bitboards.Bits(bb.Occupied))
        {
            int f = Bitboards.FileOfBit(bit), r = Bitboards.RankOfBit(bit);
            char pc = Board.PieceToChar(board.Squares[Board.Sq(f, r)]);
            if (ChessVocabularyCache.TryGetPieceSquare(pc, f, r, out var node))
                ids[n++] = node.Id;
            else
                ids[n++] = TokenId($"{pc}{(char)('a' + f)}{(char)('1' + r)}".AsSpan());
        }

        // The wpawns:/bpawns:/wpf:/bpf:/mat: tokens USED TO BE EMITTED HERE, and this is
        // the other half of removing them from the identity surface (PositionContent.Surface,
        // plan item 6b). This path must stay bit-identical to PositionId(surface), and the
        // block was dead under BOTH settings of the flag: with IncludeFeatureTokens false the
        // surface no longer carries them, so emitting them here diverged the two paths; with
        // it true the branch below re-derives the id from the surface string and discards
        // `ids` entirely. Emitting them was therefore never right, only unnoticed — the
        // surface half shipped without this and left
        // ChessComposeBoardPositionIdTests.BoardPositionId_MatchesSurfacePath_StartAndPlies
        // failing, which is why it never landed.

        if (PositionContent.IncludeFeatureTokens)
        {
            // Rare path — keep identity via the surface string rather than re-encode features.
            return PositionId(PositionContent.Surface(board, ep, rules));
        }

        return Hash128.Merkle(PositionTier, ids[..n]);
    }

    private static Hash128 TokenId(ReadOnlySpan<char> token)
    {
        if (ChessVocabularyCache.TryGet(token, out var vocab)) return vocab.Id;
        return TokenMemo.GetOrAdd(token.ToString(), ComposeToken).Id;
    }

    private static void AppendPawnToken(StringBuilder sb, string tag, ulong pawns)
    {
        sb.Append(tag);
        bool first = true;
        foreach (int bit in Bitboards.Bits(pawns))
        {
            if (!first) sb.Append('.');
            sb.Append((char)('a' + Bitboards.FileOfBit(bit)));
            sb.Append((char)('1' + Bitboards.RankOfBit(bit)));
            first = false;
        }
        if (first) sb.Append('-');
    }

    [ThreadStatic] private static StringBuilder? t_sb;
    private static StringBuilder RentSb()
    {
        var sb = t_sb;
        if (sb is null) { sb = new StringBuilder(96); t_sb = sb; }
        else sb.Clear();
        return sb;
    }
    private static void ReturnSb(StringBuilder sb) => sb.Clear();

    internal static ChessNode ComposeTokenForProbe(string token) => ComposeToken(token);

    private static ChessNode ComposeToken(string token)
    {
        var recs = CodepointPerfcache.Records;
        int n = 0;
        foreach (var _ in token.EnumerateRunes()) n++;
        if (n == 0) throw new ArgumentException("empty token", nameof(token));

        var ids = new Hash128[n];
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

        double[] coord = Math4d.Centroid(childCoords);
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
            if (!CodepointPerfcache.IsLoaded) CodepointPerfcache.LoadDefault();
            ChessVocabularyCache.Prime(ComposeToken);
            ChessPositionFloor.LoadDefault();
            ChessTransitionFloor.LoadDefault();
            _composeReady = true;
        }
    }
}
