import { useRef, type CSSProperties, type PointerEvent, type RefObject } from 'react';

export const GLYPH: Record<string, string> = {
  K: '♔', Q: '♕', R: '♖', B: '♗', N: '♘', P: '♙',
  k: '♚', q: '♛', r: '♜', b: '♝', n: '♞', p: '♟',
};

const FILES = 'abcdefgh';

export interface MoveScore { uci: string; effMu: number; rated: boolean; }

export function parseBoard(fen: string): string[][] {
  const rows = fen.split(' ')[0].split('/');
  return rows.map((row) => {
    const out: string[] = [];
    for (const c of row) {
      if (c >= '1' && c <= '8') for (let i = 0; i < Number(c); i++) out.push('');
      else out.push(c);
    }
    return out;
  });
}

export const sqName = (file: number, rank8: number) => `${FILES[file]}${8 - rank8}`;

export const whiteToMove = (fen: string) => fen.split(' ')[1] !== 'b';

const fmtDelta = (mu?: number) => { const d = (mu ?? 1500) - 1500; return `${d >= 0 ? '+' : ''}${Math.round(d)}`; };

function sqCenter(sq: string): { x: number; y: number } {
  const f = FILES.indexOf(sq[0]);
  const rank = Number(sq[1]);
  return { x: (f + 0.5) * 12.5, y: (8 - rank + 0.5) * 12.5 };
}

function elbow(from: string, to: string): string {
  const a = sqCenter(from), b = sqCenter(to);
  const corner = Math.abs(b.x - a.x) >= Math.abs(b.y - a.y) ? { x: b.x, y: a.y } : { x: a.x, y: b.y };
  return `${a.x},${a.y} ${corner.x},${corner.y} ${b.x},${b.y}`;
}

export interface BoardProps {
  fen: string;
  legal: MoveScore[];
  sel: string | null;
  drag: { from: string; piece: string; x: number; y: number } | null;
  marks: Set<string>;
  userArrows: { from: string; to: string }[];
  showPick: boolean;
  botBestTo?: string;
  whiteEval: number;
  evalFrac: number;
  leadTxt: string;
  winPct: number;
  boardRef: RefObject<HTMLDivElement | null>;
  flip?: boolean;
  lastMove?: { from: string; to: string } | null;
  onPointerDown: (e: PointerEvent, sq: string) => void;
  onPointerUp: (e: PointerEvent, sq: string) => void;
  onDragMove: (x: number, y: number) => void;
}

export function Board({
  fen, legal, sel, drag, marks, userArrows, showPick, botBestTo,
  whiteEval, evalFrac, leadTxt, winPct, boardRef, flip = false, lastMove,
  onPointerDown, onPointerUp, onDragMove,
}: BoardProps) {
  const board = parseBoard(fen);
  const ranks = flip ? [...board].reverse() : board;

  const selMoves = sel ? legal.filter((m) => m.uci.startsWith(sel)) : [];
  const targets = new Set(selMoves.map((m) => m.uci.slice(2, 4)));
  const evLo = Math.min(...selMoves.map((m) => m.effMu), Infinity);
  const evHi = Math.max(...selMoves.map((m) => m.effMu), -Infinity);
  const targetHue = (sq: string) => {
    const m = selMoves.find((x) => x.uci.slice(2, 4) === sq);
    if (!m) return null;
    const t = evHi > evLo ? (m.effMu - evLo) / (evHi - evLo) : 0.5;
    return Math.round(t * 130);
  };
  const targetMu = new Map(selMoves.map((m) => [m.uci.slice(2, 4), m.effMu]));

  const suggMark = new Map<string, { hue: number; role: 'from' | 'to' }>();
  if (!sel && !drag) {
    const sugg = [...legal].sort((a, b) => b.effMu - a.effMu).filter((m) => m.rated).slice(0, 5);
    sugg.forEach((m, i) => {
      const hue = Math.round((i / Math.max(1, sugg.length)) * 320);
      const from = m.uci.slice(0, 2), to = m.uci.slice(2, 4);
      if (!suggMark.has(to)) suggMark.set(to, { hue, role: 'to' });
      if (!suggMark.has(from)) suggMark.set(from, { hue, role: 'from' });
    });
  }

  const sqPx = (boardRef.current?.clientWidth ?? 480) / 8;

  return (
    <>
      <div className="board-wrap">
        <div className="board-area">
          <div className="eval-col">
            <div className="eval-bar" title={`eval (white): ${whiteEval >= 0 ? '+' : ''}${whiteEval.toFixed(0)}`}>
              <div className="eval-white" style={{ height: `${evalFrac * 100}%` }} />
            </div>
            <div className="eval-readout"><b>{leadTxt}</b><span>{winPct}% white</span></div>
          </div>
          <div
            className={`board${flip ? ' flipped' : ''}`} role="grid" ref={boardRef}
            onContextMenu={(e) => e.preventDefault()}
            onPointerMove={(e) => { if (drag) onDragMove(e.clientX, e.clientY); }}
          >
            {ranks.map((row, r) =>
              row.map((piece, fi) => {
                const f = flip ? 7 - fi : fi;
                const rank8 = flip ? r + 1 : 8 - r;
                const sq = sqName(f, rank8);
                const dark = (f + (8 - rank8)) % 2 === 1;
                const isLast = lastMove && (sq === lastMove.from || sq === lastMove.to);
                const inCheck = piece === (fen.split(' ')[1] === 'w' ? 'K' : 'k'); // simplified king highlight
                const sm = suggMark.get(sq);
                const suggTo = sm?.role === 'to' ? sm : null;
                const suggFrom = sm?.role === 'from' ? sm : null;
                const cls = ['square', dark ? 'dark' : 'light',
                  sel === sq ? 'sel' : '', targets.has(sq) ? 'target' : '',
                  marks.has(sq) ? 'marked' : '',
                  isLast ? 'last-move' : '',
                  inCheck ? 'in-check' : '',
                  suggTo ? 'sugg sugg-to' : '',
                  showPick && sq === botBestTo ? 'pick' : '',
                  drag?.from === sq ? 'dragging' : ''].join(' ');
                const th = targets.has(sq) ? targetHue(sq) : null;
                const style = suggTo
                  ? ({ ['--sugg' as string]: `hsl(${suggTo.hue} 90% 50%)` } as CSSProperties)
                  : th !== null
                  ? ({ ['--teval' as string]: `hsl(${th} 70% 55%)` } as CSSProperties)
                  : undefined;
                const pieceStyle = suggFrom
                  ? ({ color: `hsl(${suggFrom.hue} 85% 45%)` } as CSSProperties)
                  : undefined;
                return (
                  <div key={sq} className={cls} style={style}
                       onPointerDown={(e) => onPointerDown(e, sq)}
                       onPointerUp={(e) => onPointerUp(e, sq)}>
                    {piece && <span className={`piece${suggFrom ? ' piece-sugg' : ''}`} style={pieceStyle}>{GLYPH[piece]}</span>}
                    {showPick && sq === botBestTo && <span className="botpick" title="bot's pick">★</span>}
                    {targets.has(sq) && <span className="target-mu">{fmtDelta(targetMu.get(sq))}</span>}
                  </div>
                );
              }),
            )}
            <svg className="board-overlay" viewBox="0 0 100 100" preserveAspectRatio="none">
              <defs>
                <marker id="uarrowhead" markerWidth="4" markerHeight="4" refX="2.4" refY="2" orient="auto">
                  <path d="M0,0 L4,2 L0,4 Z" />
                </marker>
              </defs>
              {userArrows.map((ar, i) => (
                <polyline key={`${ar.from}${ar.to}${i}`} className="uarrow" markerEnd="url(#uarrowhead)"
                          points={elbow(ar.from, ar.to)} />
              ))}
            </svg>
          </div>
        </div>
        <div className="board-legend">
          <span><b className="lg-star">★</b> bot&rsquo;s pick</span>
          <span className="lg-scale"><i /> weaker → stronger move</span>
          <span><b>±</b> = rating points vs an even game (1500 = even)</span>
          <span>eval bar = who&rsquo;s ahead</span>
          <span>right-drag = arrow · right-click = mark</span>
        </div>
      </div>
      {drag && (
        <span className="drag-ghost" style={{
          left: drag.x, top: drag.y, fontSize: `${sqPx * 0.82}px`,
        }}>{GLYPH[drag.piece]}</span>
      )}
    </>
  );
}

export function useBoardRef() {
  return useRef<HTMLDivElement>(null);
}
