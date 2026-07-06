import { useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import { exploreCatalog } from '../api';
import { SearchBar } from '../components/SearchBar';
import { useExploreStore } from '../store';
import type { ExploreSourceRow } from '../types';

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

  if (!source) return <p className="muted">Loading source…</p>;

  return (
    <div>
      <h2>{source.key}</h2>
      <p className="muted">Stage {source.stage ?? '—'} · Layer {source.layer ?? '—'}</p>
      <div className="stat-grid compact">
        <div><strong>{source.evidence.toLocaleString()}</strong> attestations</div>
        <div><strong>{source.content.toLocaleString()}</strong> content entities</div>
      </div>
      {source.role ? <p>{source.role}</p> : null}
      <SearchBar placeholder={`Search within ${source.key}…`} />
    </div>
  );
}
