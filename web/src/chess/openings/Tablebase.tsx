import { Muted, ReadStatus, useReadResource } from '@ui';
import { pieceCount, positionTablebase, TABLEBASE_PIECES, type Wdl } from './api';
import styles from './Openings.module.css';

const OUTCOME: Record<string, string> = {
  win: 'wins', 'cursed-win': 'wins, but not within the 50-move rule', draw: 'draws',
  'blessed-loss': 'loses, but saved by the 50-move rule', loss: 'loses',
};
/** The verdict for the side whose view it is, in words; an unknown verdict is shown as served. */
const words = (wdl: Wdl) => OUTCOME[wdl] ?? wdl;
/** A move's verdict is from the opponent's view after it; the mover's result is its inverse. */
const INVERSE: Record<string, string> = {
  win: 'loss', 'cursed-win': 'blessed-loss', draw: 'draw', 'blessed-loss': 'cursed-win', loss: 'win',
};
const forMover = (wdl: Wdl) => INVERSE[wdl] ?? wdl;

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
  if (!eligible) return null;
  const toMove = fen.split(' ')[1] === 'b' ? 'Black' : 'White';
  const verdict = read.data;
  return (
    <section className={styles.panel} aria-label="Endgame tablebase">
      <h3 className={styles.heading}>Endgame tablebase</h3>
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
                <strong>{move.san}</strong> <Muted>{words(forMover(move.wdl!))}{move.dtz != null && move.dtz !== 0 ? `, ${Math.abs(move.dtz)}` : ''}</Muted>
              </li>
            ))}
          </ul>
        </>
      ))}
    </section>
  );
}
