import { useEffect, useRef, useState, type CSSProperties, type PointerEvent, type ReactNode, type RefObject } from 'react';
import { cn, Tooltip, TooltipContent, TooltipTrigger } from '@ui';
import { formatSubstrateMoveDelta } from '../evalDisplay';
import shared from './playShared.module.css';
import styles from './Board.module.css';

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

export const sqName = (file: number, rowIdx: number) => `${FILES[file]}${8 - rowIdx}`;

const visualOrder = (flip: boolean) => (flip ? [7, 6, 5, 4, 3, 2, 1, 0] : [0, 1, 2, 3, 4, 5, 6, 7]);

export const whiteToMove = (fen: string) => fen.split(' ')[1] !== 'b';

function sqCenter(sq: string, flip: boolean): { x: number; y: number } {
  const f = FILES.indexOf(sq[0]);
  const rank = Number(sq[1]);
  const col = flip ? 7 - f : f;
  const row = flip ? rank - 1 : 8 - rank;
  return { x: (col + 0.5) * 12.5, y: (row + 0.5) * 12.5 };
}

function elbow(from: string, to: string, flip: boolean): string {
  const a = sqCenter(from, flip), b = sqCenter(to, flip);
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
  whiteEval: string;
  evalFrac: number;
  evalDetail: string;
  evalPending?: boolean;
  boardRef: RefObject<HTMLDivElement | null>;
  flip?: boolean;
  lastMove?: { from: string; to: string } | null;
  matedKing?: string | null;
  checkedKing?: string | null;
  readOnly?: boolean;
  footer?: ReactNode;
  onPointerDown: (e: PointerEvent, sq: string) => void;
  onPointerUp: (e: PointerEvent, sq: string) => void;
  onDragMove: (x: number, y: number) => void;
}

export function Board({
  fen, legal, sel, drag, marks, userArrows, showPick, botBestTo,
  whiteEval, evalFrac, evalDetail, evalPending = false, boardRef, flip = false, lastMove, matedKing, checkedKing,
  readOnly = false, footer,
  onPointerDown, onPointerUp, onDragMove,
}: BoardProps) {
  const board = parseBoard(fen);
  const rowOrder = visualOrder(flip);
  const colOrder = visualOrder(flip);

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
  if (!readOnly && !sel && !drag) {
    const sugg = [...legal].sort((a, b) => b.effMu - a.effMu).filter((m) => m.rated).slice(0, 5);
    sugg.forEach((m, i) => {
      const hue = Math.round((i / Math.max(1, sugg.length)) * 320);
      const from = m.uci.slice(0, 2), to = m.uci.slice(2, 4);
      if (!suggMark.has(to)) suggMark.set(to, { hue, role: 'to' });
      if (!suggMark.has(from)) suggMark.set(from, { hue, role: 'from' });
    });
  }

  const [boardW, setBoardW] = useState(0);
  useEffect(() => {
    const el = boardRef.current;
    if (!el) return;
    setBoardW(el.clientWidth);
    const ro = new ResizeObserver(([entry]) => setBoardW(entry.contentRect.width));
    ro.observe(el);
    return () => ro.disconnect();
  }, [boardRef]);
  const sqPx = (boardW || boardRef.current?.clientWidth || 480) / 8;

  return (
    <>
      <div className={styles.wrap}>
        <div className={styles.area}>
          <div className={styles.evalCol}>
            <div className={styles.evalBarSlot}>
              <Tooltip>
                <TooltipTrigger asChild>
                  <div
                    className={cn(styles.evalBar, evalPending && styles.evalBarPending)}
                    role="meter"
                    aria-valuemin={0}
                    aria-valuemax={100}
                    aria-valuenow={Math.round(evalFrac * 100)}
                    aria-label={evalDetail}
                  >
                    <div
                      className={styles.evalWhite}
                      style={
                        flip
                          ? { top: 0, bottom: 'auto', height: `${evalFrac * 100}%` }
                          : { bottom: 0, top: 'auto', height: `${evalFrac * 100}%` }
                      }
                    />
                  </div>
                </TooltipTrigger>
                <TooltipContent>{evalDetail}</TooltipContent>
              </Tooltip>
            </div>
            <div className={styles.evalReadout}>
              <b>{whiteEval}</b>
              <span>white&rsquo;s view</span>
            </div>
          </div>
          <div
            className={styles.board}
            role="grid"
            ref={boardRef}
            onContextMenu={(e) => e.preventDefault()}
            onPointerMove={(e) => { if (drag) onDragMove(e.clientX, e.clientY); }}
          >
            {rowOrder.map((boardRow) =>
              colOrder.map((f) => {
                const piece = board[boardRow]?.[f] ?? '';
                const rank = 8 - boardRow;
                const sq = sqName(f, boardRow);
                const dark = (f + rank) % 2 === 1;
                const isLast = lastMove && (sq === lastMove.from || sq === lastMove.to);
                const inCheck = !!checkedKing && piece === checkedKing;
                const mated = !!matedKing && piece === matedKing;
                const sm = suggMark.get(sq);
                const style = sm
                  ? ({ ['--sugg' as string]: `hsl(${sm.hue} 70% 50%)` } as CSSProperties)
                  : targets.has(sq) && targetHue(sq) !== null
                  ? ({ ['--teval' as string]: `hsl(${targetHue(sq)} 70% 55%)` } as CSSProperties)
                  : undefined;
                return (
                  <div
                    key={sq}
                    className={cn(
                      styles.square,
                      dark ? styles.dark : styles.light,
                      sel === sq && styles.sel,
                      targets.has(sq) && styles.target,
                      marks.has(sq) && styles.marked,
                      isLast && styles.lastMove,
                      inCheck && styles.inCheck,
                      mated && styles.mated,
                      sm && (sm.role === 'to' ? styles.suggTo : styles.sugg),
                      showPick && sq === botBestTo && styles.pick,
                      drag?.from === sq && styles.dragging,
                      readOnly && styles.readOnly,
                    )}
                    style={style}
                    onPointerDown={(e) => onPointerDown(e, sq)}
                    onPointerUp={(e) => onPointerUp(e, sq)}
                  >
                    {piece && <span className={styles.piece}>{GLYPH[piece]}</span>}
                    {showPick && sq === botBestTo && (
                      <Tooltip>
                        <TooltipTrigger asChild>
                          <span className={styles.botpick}>★</span>
                        </TooltipTrigger>
                        <TooltipContent>bot&apos;s pick</TooltipContent>
                      </Tooltip>
                    )}
                    {targets.has(sq) && <span className={styles.targetMu}>{formatSubstrateMoveDelta(targetMu.get(sq) ?? 1500)}</span>}
                  </div>
                );
              }),
            )}
            <svg className={styles.overlay} viewBox="0 0 100 100" preserveAspectRatio="none">
              <defs>
                <marker id="uarrowhead" markerWidth="4" markerHeight="4" refX="2.4" refY="2" orient="auto">
                  <path d="M0,0 L4,2 L0,4 Z" />
                </marker>
              </defs>
              {userArrows.map((ar, i) => (
                <polyline
                  key={`${ar.from}${ar.to}${i}`}
                  className={styles.uarrow}
                  markerEnd="url(#uarrowhead)"
                  points={elbow(ar.from, ar.to, flip)}
                />
              ))}
            </svg>
          </div>
        </div>
        <div className={styles.legend}>
          <span><b className={shared.lgStar}>★</b> bot&rsquo;s pick</span>
          <span className={styles.lgScale}><i /> weaker → stronger move</span>
          <span><b>±</b> = substrate move edge vs even (1500 prior)</span>
          <span>eval bar = engine score in pawns (white&rsquo;s view)</span>
          <span>right-drag = arrow · right-click = mark</span>
        </div>
        {footer ? <div className={styles.footer}>{footer}</div> : null}
      </div>
      {drag && (
        <span
          className={styles.dragGhost}
          style={{
            left: drag.x,
            top: drag.y,
            fontSize: `${sqPx * 0.82}px`,
          }}
        >
          {GLYPH[drag.piece]}
        </span>
      )}
    </>
  );
}

export function useBoardRef() {
  return useRef<HTMLDivElement>(null);
}
