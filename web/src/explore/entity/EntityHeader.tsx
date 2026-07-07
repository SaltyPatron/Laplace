import { Button, ConsensusBadge, Muted } from '@ui';
import type { ExploreEntityPreviewResponse } from '../types';
import styles from './EntityDetail.module.css';

export function EntityHeader({
  preview,
  copied,
  unlocked,
  exportBusy,
  onCopyId,
  onExport,
  onAskSubstrate,
}: {
  preview: ExploreEntityPreviewResponse;
  copied: boolean;
  unlocked: boolean;
  exportBusy: boolean;
  onCopyId: () => void;
  onExport: () => void;
  onAskSubstrate: () => void;
}) {
  return (
    <header className={styles.header}>
      <div className={styles.titleBlock}>
        <h2>{preview.label}</h2>
        <Muted className={styles.meta}>
          {preview.id_hex} · tier {preview.tier ?? '—'} · {preview.type ?? 'unknown'}{' '}
          <Button variant="ghost" size="sm" onClick={onCopyId}>
            {copied ? 'Copied' : 'Copy id'}
          </Button>
        </Muted>
        <ConsensusBadge witnesses={preview.evidence_count} tone="explore" />
      </div>
      <div className={styles.actions}>
        <Button disabled={!unlocked || exportBusy} loading={exportBusy} onClick={onExport}>
          Export for training
        </Button>
        <Button variant="ghost" onClick={onAskSubstrate}>
          Ask substrate
        </Button>
      </div>
    </header>
  );
}
