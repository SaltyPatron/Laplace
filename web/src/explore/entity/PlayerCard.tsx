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
 * of its edges; this is the card: top rating, games played (evidence rows), and
 * the verdict record — confirmed / contested / refuted / thin — from the
 * substrate's canonical epistemic_status logic, never re-derived client-side.
 */
export function PlayerCard({ preview }: { preview: ExploreEntityPreviewResponse }) {
  const [record, setRecord] = useState<EntityRecord | null>(null);
  const [career, setCareer] = useState<ChessPlayerResponse | null>(null);

  useEffect(() => {
    let stale = false;
    setRecord(null);
    setCareer(null);
    entityRecord(preview.id_hex)
      .then((r) => { if (!stale) setRecord(r); })
      .catch(() => { /* the card stands without the record line */ });
    if (preview.type === 'Chess_Player') {
      chessPlayer(preview.id_hex, 0)
        .then((r) => { if (!stale) setCareer(r); })
        .catch(() => { /* old/unrecorded player: retain the generic entity card */ });
    }
    return () => { stale = true; };
  }, [preview.id_hex, preview.type]);

  const topMu = preview.preview_facts.length
    ? Math.max(...preview.preview_facts.map((f) => Number(f.eff_mu)))
    : null;

  if (preview.type === 'Chess_Player' && career) {
    return (
      <div className={styles.card}>
        <Stat
          value={career.peak_rating != null ? String(career.peak_rating) : '—'}
          label="peak source Elo"
          hint="highest Elo explicitly tagged by an imported game or official/provider profile"
          accent
        />
        <Stat value={career.overall.games.toLocaleString()} label="games" hint="witnessed games attributed to this player" />
        <Stat value={career.overall.wins.toLocaleString()} label="wins" hint="witnessed scored wins" />
        <Stat value={career.overall.draws.toLocaleString()} label="draws" hint="witnessed scored draws" />
        <Stat value={career.overall.losses.toLocaleString()} label="losses" hint="witnessed scored losses" />
        <Stat value={career.overall.unscored.toLocaleString()} label="unscored" hint="games whose source asserted no result" />
      </div>
    );
  }

  return (
    <div className={styles.card}>
      <Stat
        value={topMu != null ? topMu.toFixed(0) : '—'}
        label="strongest edge"
        hint="highest conservative edge score (rating − 2·RD) among this entity's rated relations"
        accent
      />
      <Stat
        value={preview.evidence_count.toLocaleString()}
        label="evidence rows"
        hint="every witnessed assertion involving this entity, with provenance preserved"
      />
      <Stat
        value={record ? String(record.confirmed) : '…'}
        label="confirmed"
        hint="edges the fold rates as settled consensus"
      />
      <Stat
        value={record ? String(record.contested) : '…'}
        label="contested"
        hint="edges with high volatility — the witnesses disagree"
      />
      <Stat
        value={record ? String(record.refuted) : '…'}
        label="refuted"
        hint="edges rated negative — the consensus says no"
      />
      <Stat
        value={record ? String(record.thin) : '…'}
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
