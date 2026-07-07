export const PIECES = ['P', 'N', 'B', 'R', 'Q', 'K'] as const;

export const PIECE_NAME: Record<string, string> = {
  P: 'Pawn',
  N: 'Knight',
  B: 'Bishop',
  R: 'Rook',
  Q: 'Queen',
  K: 'King',
};
