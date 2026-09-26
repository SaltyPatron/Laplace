using Laplace.Engine.Core;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

/// <summary>
/// RESOLVE for a FEN-shaped reference: the FEN is parsed at the boundary and resolves to
/// the position entity, the Merkle composition of typed board-state atoms (the same id
/// /chess/explore composes), not to the content hash of the FEN text. resolve_ref,
/// explore and the MCP topic surface all compose through here.
/// </summary>
public static class ChessPositionRef
{
    /// <summary>
    /// Cheap shape gate: 8 slash-separated ranks, side-to-move, at least the four
    /// mandatory FEN fields. Not a validator — Board.FromFen / InitialState refuse
    /// unreadable boards; this only keeps ordinary words out of the compose path.
    /// </summary>
    public static bool LooksLikeFen(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return false;
        if (parts[1] is not ("w" or "b")) return false;
        int slashes = 0;
        foreach (char c in parts[0])
            if (c == '/') slashes++;
        return slashes == 7;
    }

    /// <summary>
    /// Compose the position entity id for a FEN-shaped reference, or return false
    /// when the text is not a FEN / cannot be modelled (X-FEN Chess960, garbage).
    /// </summary>
    public static bool TryComposeId(string? text, out Hash128 id)
    {
        id = default;
        if (!LooksLikeFen(text)) return false;
        var fen = text!.Trim();
        var m = new ChessModality();
        if (ChessAnalyze.InitialState(fen, m) is not { } start) return false;
        lock (ChessCompose.Gate)
            id = ChessCompose.PositionId(start.Initial.Board);
        return true;
    }

    /// <summary>32-hex form resolve_ref accepts as a literal content id.</summary>
    public static string? TryComposeHex(string? text)
        => TryComposeId(text, out var id)
            ? Convert.ToHexString(id.ToBytes()).ToLowerInvariant()
            : null;

    /// <summary>
    /// When <paramref name="reference"/> is a FEN, rewrite it to the composed
    /// position hex so SQL resolve_ref / resolve_topic can decode it. Non-FEN
    /// text is returned unchanged (including null).
    /// </summary>
    public static string? RewriteFenToHex(string? reference)
        => reference is null ? null : (TryComposeHex(reference) ?? reference);
}
