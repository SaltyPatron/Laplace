using Laplace.Engine.Core;

namespace Laplace.Chess.Service;

/// <summary>
/// The T0-EQUIVALENT for chess: the modality's tier-1 vocabulary, precomputed once and
/// indexed by structure instead of by string.
///
/// Chess has a finite, deterministic tier-1 alphabet — 12 piece kinds x 64 squares, plus
/// side-to-move, castling rights and en-passant. Under 900 entries, identical on every
/// machine and every run, exactly the shape laplace_t0_perfcache exists for at the codepoint
/// tier. It was being resolved through a ConcurrentDictionary keyed by STRING: measured at
/// 33.8 lookups per position and 68.1% of all position-composition time, which is ~28% of
/// the whole analyze pass, spent re-deriving ~900 constants hundreds of millions of times.
///
/// This holds them in a flat array. A token like "Pe2" resolves by parsing two characters
/// and indexing, with no hashing and no allocation.
///
/// IDS ARE BIT-IDENTICAL to the dictionary path. Entries are produced by the same
/// ComposeToken over the same tier-0 perfcache records, so a position composed through this
/// cache hashes exactly as before — the corpus stays valid. That is the whole constraint:
/// this is a lookup change, never an identity change.
///
/// Tokens outside the finite alphabet (the pawn-structure aggregates, which are
/// position-specific strings) fall through to the general path. They are ~5 of 33.8.
/// </summary>
internal static class ChessComposeProbe
{
    internal static ChessNode Compose(string token) => ChessCompose.ComposeTokenForProbe(token);
}

internal static class ChessVocabularyCache
{
    // 12 piece chars, index by their position in this string. Uppercase = white.
    private const string PieceChars = "PNBRQKpnbrqk";
    private const int Squares = 64;

    private static readonly ChessNode[] PieceSquare = new ChessNode[PieceChars.Length * Squares];

    // Filled ONCE, eagerly, by Prime(). It used to fill lazily inside TryGet:
    //
    //     if (!Present[i]) { PieceSquare[i] = compose(token); Present[i] = true; }
    //     node = PieceSquare[i];
    //
    // ChessNode is a ~60-byte struct (two Hash128, a Hilbert128, two array refs, an int, a
    // byte), so that assignment is a multi-word copy, not an atomic store, and neither write
    // carried a barrier. A second thread could observe Present[i] == true — or reach
    // PieceSquare[i] mid-copy — and read a TORN node: a correct Id paired with a stale
    // Trajectory or a mismatched PhysId. That is silent identity corruption, not a crash, and
    // it becomes reachable the moment chess joins the parallel file-worker pool that every
    // other multi-file source already uses.
    //
    // The alphabet is 768 entries and finite, so there is nothing to be lazy about: compose
    // all of it up front, publish with a barrier, and TryGet becomes a pure read.
    private static volatile bool _primed;
    private static readonly object PrimeGate = new();

    private static int Index(int piece, int file, int rank) => (piece * Squares) + (rank * 8) + file;

    /// <summary>
    /// Compose the whole finite piece-square alphabet once. Idempotent and safe to call from
    /// any thread; callers must invoke it before <see cref="TryGet"/> (ChessCompose does so
    /// from EnsureLoaded, after the codepoint perfcache is available — composition reads it).
    /// </summary>
    internal static void Prime(Func<string, ChessNode> compose)
    {
        if (_primed) return;
        lock (PrimeGate)
        {
            if (_primed) return;
            Span<char> tok = stackalloc char[3];
            for (int piece = 0; piece < PieceChars.Length; piece++)
            {
                tok[0] = PieceChars[piece];
                for (int rank = 0; rank < 8; rank++)
                {
                    for (int file = 0; file < 8; file++)
                    {
                        tok[1] = (char)('a' + file);
                        tok[2] = (char)('1' + rank);
                        PieceSquare[Index(piece, file, rank)] = compose(new string(tok));
                    }
                }
            }
            _primed = true;   // volatile write publishes every slot above it
        }
    }

    /// <summary>
    /// Resolve a tier-1 token by structure. Returns false for anything outside the finite
    /// alphabet so the caller can fall through — never a wrong answer, only "not mine".
    /// </summary>
    /// <summary>
    /// Span form: resolves a tier-1 token WITHOUT materialising it as a string. The caller
    /// slices the surface directly, so the ~30 piece-square tokens in a position never become
    /// heap objects — <c>surface.Split(' ')</c> was allocating one string per token per ply
    /// purely to recover the (piece, square) pair the board loop already had, then throwing
    /// them away. Same parse, same index, same node: this is a lookup change, not an identity
    /// change, exactly as the array itself was.
    /// </summary>
    internal static bool TryGet(ReadOnlySpan<char> token, out ChessNode node)
    {
        node = default;
        if (!_primed) return false;              // caller falls through to the composing path
        if (token.Length != 3) return false;
        int piece = PieceChars.IndexOf(token[0]);
        if (piece < 0) return false;
        int file = token[1] - 'a';
        int rank = token[2] - '1';
        if ((uint)file >= 8 || (uint)rank >= 8) return false;
        node = PieceSquare[Index(piece, file, rank)];
        return true;
    }

    internal static bool TryGet(string token, Func<string, ChessNode> compose, out ChessNode node)
    {
        node = default;
        // "Pe2" — piece char, file a-h, rank 1-8. Exactly three chars, nothing else qualifies.
        if (token.Length != 3) return false;
        int piece = PieceChars.IndexOf(token[0]);
        if (piece < 0) return false;
        int file = token[1] - 'a';
        int rank = token[2] - '1';
        if ((uint)file >= 8 || (uint)rank >= 8) return false;

        int i = Index(piece, file, rank);
        // Pure read on the primed table. The `compose` delegate is retained only for the
        // not-yet-primed case (direct unit-test entry, which never runs concurrently) so the
        // hot path stays a bounds-checked array read with no branch on per-slot state.
        if (!_primed) Prime(compose);
        node = PieceSquare[i];
        return true;
    }
}
