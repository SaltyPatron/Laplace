import { useState } from 'react';
import { cn, Muted } from '@ui';
import { GLYPH, parseBoard, sqName } from '../play/Board';
import styles from './LiveBoard.module.css';

export interface LabBoardState {
  game: number;
  ply: number;
  uci: string;
  fen: string;
  white?: string | null;
  black?: string | null;
}

export function LiveBoard({ boards, lastGame }: { boards: Record<number, LabBoardState>; lastGame: number | null }) {
  const [pinned, setPinned] = useState<number | null>(null);
  const games = Object.keys(boards).map(Number).sort((a, b) => a - b);
  const shownGame = pinned !== null && boards[pinned] ? pinned : lastGame;
  const state = shownGame !== null ? boards[shownGame] : undefined;
  if (!state) return null;

  const board = parseBoard(state.fen);
  const from = state.uci.slice(0, 2);
  const to = state.uci.slice(2, 4);

  return (
    <div className={styles.wrap}>
      {games.length > 1 && (
        <div className={styles.picker} aria-label="Live games">
          <button
            type="button"
            className={cn(styles.chip, pinned === null && styles.chipActive)}
            aria-pressed={pinned === null}
            onClick={() => setPinned(null)}
          >
            follow
          </button>
          {games.slice(-8).map((g) => (
            <button
              key={g}
              type="button"
              className={cn(styles.chip, pinned === g && styles.chipActive)}
              aria-pressed={pinned === g}
              onClick={() => setPinned((p) => (p === g ? null : g))}
            >
              #{g}
            </button>
          ))}
        </div>
      )}
      <div className={styles.meta}>
        <span>game <b>{state.game}</b> · ply <b>{state.ply}</b> · <span className={styles.uci}>{state.uci}</span></span>
        {(state.white || state.black) && (
          <Muted>{state.white ?? '?'} (W) vs {state.black ?? '?'} (B)</Muted>
        )}
      </div>
      <div className={styles.board} aria-label={`Game ${state.game} position after ply ${state.ply}`}>
        {board.map((row, r) =>
          row.map((piece, f) => {
            const sq = sqName(f, r);
            const dark = (f + (8 - r)) % 2 === 1;
            return (
              <div
                key={sq}
                className={cn(styles.square, dark ? styles.dark : styles.light, (sq === from || sq === to) && styles.lastMove)}
              >
                {piece && <span className={styles.piece}>{GLYPH[piece]}</span>}
              </div>
            );
          }),
        )}
      </div>
    </div>
  );
}
