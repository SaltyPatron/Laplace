import { Tooltip, TooltipContent, TooltipTrigger } from '@ui';
import type { ChessRecord } from './types';
import styles from './ChessDb.module.css';

/** The chess convention: a win is a point, a draw is half. Null until something is scored. */
export function scoreText(record: ChessRecord): string {
  return record.score == null ? '—' : `${(record.score * 100).toFixed(1)}%`;
}

export function recordText(record: ChessRecord): string {
  return `${record.wins}–${record.losses}–${record.draws}`;
}

/**
 * A record as its own bar: won, drew, lost, in proportion. Unscored games are
 * drawn as a distinct empty segment rather than being hidden or counted as
 * draws — a game the source never scored is an abstention, and the bar says so.
 */
export function RecordBar({ record }: { record: ChessRecord }) {
  const total = record.games || 1;
  const pct = (n: number) => `${(n / total) * 100}%`;

  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <div className={styles.bar} tabIndex={0} aria-label={`W ${record.wins} D ${record.draws} L ${record.losses}`}>
          <span className={styles.barWin} style={{ width: pct(record.wins) }} />
          <span className={styles.barDraw} style={{ width: pct(record.draws) }} />
          <span className={styles.barLoss} style={{ width: pct(record.losses) }} />
          <span className={styles.barUnscored} style={{ width: pct(record.unscored) }} />
        </div>
      </TooltipTrigger>
      <TooltipContent>
        {record.wins} won · {record.draws} drawn · {record.losses} lost
        {record.unscored > 0 ? ` · ${record.unscored} unscored` : ''}
        {' — '}
        {scoreText(record)} over {(record.games - record.unscored).toLocaleString()} scored games
      </TooltipContent>
    </Tooltip>
  );
}

/** W/D/L as three numbers plus the bar — the line that repeats down every table. */
export function RecordCell({ record }: { record: ChessRecord }) {
  return (
    <div className={styles.recordCell}>
      <span className={styles.recordNums}>{recordText(record)}</span>
      <RecordBar record={record} />
    </div>
  );
}

/** The result of one game from one player's side, in the substrate's own enum. */
export function OutcomeChip({ outcome }: { outcome: number | null }) {
  if (outcome == null) return <span className={styles.outcomeNone}>—</span>;
  if (outcome === 2) return <span className={styles.outcomeWin}>W</span>;
  if (outcome === 1) return <span className={styles.outcomeDraw}>D</span>;
  return <span className={styles.outcomeLoss}>L</span>;
}
