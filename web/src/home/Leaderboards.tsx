import { useEffect, useState } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { Muted } from '@ui';
import { queryLeaders } from '../query/api';
import type { BandLeaders } from '../query/types';
import styles from './Leaderboards.module.css';

/** The content bands shown on the landing — the arenas with real semantics. */
const HOME_BANDS = [1, 2, 4, 5];

/**
 * League leaders. Every consensus edge is a rated competitor in its arena — a
 * salience band — so the landing shows who's on top of each, live, exactly the
 * way a sports front page leads with the leaderboard rather than attendance
 * totals. Each row links to the subject entity in Explore.
 */
export function Leaderboards() {
  const [bands, setBands] = useState<BandLeaders[] | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    queryLeaders(HOME_BANDS, 5)
      .then((r) => setBands(r.bands ?? []))
      .catch(() => setFailed(true));
  }, []);

  if (failed) return null; // the landing stands without it; no error chrome here

  return (
    <section className={styles.leaders} aria-label="League leaders">
      <div className={styles.head}>
        <span className={styles.title}>League leaders</span>
        <Muted className={styles.sub}>strongest consensus per arena — rating and games, live</Muted>
      </div>

      <div className={styles.grid}>
        {(bands ?? HOME_BANDS.map(() => null)).map((band, i) => (
          <div key={band?.band ?? i} className={styles.arena}>
            <div className={styles.arenaName}>{band ? band.name.replace(/_/g, ' ') : ' '}</div>
            {band ? (
              <ol className={styles.rows}>
                {band.rows.map((row, rank) => (
                  <li key={`${row.subject_id}-${rank}`} className={styles.row}>
                    <span className={styles.rank}>{rank + 1}</span>
                    <span className={styles.edge}>
                      <RouterLink className={styles.subject} to={`/explore/entity/${row.subject_id}`}>
                        {row.subject}
                      </RouterLink>
                      <span className={styles.relation}>{row.relation.replace(/_/g, ' ').toLowerCase()}</span>
                      <span className={styles.object} title={row.object}>{row.object}</span>
                    </span>
                    <span className={styles.stat}>
                      <span className={styles.mu}>{row.eff_mu.toFixed(0)}</span>
                      <span className={styles.wit}>{row.witnesses}g</span>
                    </span>
                  </li>
                ))}
              </ol>
            ) : (
              <div className={styles.loading} aria-hidden="true" />
            )}
          </div>
        ))}
      </div>
    </section>
  );
}
