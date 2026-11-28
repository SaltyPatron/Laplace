import { Muted, ReadStatus, useReadResource } from '@ui';
import { pieceCount, positionTablebase, TABLEBASE_PIECES, type Wdl } from './api';
import styles from './Openings.module.css';

const OUTCOME: Record<string, string> = {
  win: 'wins', 'cursed win': 'wins, but not within the 50-move rule', draw: 'draws',
  'blessed loss': 'loses, but saved by the 50-move rule', loss: 'loses',
};
/** The verdict for the side to move (or mover, for a move), in words; an unknown verdict is shown as served. */
const words = (wdl: Wdl) => OUTCOME[wdl] ?? wdl;

/**
 * Endgame verdicts recorded from Syzygy on this position: whether the side to move wins,
 * draws or loses with best play, and the best moves with theirs. Asked only when the board
 * holds few enough pieces for a table; positions with no recorded table say so.
 */
export function PositionTablebase({ fen }: { fen: string }) {
  const pieces = pieceCount(fen);
  const eligible = pieces <= TABLEBASE_PIECES;
  const read = useReadResource({
    key: JSON.stringify(['chess-position-tablebase', fen]),
    enabled: eligible,
    read: (signal) => positionTablebase(fen, signal),
  });
  if (!eligible) {
    return (
      <section className={styles.panel} aria-label="Endgame tablebase">
        <h3 className={styles.heading}>Endgame tablebase (Syzygy WDL/DTZ)</h3>
        <Muted>{pieces} pieces on board — Syzygy WDL/DTZ probes activate at &le; {TABLEBASE_PIECES} pieces.</Muted>
      </section>
    );
  }
  const toMove = fen.split(' ')[1] === 'b' ? 'Black' : 'White';
  const verdict = read.data;
  return (
    <section className={styles.panel} aria-label="Endgame tablebase">
      <h3 className={styles.heading}>Endgame tablebase (Syzygy WDL/DTZ)</h3>
      <ReadStatus label="Tablebase" resource={read} />
      {verdict && (verdict.wdl == null ? (
        <Muted>No tablebase verdict is recorded for this {pieces}-piece position.</Muted>
      ) : (
        <>
          <p className={styles.primary}>
            {toMove} to move {words(verdict.wdl)} with best play
            {verdict.dtz != null && verdict.dtz !== 0 ? <Muted> (distance to zeroing {Math.abs(verdict.dtz)})</Muted> : null}.
          </p>
          <ul className={styles.broader} aria-label="Best moves by tablebase verdict">
            {verdict.moves.filter((move) => move.wdl != null).slice(0, 5).map((move) => (
              <li key={move.position}>
                <strong>{move.san}</strong> <Muted>{words(move.wdl!)}{move.dtz != null && move.dtz !== 0 ? `, ${Math.abs(move.dtz)}` : ''}</Muted>
              </li>
            ))}
          </ul>
        </>
      ))}
    </section>
  );
}
