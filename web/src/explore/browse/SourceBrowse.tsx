import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { ErrorText, LoadingText, Muted, Panel, Stack } from '@ui';
import { exploreCatalog, exploreSourceRoster } from '../api';
import { SearchBar } from '../components/SearchBar';
import { StatCard } from '../components/StatCard';
import { useExploreStore } from '../store';
import type { ExploreSourceRow, SourceRosterRow } from '../types';
import styles from '../catalog/WarehouseHome.module.css';
import browse from './Browse.module.css';

/**
 * A source as a franchise page: who this witness is (stage, layer, role), its
 * scale, and its roster — a bounded sample of what it actually witnessed, every
 * name a drill into that entity. A franchise page without a roster was the gap:
 * you could see a source's row count but not one thing it said.
 */
export function SourceBrowse() {
  const { sourceKey } = useParams();
  const setBreadcrumb = useExploreStore((s) => s.setBreadcrumb);
  const [source, setSource] = useState<ExploreSourceRow | null>(null);
  const [missing, setMissing] = useState(false);
  const [roster, setRoster] = useState<SourceRosterRow[] | null>(null);
  const [rosterError, setRosterError] = useState<string | null>(null);

  useEffect(() => {
    const key = decodeURIComponent(sourceKey ?? '');
    let stale = false;
    exploreCatalog().then((c) => {
      if (stale) return;
      const hit = c.sources.find((s) => s.key === key) ?? null;
      setSource(hit);
      setMissing(!hit);
      if (hit?.stage) setBreadcrumb({ stage: hit.stage, source: hit.key });
      if (hit?.id_hex) {
        exploreSourceRoster(hit.id_hex, 40)
          .then((r) => { if (!stale) setRoster(r.rows); })
          .catch((e) => { if (!stale) setRosterError(e instanceof Error ? e.message : String(e)); });
      }
    });
    return () => { stale = true; };
  }, [sourceKey, setBreadcrumb]);

  if (missing) return <ErrorText>No live source named “{decodeURIComponent(sourceKey ?? '')}”.</ErrorText>;
  if (!source) return <LoadingText>Loading source…</LoadingText>;

  return (
    <Stack gap={4}>
      <Panel title={source.key}>
        <Muted>Stage {source.stage ?? '—'} · Layer {source.layer ?? '—'}</Muted>
        <div className={styles.statGrid} style={{ gridTemplateColumns: 'repeat(2, 1fr)', marginTop: '0.75rem' }}>
          <StatCard label="Attestations" value={source.evidence.toLocaleString()} />
          <StatCard label="Content entities" value={source.content?.toLocaleString() ?? "—"} />
        </div>
        {source.role ? <Muted>{source.role}</Muted> : null}
        <SearchBar placeholder={`Search within ${source.key}…`} />
      </Panel>

      <Panel title="Roster — what this witness asserts">
        <Muted style={{ marginBottom: '0.5rem' }}>
          a bounded sample of its testimony; every name drills into the entity
        </Muted>
        {rosterError ? (
          <ErrorText>{rosterError}</ErrorText>
        ) : !source.id_hex ? (
          <Muted>No live id for this source — roster unavailable.</Muted>
        ) : roster === null ? (
          <LoadingText>Sampling testimony…</LoadingText>
        ) : roster.length === 0 ? (
          <Muted>Nothing witnessed by this source yet.</Muted>
        ) : (
          <ul style={{ listStyle: 'none', margin: 0, padding: 0 }}>
            {roster.map((r, i) => (
              <li key={i} className={browse.rosterRow}>
                <Link className={browse.rosterSubject} to={`/explore/entity/${r.subject_id}`}>{r.subject}</Link>
                <span className={browse.rosterRel}>{r.relation.replace(/_/g, ' ').toLowerCase()}</span>
                <Link className={browse.rosterObject} to={`/explore/entity/${r.object_id}`}>{r.object}</Link>
                <span className={browse.rosterObs}>{r.observations}×</span>
              </li>
            ))}
          </ul>
        )}
      </Panel>
    </Stack>
  );
}
