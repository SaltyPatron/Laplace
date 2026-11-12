import { useEffect, useState } from 'react';
import { Button, ErrorText, Tooltip, TooltipContent, TooltipTrigger } from '@ui';
import { describeFailure } from '../../api/client';
import { entityRecord } from '../../query/api';
import type { EntityRecord } from '../../query/types';
import type { ExploreEntityPreviewResponse } from '../types';
import styles from './PlayerCard.module.css';

/**
 * The entity's standing card: the record read, its Glicko-2 standing over its relations
 * and its verdict counts (confirmed / contested / refuted / thin) as the server's
 * epistemic_status computes them.
 */
export function PlayerCard({ preview }: { preview: ExploreEntityPreviewResponse }) {
  const [record, setRecord] = useState<EntityRecord | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let stale = false;
    setLoading(true);
    setRecord(null);
    setError(null);
    entityRecord(preview.id_hex)
      .then((r) => { if (!stale) setRecord(r); })
      .catch((failure: unknown) => { if (!stale) setError(describeFailure(failure).message); })
      .finally(() => { if (!stale) setLoading(false); });
    return () => { stale = true; };
  }, [preview.id_hex, attempt]);

  if (error) return <div>
    <ErrorText>Standing could not be loaded: {error}</ErrorText>
    <Button onClick={() => setAttempt((value) => value + 1)}>Retry standing</Button>
  </div>;

  const pending = loading ? '…' : '—';
  return (
    <div className={styles.card}>
      <Stat
        value={preview.evidence_count.toLocaleString()}
        label="evidence rows"
        hint="every witnessed assertion involving this entity, with provenance preserved"
      />
      <Stat
        value={record ? String(record.confirmed) : pending}
        label="confirmed"
        hint="edges the fold rates as settled consensus"
      />
      <Stat
        value={record ? String(record.contested) : pending}
        label="contested"
        hint="edges with high volatility — the witnesses disagree"
      />
      <Stat
        value={record ? String(record.refuted) : pending}
        label="refuted"
        hint="edges rated negative — the consensus says no"
      />
      <Stat
        value={record ? String(record.thin) : pending}
        label="thin"
        hint="edges with too few witnesses to settle — wide RD, rookie sample"
      />
    </div>
  );
}

function Stat({ value, label, hint, accent }: { value: string; label: string; hint: string; accent?: boolean }) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <div className={styles.stat} tabIndex={0}>
          <span className={`${styles.value} ${accent ? styles.accent : ''}`}>{value}</span>
          <span className={styles.label}>{label}</span>
        </div>
      </TooltipTrigger>
      <TooltipContent>{hint}</TooltipContent>
    </Tooltip>
  );
}
