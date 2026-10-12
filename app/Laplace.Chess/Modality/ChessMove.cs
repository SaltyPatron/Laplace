namespace Laplace.Modality.Chess;

[Flags]
public enum MoveFlags : byte
{
    None = 0,
    DoublePush = 1,
    EnPassant = 2,
    Castle = 4,
    Promotion = 8,
    Drop = 16,
}

public readonly record struct ChessMove(int From, int To, Piece Promotion, MoveFlags Flags)
{
    public bool IsDrop => (Flags & MoveFlags.Drop) != 0;
    public bool IsPromotion => (Flags & MoveFlags.Promotion) != 0;
    public bool IsCastle => (Flags & MoveFlags.Castle) != 0;
    public bool IsKingSideCastle => IsCastle && Board.FileOf(To) == CastlePaths.KingSideKingFile;
    public bool IsQueenSideCastle => IsCastle && Board.FileOf(To) == CastlePaths.QueenSideKingFile;

    public string ToUci()
    {
        if (IsDrop) return $"{Board.PieceToChar(Board.TypeOf(Promotion))}@{Board.SquareToAlgebraic(To)}";
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
