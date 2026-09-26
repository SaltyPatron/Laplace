import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { LoadingText, Muted, Panel } from '@ui';
import { exploreCatalog } from '../api';
import { useExploreStore } from '../store';
import type { ExploreStageRow } from '../types';
import styles from './Browse.module.css';

/**
 * One catalog stage: its law, then its declared sources as cards. A source with a
 * `source_key` links to its source page with its attestation count; one without is shown
 * as not yet ingested.
 */
export function StageBrowse() {
  const { stageId } = useParams();
  const setBreadcrumb = useExploreStore((s) => s.setBreadcrumb);
  const [stage, setStage] = useState<ExploreStageRow | null>(null);

  useEffect(() => {
    const id = decodeURIComponent(stageId ?? '');
    exploreCatalog().then((c) => {
      const hit = c.stages.find((s) => s.stage === id) ?? null;
      setStage(hit);
      if (hit) setBreadcrumb({ stage: hit.stage });
    });
  }, [stageId, setBreadcrumb]);

  if (!stage) return <LoadingText>Loading stage…</LoadingText>;

  return (
    <Panel title={`Stage — ${stage.stage}`}>
      {stage.law ? <Muted className={styles.law}>{stage.law}</Muted> : null}
      <div className={styles.grid}>
        {stage.sources.map((s) => {
          const inner = (
            <>
              <div className={styles.cardHead}>
                <span className={styles.cardName}>{s.cli}</span>
                {s.layer && <span className={styles.layer}>{s.layer}</span>}
              </div>
              {s.role && <span className={styles.role}>{s.role}</span>}
              <span className={styles.count}>
                {s.source_key
                  ? `${(s.evidence ?? 0).toLocaleString()} attestations`
                  : 'not yet ingested'}
              </span>
            </>
          );
          return s.source_key ? (
            <Link key={s.cli + (s.layer ?? '')} className={styles.card}
              to={`/explore/source/${encodeURIComponent(s.source_key)}`}>
              {inner}
            </Link>
          ) : (
            <div key={s.cli + (s.layer ?? '')} className={`${styles.card} ${styles.awaiting}`}>{inner}</div>
          );
        })}
      </div>
    </Panel>
  );
}
