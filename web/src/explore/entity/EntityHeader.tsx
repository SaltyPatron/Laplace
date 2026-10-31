import { useState } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { Button, Muted } from '@ui';
import type { ExploreEntityPreviewResponse } from '../types';
import { PlayerCard } from './PlayerCard';
import styles from './EntityDetail.module.css';

/** The chess pages that can present this entity type, if any. */
function chessRoute(type: string | null | undefined): string | null {
  if (type === 'Chess_Player') return '/chess/players';
  if (type === 'Chess_Game') return '/chess/games';
  return null;
}

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
        {preview.type === 'Chess_Player' && <PlayerCard preview={preview} />}
      </div>
      <div className={styles.actions}>
        {/*
          A chess player or game page is another presentation of this same entity:
          the link carries the same content hash.
        */}
        {chessRoute(preview.type) ? (
          <Button asChild>
            <RouterLink to={`${chessRoute(preview.type)}/${preview.id_hex}`}>
              {preview.type === 'Chess_Game' ? 'View as game' : 'View as player'}
            </RouterLink>
          </Button>
        ) : null}
        <details className={styles.sourceEvidence} onToggle={event => setInspectionOpen(event.currentTarget.open)}>
          <summary>Inspect and export</summary>
          <Muted>Composition tier {preview.tier ?? '—'}</Muted>
          {inspectionOpen && preview.type !== 'Chess_Player' && <PlayerCard preview={preview} />}
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
