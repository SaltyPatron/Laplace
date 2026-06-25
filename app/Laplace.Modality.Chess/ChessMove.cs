namespace Laplace.Modality.Chess;

[Flags]
public enum MoveFlags : byte
{
    None = 0,
    DoublePush = 1,
    EnPassant = 2,
    CastleKing = 4,
    CastleQueen = 8,
    Promotion = 16,
}

/// <summary>From/to are 0x88 squares. Promotion is the promoted piece TYPE (white-positive) or Empty.</summary>
public readonly record struct ChessMove(int From, int To, Piece Promotion, MoveFlags Flags)
{
    public bool IsPromotion => (Flags & MoveFlags.Promotion) != 0;

    /// <summary>UCI long algebraic, e.g. "e2e4", "e7e8q".</summary>
    public string ToUci()
    {
        string s = Board.SquareToAlgebraic(From) + Board.SquareToAlgebraic(To);
        if (IsPromotion)
        {
            char c = Board.TypeOf(Promotion) switch
            {
                Piece.WKnight => 'n',
                Piece.WBishop => 'b',
                Piece.WRook => 'r',
                Piece.WQueen => 'q',
                _ => '?',
            };
            s += c;
        }
        return s;
    }

    public override string ToString() => ToUci();
}
