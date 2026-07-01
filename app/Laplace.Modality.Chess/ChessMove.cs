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

public readonly record struct ChessMove(int From, int To, Piece Promotion, MoveFlags Flags)
{
    public bool IsPromotion => (Flags & MoveFlags.Promotion) != 0;

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
