import { Link as RouterLink } from 'react-router-dom';
import { Muted, ReadStatus, useReadResource } from '@ui';
import { queryHomeLeaders } from '../query/api';
import { useAppStore } from '../store';
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
  const { tenant, authUser } = useAppStore();
  const leaders = useReadResource({
    key: JSON.stringify(['home-leaders', tenant, authUser?.id]),
    read: (signal) => queryHomeLeaders({ tenant, signal }),
  });
  const bands = leaders.data?.bands;

  return (
    <section className={styles.leaders} aria-label="League leaders">
      <div className={styles.head}>
        <span className={styles.title}>League leaders</span>
        <Muted className={styles.sub}>strongest consensus per arena — conservative standing and witnesses, live</Muted>
      </div>
      <ReadStatus label="League leaders" resource={leaders} />
      {bands?.length === 0 ? <Muted>No leaders were returned.</Muted> : null}
      <div className={styles.grid}>
        {(bands ?? (leaders.busy ? HOME_BANDS.map(() => null) : [])).map((band, i) => (
          <div key={band?.band ?? i} className={styles.arena}>
            <div className={styles.arenaName}>{band ? band.name.replace(/_/g, ' ') : ' '}</div>
            {band?.rows.length === 0 ? (
              <Muted>No eligible consensus edges in this arena.</Muted>
            ) : band ? (
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
