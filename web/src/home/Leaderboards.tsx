import { useEffect, useState } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { Muted } from '@ui';
import { queryHomeLeaders } from '../query/api';
import type { BandLeaders } from '../query/types';
import styles from './Leaderboards.module.css';

/** Salience bands whose leaders the landing shows. */
const HOME_BANDS = [1, 2, 4, 5];

/**
 * Band leaders: the highest-standing consensus cells in each salience band, read
 * live from /v1/query/leaders/home. Each row links to its subject entity in Explore.
 *
 * Rows carry conservative standing (rating - 2*RD) and witness count; they are
 * labelled as such, never as a rating or as games, since relation witnesses come
 * from any admitted source or modality.
 */
export function Leaderboards() {
  const [bands, setBands] = useState<BandLeaders[] | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    queryHomeLeaders()
      .then((r) => setBands(r.bands ?? []))
      .catch(() => setFailed(true));
  }, []);

  if (failed) return null; // the landing renders without leaders

  return (
    <section className={styles.leaders} aria-label="League leaders">
      <div className={styles.head}>
        <span className={styles.title}>League leaders</span>
        <Muted className={styles.sub}>strongest consensus per arena — conservative standing and witnesses, live</Muted>
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
                      <RouterLink
                        className={styles.subject}
                        to={`/explore/entity/${row.subject_id}`}
                        title={row.subject}
                      >
                        {row.subject}
                      </RouterLink>
                      <span className={styles.relation} title={row.relation.replace(/_/g, ' ')}>
                        {row.relation.replace(/_/g, ' ').toLowerCase()}
                      </span>
                      <span className={styles.object} title={row.object}>{row.object}</span>
                    </span>
                    <span className={styles.stat}>
                      <span
                        className={styles.mu}
                        title="Conservative standing (rating − 2×RD); not the underlying rating"
                      >
                        {row.eff_mu.toFixed(0)}
                      </span>
                      <span className={styles.wit} title="Witnessed observations">
                        {row.witnesses.toLocaleString()} wit
                      </span>
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
