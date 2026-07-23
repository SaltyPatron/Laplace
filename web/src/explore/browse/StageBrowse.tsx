import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { LoadingText, Muted, Panel } from '@ui';
import { exploreCatalog } from '../api';
import { useExploreStore } from '../store';
import type { ExploreStageRow } from '../types';
import styles from './Browse.module.css';

/**
 * A cadence stage as a division landing: its law, then its sources as cards —
 * each the drill-down into a live franchise page when ingested, and an honest
 * dashed "not yet ingested" card when the cadence declares it but the substrate
 * doesn't hold it yet.
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
