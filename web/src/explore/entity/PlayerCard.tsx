import { useEffect, useState } from 'react';
import { Tooltip, TooltipContent, TooltipTrigger } from '@ui';
import { chessPlayer } from '../../chess/db/api';
import type { ChessPlayerResponse } from '../../chess/db/types';
import { entityRecord } from '../../query/api';
import type { EntityRecord } from '../../query/types';
import type { ExploreEntityPreviewResponse } from '../types';
import styles from './PlayerCard.module.css';

/**
 * The entity as the rated competitor it literally is. Glicko-2 rates every one
 * of its edges; this card keeps source Elo and witnessed game records separate
 * from relation-scoped Laplace standing. It also shows
 * the verdict record — confirmed / contested / refuted / thin — from the
 * substrate's canonical epistemic_status logic, never re-derived client-side.
 */
export function PlayerCard({ preview }: { preview: ExploreEntityPreviewResponse }) {
  const [record, setRecord] = useState<EntityRecord | null>(null);
  const [loading, setLoading] = useState(true);
  const [career, setCareer] = useState<ChessPlayerResponse | null>(null);

  useEffect(() => {
    let stale = false;
    setLoading(true);
    setRecord(null);
    setCareer(null);
    if (preview.type === 'Chess_Player') {
      chessPlayer(preview.id_hex, 0)
        .then((r) => { if (!stale) setCareer(r); })
        .catch(() => { /* show unavailable values after a failed read */ })
        .finally(() => { if (!stale) setLoading(false); });
    } else {
      entityRecord(preview.id_hex)
        .then((r) => { if (!stale) setRecord(r); })
        .catch(() => { /* show unavailable values after a failed read */ })
        .finally(() => { if (!stale) setLoading(false); });
    }
    return () => { stale = true; };
  }, [preview.id_hex, preview.type]);

  const pending = loading ? '…' : '—';
  if (preview.type === 'Chess_Player') {
    return (
      <div className={styles.card}>
        <Stat
          value={career?.peak_rating != null ? String(career.peak_rating) : '—'}
          label="peak source Elo"
          hint="highest Elo explicitly tagged by an imported game or official/provider profile"
          accent
        />
        <Stat value={career?.overall.games.toLocaleString() ?? pending} label="games" hint="witnessed games attributed to this player" />
        <Stat value={career?.overall.wins.toLocaleString() ?? pending} label="wins" hint="witnessed scored wins" />
        <Stat value={career?.overall.draws.toLocaleString() ?? pending} label="draws" hint="witnessed scored draws" />
        <Stat value={career?.overall.losses.toLocaleString() ?? pending} label="losses" hint="witnessed scored losses" />
        <Stat value={career?.overall.unscored.toLocaleString() ?? pending} label="unscored" hint="games whose source asserted no result" />
      </div>
    );
  }

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
