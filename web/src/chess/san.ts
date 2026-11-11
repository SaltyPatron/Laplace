/**
 * Standard algebraic notation for a move, from the position before it.
 * Disambiguation uses pseudo-legal reach (pins are ignored), which matches SAN in every
 * position except the rare one where a pinned twin could otherwise reach the square.
 */
type Grid = string[][];

const FILES = 'abcdefgh';

function grid(fen: string): Grid {
  return fen.split(' ')[0].split('/').map((row) => {
    const out: string[] = [];
    for (const c of row) {
      if (c >= '1' && c <= '8') for (let i = 0; i < Number(c); i++) out.push('');
      else out.push(c);
    }
    return out;
  });
}

/** [row, col] with row 0 = rank 8. */
const at = (sq: string): [number, number] => [8 - Number(sq[1]), FILES.indexOf(sq[0])];
const inside = (r: number, c: number) => r >= 0 && r < 8 && c >= 0 && c < 8;
const isWhite = (p: string) => p !== '' && p === p.toUpperCase();

const KNIGHT = [[1, 2], [2, 1], [-1, 2], [-2, 1], [1, -2], [2, -1], [-1, -2], [-2, -1]];
const KING = [[1, 0], [-1, 0], [0, 1], [0, -1], [1, 1], [1, -1], [-1, 1], [-1, -1]];
const ROOK = [[1, 0], [-1, 0], [0, 1], [0, -1]];
const BISHOP = [[1, 1], [1, -1], [-1, 1], [-1, -1]];

/** Can the piece on (r, c) reach (tr, tc) by its movement pattern on this board (ignoring pins)? */
function reaches(g: Grid, r: number, c: number, tr: number, tc: number): boolean {
  const piece = g[r][c].toLowerCase();
  const dr = tr - r, dc = tc - c;
  const step = (dirs: number[][], slide: boolean) => dirs.some(([sr, sc]) => {
    let rr = r + sr, cc = c + sc;
    while (inside(rr, cc)) {
      if (rr === tr && cc === tc) return true;
      if (!slide || g[rr][cc] !== '') return false;
      rr += sr; cc += sc;
    }
    return false;
  });
  if (piece === 'n') return KNIGHT.some(([a, b]) => a === dr && b === dc);
  if (piece === 'k') return KING.some(([a, b]) => a === dr && b === dc);
  if (piece === 'r') return step(ROOK, true);
  if (piece === 'b') return step(BISHOP, true);
  if (piece === 'q') return step(ROOK, true) || step(BISHOP, true);
  return false;
}

function attacked(g: Grid, r: number, c: number, byWhite: boolean): boolean {
  for (let rr = 0; rr < 8; rr++) for (let cc = 0; cc < 8; cc++) {
    const p = g[rr][cc];
    if (!p || isWhite(p) !== byWhite) continue;
    if (p.toLowerCase() === 'p') {
      const dir = byWhite ? -1 : 1;
      if (r - rr === dir && Math.abs(c - cc) === 1) return true;
    } else if (reaches(g, rr, cc, r, c)) return true;
  }
  return false;
}

function play(g: Grid, uci: string, enPassant: boolean): Grid {
  const next = g.map((row) => row.slice());
  const [fr, fc] = at(uci.slice(0, 2));
  const [tr, tc] = at(uci.slice(2, 4));
  let piece = next[fr][fc];
  next[fr][fc] = '';
  if (enPassant) next[fr][tc] = '';
  if (uci.length > 4) piece = isWhite(piece) ? uci[4].toUpperCase() : uci[4].toLowerCase();
  next[tr][tc] = piece;
  if (piece.toLowerCase() === 'k' && Math.abs(tc - fc) === 2) {
    const rookFrom = tc > fc ? 7 : 0, rookTo = tc > fc ? 5 : 3;
    next[tr][rookTo] = next[tr][rookFrom];
    next[tr][rookFrom] = '';
  }
  return next;
}

/**
 * SAN for `uci` played from `fenBefore`. `statusAfter` (the server's game status) marks mate;
 * check is computed from the resulting position.
 */
export function toSan(fenBefore: string, uci: string, statusAfter?: string): string {
  if (!/^[a-h][1-8][a-h][1-8][qrbn]?$/.test(uci)) return uci;
  const g = grid(fenBefore);
  const [fr, fc] = at(uci.slice(0, 2));
  const [tr, tc] = at(uci.slice(2, 4));
  const piece = g[fr]?.[fc];
  if (!piece) return uci;
  const white = isWhite(piece);
  const kind = piece.toLowerCase();
  const target = g[tr][tc];
  const epSquare = fenBefore.split(' ')[3];
  const enPassant = kind === 'p' && fc !== tc && target === '' && epSquare === uci.slice(2, 4);
  const capture = target !== '' || enPassant;

  let san: string;
  if (kind === 'k' && Math.abs(tc - fc) === 2) {
    san = tc > fc ? 'O-O' : 'O-O-O';
  } else if (kind === 'p') {
    san = `${capture ? `${FILES[fc]}x` : ''}${uci.slice(2, 4)}${uci.length > 4 ? `=${uci[4].toUpperCase()}` : ''}`;
  } else {
    const twins: [number, number][] = [];
    for (let r = 0; r < 8; r++) for (let c = 0; c < 8; c++)
      if ((r !== fr || c !== fc) && g[r][c] === piece && reaches(g, r, c, tr, tc)) twins.push([r, c]);
    let which = '';
    if (twins.length > 0) {
      if (!twins.some(([, c]) => c === fc)) which = FILES[fc];
      else if (!twins.some(([r]) => r === fr)) which = String(8 - fr);
      else which = `${FILES[fc]}${8 - fr}`;
    }
    san = `${kind.toUpperCase()}${which}${capture ? 'x' : ''}${uci.slice(2, 4)}`;
  }

  if (statusAfter && /mate/i.test(statusAfter) && !/stalemate/i.test(statusAfter)) return `${san}#`;
  const after = play(g, uci, enPassant);
  const enemyKing = white ? 'k' : 'K';
  for (let r = 0; r < 8; r++) for (let c = 0; c < 8; c++)
    if (after[r][c] === enemyKing && attacked(after, r, c, white)) return `${san}+`;
  return san;
}

function fenOf(g: Grid, whiteToMove: boolean, ep: string): string {
  const rows = g.map((row) => {
    let out = '', empty = 0;
    for (const p of row) {
      if (p === '') { empty++; continue; }
      if (empty) { out += String(empty); empty = 0; }
      out += p;
    }
    return empty ? out + String(empty) : out;
  });
  return `${rows.join('/')} ${whiteToMove ? 'w' : 'b'} - ${ep} 0 1`;
}

/** A line of UCI moves from `fen`, written in standard algebraic notation. */
export function sanLine(fen: string, ucis: string[]): string[] {
  const out: string[] = [];
  let current = fen;
  for (const uci of ucis) {
    if (!/^[a-h][1-8][a-h][1-8][qrbn]?$/.test(uci)) return [...out, ...ucis.slice(out.length)];
    out.push(toSan(current, uci));
    const g = grid(current);
    const [fr, fc] = at(uci.slice(0, 2));
    const [tr, tc] = at(uci.slice(2, 4));
    const piece = g[fr][fc];
    const ep = current.split(' ')[3];
    const enPassant = piece.toLowerCase() === 'p' && fc !== tc && g[tr][tc] === '' && ep === uci.slice(2, 4);
    const nextEp = piece.toLowerCase() === 'p' && Math.abs(tr - fr) === 2 ? `${FILES[fc]}${8 - (fr + tr) / 2}` : '-';
    current = fenOf(play(g, uci, enPassant), !isWhite(piece), nextEp);
  }
  return out;
}
