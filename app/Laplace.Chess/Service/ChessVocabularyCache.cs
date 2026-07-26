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
    private static readonly bool[] Present = new bool[PieceChars.Length * Squares];

    private static int Index(int piece, int file, int rank) => (piece * Squares) + (rank * 8) + file;

    /// <summary>
    /// Resolve a tier-1 token by structure. Returns false for anything outside the finite
    /// alphabet so the caller can fall through — never a wrong answer, only "not mine".
    /// </summary>
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
        if (!Present[i])
        {
            // Composed through the SAME path the dictionary used, so the id is identical.
            PieceSquare[i] = compose(token);
            Present[i] = true;
        }
        node = PieceSquare[i];
        return true;
    }
}
