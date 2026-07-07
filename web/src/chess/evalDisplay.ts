/** Centipawn scores at or beyond this magnitude are treated as forced mate. */
export const MATE_CP = 20_000;

/** Map white's centipawn eval to the eval-bar fill (0 = all black, 1 = all white). */
export function whiteCpToBarFraction(whiteCp: number): number {
  if (whiteCp >= MATE_CP) return 1;
  if (whiteCp <= -MATE_CP) return 0;
  return 1 / (1 + Math.exp(-whiteCp / 400));
}

/** Conventional chess eval label from white's point of view. */
export function formatPositionEval(whiteCp: number): { lead: string; detail: string } {
  if (whiteCp >= MATE_CP) return { lead: '#M', detail: 'White is winning' };
  if (whiteCp <= -MATE_CP) return { lead: '#M', detail: 'Black is winning' };
  const pawns = whiteCp / 100;
  if (Math.abs(pawns) < 0.05) return { lead: '0.0', detail: 'Even' };
  const sign = pawns > 0 ? '+' : '';
  return {
    lead: `${sign}${pawns.toFixed(1)}`,
    detail: `${sign}${pawns.toFixed(2)} pawns (white's view)`,
  };
}

/** Substrate move consensus as a delta from the neutral prior (1500). */
export function formatSubstrateMoveDelta(effMu: number): string {
  // ModalityEngine stamps terminal / mate-allowing edges outside the normal band.
  if (effMu <= 150) return '−M';
  if (effMu >= 3000) return '+M';
  const d = effMu - 1500;
  if (Math.abs(d) < 1) return '±0';
  return `${d >= 0 ? '+' : ''}${Math.round(d)}`;
}

export function terminalWhiteCp(status: string): number {
  if (status === 'white wins') return MATE_CP;
  if (status === 'black wins') return -MATE_CP;
  return 0;
}
