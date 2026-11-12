import { useState } from 'react';
import { WorldLink as RouterLink } from '../../world/WorldLink';
import { Button, Muted } from '@ui';
import type { ExploreEntityPreviewResponse } from '../types';
import { PlayerCard } from './PlayerCard';
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
  const [inspectionOpen, setInspectionOpen] = useState(false);
  return (
    <header className={styles.header}>
      <div className={styles.titleBlock}>
        <h2>{preview.label}</h2>
        {preview.type && <Muted>{preview.type.replaceAll('_', ' ')}</Muted>}
        <Button variant="ghost" size="sm" onClick={onCopyId}>{copied ? 'Reference copied' : 'Copy reference'}</Button>
      </div>
      <div className={styles.actions}>
        {/* A recorded game's page is another presentation of the same record id. */}
        {preview.type === 'Chess_Game' ? (
          <Button asChild>
            <RouterLink to={`/chess/games/${preview.id_hex}`}>View as game</RouterLink>
          </Button>
        ) : null}
        <Button asChild variant="ghost" size="sm">
          <RouterLink to={`/explore/entity/${preview.id_hex}`}>Full entity</RouterLink>
        </Button>
        <Button asChild variant="ghost" size="sm">
          <RouterLink to={`/proof?q=${encodeURIComponent(preview.label)}`}>Prove storage</RouterLink>
        </Button>
        <details className={styles.sourceEvidence} onToggle={event => setInspectionOpen(event.currentTarget.open)}>
          <summary>Inspect and export</summary>
          <Muted>Composition tier {preview.tier ?? '—'}</Muted>
          {inspectionOpen && <PlayerCard preview={preview} />}
        <Button asChild>
          <RouterLink to={`/explore/mesh/${preview.id_hex}`}>View in mesh</RouterLink>
        </Button>
        <Button asChild>
          <RouterLink to={`/proof?q=${encodeURIComponent(preview.label)}`}>
            Prove storage
          </RouterLink>
        </Button>
        <Button disabled={!unlocked || exportBusy} loading={exportBusy} onClick={onExport}>
          Export for training
        </Button>
        </details>
        <Button variant="ghost" onClick={onAskSubstrate}>
          Ask substrate
        </Button>
      </div>
    </header>
  );
}
