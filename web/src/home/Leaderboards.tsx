import { useEffect, useState } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { Muted } from '@ui';
import { queryLeaders } from '../query/api';
import type { BandLeaders, LeaderRow, RealizationKind } from '../query/types';
import styles from './Leaderboards.module.css';

/** The content bands shown on the landing — the arenas with real semantics. */
const HOME_BANDS = [1, 2, 4, 5];

function realizedTerm(
  label: string,
  realization: RealizationKind | null | undefined,
  technicalName: string | null | undefined,
  id: string,
) {
  const human = realization === 'content' || realization === 'name' || !realization;
  return {
    text: human ? label : (technicalName || label),
    technical: !human,
    title: technicalName
      ? `${technicalName} · ${realization ?? 'legacy'} · ${id}`
      : `${realization ?? 'legacy'} · ${id}`,
  };
}

function standingDetail(row: LeaderRow) {
  const parts: string[] = [];
  if (row.rating != null) parts.push(`μ ${row.rating.toFixed(0)}`);
  if (row.rd != null) parts.push(`RD ${row.rd.toFixed(0)}`);
  parts.push(`${row.witnesses.toLocaleString()} wit`);
  return parts.join(' · ');
}

/**
 * League leaders. Ranking, labels, realization state and Glicko coordinates are
 * substrate-owned.  This component does not infer language/kind from strings and
 * does not reinterpret technical names as content.
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
        <Muted className={styles.sub}>strongest consensus per arena — conservative standing and witnesses, live</Muted>
      </div>

      <div className={styles.grid}>
        {(bands ?? HOME_BANDS.map(() => null)).map((band, i) => (
          <div key={band?.band ?? i} className={styles.arena}>
            <div className={styles.arenaName}>{band ? band.name : ' '}</div>
            {band ? (
              <ol className={styles.rows}>
                {band.rows.map((row, rank) => {
                  const subject = realizedTerm(
                    row.subject,
                    row.subject_realization,
                    row.subject_technical_name,
                    row.subject_id,
                  );
                  const object = realizedTerm(
                    row.object,
                    row.object_realization,
                    row.object_technical_name,
                    row.object_id,
                  );
                  const relationTitle = [
                    row.relation_technical_name,
                    row.relation_realization,
                    row.relation_id,
                  ].filter(Boolean).join(' · ');

                  return (
                    <li key={`${row.subject_id}-${row.relation_id ?? row.relation}-${row.object_id}-${rank}`} className={styles.row}>
                      <span className={styles.rank}>{rank + 1}</span>
                      <span className={styles.edge}>
                        <RouterLink
                          className={`${styles.subject} ${subject.technical ? styles.technical : ''}`}
                          to={`/explore/entity/${row.subject_id}`}
                          title={subject.title}
                        >
                          {subject.text}
                        </RouterLink>
                        <span className={styles.relation} title={relationTitle || row.relation}>
                          {row.relation}
                        </span>
                        <span
                          className={`${styles.object} ${object.technical ? styles.technical : ''}`}
                          title={object.title}
                        >
                          {object.text}
                        </span>
                      </span>
                      <span className={styles.stat}>
                        <span
                          className={styles.mu}
                          title="Conservative standing = rating − 2×RD; not the underlying rating"
                        >
                          <span className={styles.statLabel}>standing</span>{' '}{row.eff_mu.toFixed(0)}
                        </span>
                        <span className={styles.wit} title="Rating, rating deviation, and witnessed observations">
                          {standingDetail(row)}
                        </span>
                      </span>
                    </li>
                  );
                })}
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
