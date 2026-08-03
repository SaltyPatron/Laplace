using System.Collections.Concurrent;
using Laplace.Engine.Core;
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
    // GH #736: the LINE — a whole game as content, the chess ladder's document floor.
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

    public static object Gate => LaplaceCoreGate.Native;

    private static readonly ConcurrentDictionary<string, ChessNode> TokenMemo = new(StringComparer.Ordinal);

    // Position-tier composition (Merkle hash + centroid + Hilbert encode + trajectory build +
    // physicality id) was being fully recomputed on every call, even for a position already
    // composed earlier in the same run — real, avoidable cost across a corpus where opening/
    // early-game positions (and common transpositions) recur across many thousands of games.
    // SubstrateChangeBuilder.AddEntity/AddPhysicality already dedupe by id within a batch, so a
    // repeated position was computed in full and then silently discarded — CPU spent for
    // nothing. Bounded (not a static-forever dictionary like TokenMemo's small, finite piece/
    // square vocabulary) because most middle/endgame positions in a huge corpus are unique
    // one-offs; past the cap, composition just isn't memoized rather than growing unbounded.
    private const int PositionMemoCap = 2_000_000;
    private static readonly ConcurrentDictionary<string, ChessComposed> PositionMemo = new(StringComparer.Ordinal);

    private static readonly char[] Sep = { ' ' };

    /// <summary>Token count under Split(' ', RemoveEmptyEntries) semantics, without allocating.</summary>
    private static int CountTokens(ReadOnlySpan<char> s)
    {
        int n = 0, i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && s[i] == ' ') i++;
            if (i >= s.Length) break;
            n++;
            while (i < s.Length && s[i] != ' ') i++;
        }
        return n;
    }

    public static ChessComposed Position(string surface)
    {
        if (PositionMemo.TryGetValue(surface, out var cached)) return cached;

        EnsureLoaded();
        // surface.Split(' ') allocated one fresh STRING per token — ~34 per position, per ply —
        // purely to recover the (piece, square) pair the board loop in PositionContent.Surface
        // already had, then hand it to a parse that indexes an array. Walk the span instead and
        // materialise a string ONLY for the ~5 aggregate tokens that miss the finite alphabet.
        // Same tokens, same order, same ids: a lookup change, never an identity change.
        var span = surface.AsSpan();
        int count = CountTokens(span);
        if (count == 0) throw new ArgumentException("empty position surface", nameof(surface));

        var subs = new ChessNode[count];
        var ids = new Hash128[count];
        var coords = new double[(long)count * 4];

        int i = 0, p = 0;
        while (p < span.Length)
        {
            while (p < span.Length && span[p] == ' ') p++;
            if (p >= span.Length) break;
            int start = p;
            while (p < span.Length && span[p] != ' ') p++;
            var tok = span[start..p];

            // Finite tier-1 alphabet first: structural index, no hashing, no allocation.
            // Everything else (the pawn-structure aggregates) falls through to the memo.
            var s = ChessVocabularyCache.TryGet(tok, out var vocab)
                ? vocab
                : TokenMemo.GetOrAdd(new string(tok), ComposeToken);
            subs[i] = s;
            ids[i] = s.Id;
            coords[i * 4 + 0] = s.Coord[0]; coords[i * 4 + 1] = s.Coord[1];
            coords[i * 4 + 2] = s.Coord[2]; coords[i * 4 + 3] = s.Coord[3];
            i++;
        }

        var pos = ComposeOver(ids, coords, count, PositionTier);
        var composed = new ChessComposed(pos, subs);
        if (PositionMemo.Count < PositionMemoCap) PositionMemo.TryAdd(surface, composed);
        return composed;
    }

    /// <summary>Tier-1 vocabulary resolution alone (memoized), for attribution probes.</summary>
    internal static ChessNode TokenNode(string token)
        => ChessVocabularyCache.TryGet(token, ComposeToken, out var v) ? v : TokenMemo.GetOrAdd(token, ComposeToken);

    /// <summary>Position composition with the memo bypassed, for attribution probes.</summary>
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

    /// <summary>
    /// Id only, no geometry. This is the replay hot path — TryReplayLine calls it once per ply
    /// to build the ordered position ids LineId is the Merkle of — so it gets the same span
    /// walk as Position(): no Split, no per-token string, allocation only for the handful of
    /// aggregate tokens outside the finite alphabet.
    /// </summary>
    public static Hash128 PositionId(string surface)
    {
        EnsureLoaded();
        var span = surface.AsSpan();
        int count = CountTokens(span);
        if (count == 0) throw new ArgumentException("empty position surface", nameof(surface));

        var ids = new Hash128[count];
        int i = 0, p = 0;
        while (p < span.Length)
        {
            while (p < span.Length && span[p] == ' ') p++;
            if (p >= span.Length) break;
            int start = p;
            while (p < span.Length && span[p] != ' ') p++;
            var tok = span[start..p];
            ids[i++] = ChessVocabularyCache.TryGet(tok, out var v)
                ? v.Id
                : TokenMemo.GetOrAdd(new string(tok), ComposeToken).Id;
        }
        return Hash128.Merkle(PositionTier, ids);
    }

    /// <summary>The general composition path, exposed so tests can pin identity against it.</summary>
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
        double[] coord = Math4d.Centroid(childCoords);
        Hilbert128 hb = Hilbert128.Encode(coord);
        double[] traj = Trajectory.Build(childIds);
        Hash128 physId = PhysicalityId.Compute(id, PhysicalityType.Content);
        return new ChessNode(id, coord, hb, traj, physId, n, tier);
    }

    private static void EnsureLoaded()
    {
        if (!CodepointPerfcache.IsLoaded) CodepointPerfcache.LoadDefault();
        // Compose the finite 768-entry piece-square alphabet up front, once. Must follow the
        // perfcache load (ComposeToken reads CodepointPerfcache.Records) and must precede any
        // TryGet, which is now a pure read. See ChessVocabularyCache.Prime for the torn-struct
        // race this replaces.
        ChessVocabularyCache.Prime(ComposeToken);
    }
}
