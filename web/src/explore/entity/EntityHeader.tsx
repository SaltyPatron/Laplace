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
        <PlayerCard preview={preview} />
      </div>
      <div className={styles.actions}>
        {/*
          The substrate view and the chess view are two readings of one row, not
          two copies, so an entity the chess modality knows how to present offers
          the crossing explicitly — the same content hash, read as a career or as
          a game instead of as an entity.
        */}
        {chessRoute(preview.type) ? (
          <Button asChild>
            <RouterLink to={`${chessRoute(preview.type)}/${preview.id_hex}`}>
              {preview.type === 'Chess_Game' ? 'View as game' : 'View as player'}
            </RouterLink>
          </Button>
        ) : null}
        <Button asChild>
          <RouterLink to={`/explore/mesh/${preview.id_hex}`}>View in mesh</RouterLink>
        </Button>
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
