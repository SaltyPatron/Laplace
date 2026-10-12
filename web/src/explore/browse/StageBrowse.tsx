import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { useRouteText } from '../../layout/useRouteText';
import { ErrorText, LoadingText, Muted, Panel } from '@ui';
import { describeFailure } from '../../api/client';
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
  const stageId = useRouteText('/explore/stage/:stageId', 'stageId');
  const setBreadcrumb = useExploreStore((s) => s.setBreadcrumb);
  const [stage, setStage] = useState<ExploreStageRow | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let stale = false;
    const id = stageId ?? '';
    setLoading(true); setStage(null); setError(null);
    exploreCatalog().then((c) => {
      if (stale) return;
      const hit = c.stages.find((s) => s.stage === id) ?? null;
      setStage(hit);
      if (hit) setBreadcrumb({ stage: hit.stage });
    }).catch((failure) => {
      if (!stale) setError(describeFailure(failure).message);
    }).finally(() => { if (!stale) setLoading(false); });
    return () => { stale = true; };
  }, [stageId, setBreadcrumb]);

  if (error) return <ErrorText role="alert">{error}</ErrorText>;
  if (loading) return <LoadingText>Loading stage…</LoadingText>;
  if (!stage) return <Muted>No catalog stage matches “{stageId}”.</Muted>;

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
