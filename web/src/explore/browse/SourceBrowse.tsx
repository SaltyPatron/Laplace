import { useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import { LoadingText, Muted, Panel, Stack } from '@ui';
import { exploreCatalog } from '../api';
import { SearchBar } from '../components/SearchBar';
import { StatCard } from '../components/StatCard';
import { useExploreStore } from '../store';
import type { ExploreSourceRow } from '../types';
import styles from '../catalog/WarehouseHome.module.css';

export function SourceBrowse() {
  const { sourceKey } = useParams();
  const setBreadcrumb = useExploreStore((s) => s.setBreadcrumb);
  const [source, setSource] = useState<ExploreSourceRow | null>(null);

  useEffect(() => {
    const key = decodeURIComponent(sourceKey ?? '');
    exploreCatalog().then((c) => {
      const hit = c.sources.find((s) => s.key === key) ?? null;
      setSource(hit);
      if (hit?.stage) setBreadcrumb({ stage: hit.stage, source: hit.key });
    });
  }, [sourceKey, setBreadcrumb]);

  if (!source) return <LoadingText>Loading source…</LoadingText>;

  return (
    <Stack gap={4}>
      <Panel title={source.key}>
        <Muted>Stage {source.stage ?? '—'} · Layer {source.layer ?? '—'}</Muted>
        <div className={styles.statGrid} style={{ gridTemplateColumns: 'repeat(2, 1fr)', marginTop: '0.75rem' }}>
          <StatCard label="Attestations" value={source.evidence.toLocaleString()} />
          <StatCard label="Content entities" value={source.content.toLocaleString()} />
        </div>
        {source.role ? <Muted>{source.role}</Muted> : null}
        <SearchBar placeholder={`Search within ${source.key}…`} />
      </Panel>
    </Stack>
  );
}
