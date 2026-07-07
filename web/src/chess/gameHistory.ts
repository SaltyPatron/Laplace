export interface PositionSnapshot {
  fen: string;
  status: string;
  lastMove: { from: string; to: string } | null;
  uci?: string;
}

export const START_FEN = 'rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1';

export function initialPositions(): PositionSnapshot[] {
  return [{ fen: START_FEN, status: 'ongoing', lastMove: null }];
}

export function snapshotFromMove(
  fen: string,
  status: string,
  uci: string,
): PositionSnapshot {
  return {
    fen,
    status,
    uci,
    lastMove: { from: uci.slice(0, 2), to: uci.slice(2, 4) },
  };
}

export function historyFromPositions(positions: PositionSnapshot[]): string[] {
  return positions.slice(1).map((p) => p.uci!).filter(Boolean);
}

export function plyLabel(ply: number, totalPlies: number): string {
  if (ply <= 0) return 'Start';
  if (ply >= totalPlies) return `Move ${totalPlies}`;
  return `After ${ply}`;
}
