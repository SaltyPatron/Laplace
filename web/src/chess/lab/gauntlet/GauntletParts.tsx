/** Gauntlet watch column (score, substrate proof, live board) plus the Stat tile and job-state class helper. */
import { Chip, Muted, Panel } from '@ui';
import { LiveBoard, type LabBoardState } from '../LiveBoard';
import type { LabJob } from '../sse';
import styles from './GauntletView.module.css';

export function GauntletWatch({
  metrics,
  pct,
  done,
  total,
  active,
  readbackVerified,
  boards,
  lastGame,
}: {
  metrics: Record<string, number>;
  pct: number;
  done: number;
  total: number;
  active: LabJob | null;
  readbackVerified: boolean;
  boards: Record<number, LabBoardState>;
  lastGame: number | null;
}) {
  return (
        <div className={styles.watch}>
          <Panel title="Score">
            <div className={styles.scoreboard}>
              <Stat label="Laplace wins" value={metrics.wins} tone="win" />
              <Stat label="Draws" value={metrics.draws} />
              <Stat label="Losses" value={metrics.losses} tone="loss" />
              <Stat
                label="Elo diff"
                value={metrics.elo_diff}
                // cutechess prints "inf"/"nan" until each side has won at least once, and a
                // non-finite Elo is not a number to render as one.
                title="Undefined until both engines have won a game"
                format={(v) => `${v >= 0 ? '+' : ''}${v.toFixed(0)}`}
              />
            </div>
            <div className={styles.progressWrap} aria-label="Games played">
              <div className={styles.progressBar} style={{ width: `${pct}%` }} />
              <span className={styles.progressLabel}>
                {done} / {total} games{active?.summary.message ? ` · ${active.summary.message}` : ''}
              </span>
            </div>
            {active?.artifacts?.['games.pgn'] && (
              <div className={styles.artifact}>
                <a href={`/chess/lab/jobs/${active.id}/artifact/games.pgn`} download>games.pgn</a>
                <Muted>
                  {metrics.games_ingested !== undefined
                    ? `${metrics.games_ingested} games ingested to substrate`
                    : 'not yet ingested'}
                </Muted>
              </div>
            )}
          </Panel>

          <Panel title="Laplace substrate proof">
            <div className={styles.scoreboard}>
              <Stat label="Games ingested" value={metrics.games_ingested} />
              <Stat label="Games read back" value={metrics.games_committed_readback} />
              <Stat label="Plies read back" value={metrics.plies_committed_readback} />
              <Stat label="Readback seconds" value={metrics.readback_seconds} format={(v) => v.toFixed(3)} />
            </div>
            <section className={styles.readiness} aria-label="Substrate persistence proof">
              <Chip variant={readbackVerified ? 'engineOk' : 'engineMissing'}>
                persisted database readback {readbackVerified ? '✓' : 'not demonstrated'}
              </Chip>
            </section>
            <Muted>
              This proof comes from the completed gauntlet's ingest commit followed by PostgreSQL
              readback. The transcript below is the spawned engines' real UCI traffic; filter it for
              “providers” to see Laplace's per-search substrate-provider receipt.
            </Muted>
          </Panel>

          <Panel title="Live board" className={styles.boardPanel}>
            {lastGame !== null
              ? <LiveBoard boards={boards} lastGame={lastGame} />
              : <Muted>The board fills in from the engines' own UCI traffic once a game starts.</Muted>}
          </Panel>
        </div>
  );
}

export function Stat({
  label,
  value,
  tone,
  title,
  format,
}: {
  label: string;
  value: number | undefined;
  tone?: 'win' | 'loss';
  title?: string;
  format?: (v: number) => string;
}) {
  return (
    <div className={styles.stat} title={value === undefined ? title : undefined}>
      <span className={styles.statLabel}>{label}</span>
      <b className={[styles.statValue, tone === 'win' && styles.statWin, tone === 'loss' && styles.statLoss]
        .filter(Boolean).join(' ')}>
        {value === undefined ? '—' : format ? format(value) : value}
      </b>
    </div>
  );
}

const STATE_CLASS: Record<string, string> = {
  Running: styles.stateRunning,
  Pending: styles.stateRunning,
  Completed: styles.stateCompleted,
  Failed: styles.stateFailed,
  Cancelled: styles.stateCancelled,
};

export function stateClass(state: string): string {
  return [styles.state, STATE_CLASS[state]].filter(Boolean).join(' ');
}
