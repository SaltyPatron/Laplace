import { useEffect, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { Button, ErrorText, Muted, Panel, ReadStatus, Stack, useReadResource } from '@ui';
import { ResultWorkspace, type ResultColumn } from '../../ui/composites/ResultWorkspace/ResultWorkspace';
import { captureRows } from '../../ui/lib/resultRows';
import { useAppStore } from '../../store';
import { exploreCatalog, exploreSourceRoster } from '../api';
import { StatCard } from '../components/StatCard';
import { useExploreStore } from '../store';
import type { ExploreSourceRow, SourceRosterRow } from '../types';
import styles from '../catalog/WarehouseHome.module.css';

/** Catalog, selected source and sampled testimony have separate read scopes. */
export function SourceBrowse() {
  const { sourceKey = '' } = useParams(); // Router already decoded this segment.
  const { tenant, quoteId, authUser } = useAppStore();
  return <SourceWorkspace key={JSON.stringify([tenant, authUser?.id, sourceKey])} sourceKey={sourceKey} tenant={tenant} quoteId={quoteId} />;
}
function SourceWorkspace({ sourceKey, tenant, quoteId }: { sourceKey: string; tenant: string; quoteId: string }) {
  const setBreadcrumb = useExploreStore((state) => state.setBreadcrumb);
  const catalog = useReadResource({
    key: JSON.stringify(['source-catalog', tenant, quoteId]),
    read: (signal) => exploreCatalog({ tenant, quoteId, signal }),
  });
  const source = catalog.data?.sources.find((item) => item.key === sourceKey);
  useEffect(() => {
    setBreadcrumb({ stage: source?.stage ?? undefined, source: sourceKey });
  }, [source?.stage, sourceKey, setBreadcrumb]);
  return <Stack gap={4}>
    <Panel title={sourceKey || 'Source'} actions={<Button variant="ghost" onClick={() => void catalog.refresh()}>Refresh source</Button>}>
      <ReadStatus label="Source catalog" resource={catalog} />
      {catalog.data && !source && <><ErrorText>No source named “{sourceKey}” was returned by this catalog.</ErrorText><Link to="/explore/warehouse">Browse available sources</Link></>}
      {source && <>
        <Muted>Stage {source.stage ?? 'Not recorded'} · Layer {source.layer ?? 'Not recorded'}</Muted>
        <div className={styles.statGrid}>
          <StatCard label="Attestations" value={source.evidence.toLocaleString()} />
          <StatCard label="Content entities" value={source.content?.toLocaleString() ?? 'Not recorded'} />
        </div>
        {source.role && <Muted>{source.role}</Muted>}
        <p><Link to={`/operator?section=ingest&source=${encodeURIComponent(source.key)}`}>View ingestion receipts</Link>
          {source.id_hex && <> · <Link to={`/explore/entity/${source.id_hex}`}>Inspect source entity</Link></>}</p>
      </>}
    </Panel>
    {source && <SourceRows key={JSON.stringify([source.id_hex, tenant, quoteId])} source={source} tenant={tenant} quoteId={quoteId} />}
  </Stack>;
}
function SourceRows({ source, tenant, quoteId }: { source: ExploreSourceRow; tenant: string; quoteId: string }) {
  const [params, setParams] = useSearchParams();
  const [limit, setLimit] = useState(200);
  const roster = useReadResource({
    key: JSON.stringify(['source-roster', tenant, quoteId, source.id_hex, limit]), enabled: !!source.id_hex,
    read: async (signal) => {
      const result = await exploreSourceRoster(source.id_hex!, limit, { tenant, quoteId, signal });
      return captureRows(result.rows, `A bounded sample of up to ${limit} assertions from ${source.key}; not the complete source or a global search.`, { source_id: source.id_hex, source_name: source.key, requested_limit: limit });
    },
  });
  const columns: ResultColumn<SourceRosterRow>[] = [
    { key: 'subject', label: 'Subject', render: (row) => row.subject_id ? <Link to={`/explore/entity/${row.subject_id}`}>{row.subject || row.subject_id}</Link> : row.subject },
    { key: 'relation', label: 'Relation' },
    { key: 'object', label: 'Object', render: (row) => row.object_id ? <Link to={`/explore/entity/${row.object_id}`}>{row.object || row.object_id}</Link> : row.object },
    { key: 'observations', label: 'Observations' },
  ];
  return <Panel title="What this source asserts" expandable actions={<Button variant="ghost" disabled={!source.id_hex} onClick={() => void roster.refresh()}>Refresh assertions</Button>}>
    {!source.id_hex ? <Muted>No source ID was returned. The testimony read is unavailable.</Muted> : <>
      <label>Requested assertion sample <select value={limit} onChange={(event) => setLimit(Number(event.target.value))}>{[40, 100, 200].map((value) => <option key={value} value={value}>{value}</option>)}</select></label>
      <ReadStatus label="Source assertions" resource={roster} />
      {roster.data && <ResultWorkspace scopeKey={JSON.stringify([tenant, source.id_hex])} label="Source assertions" snapshot={roster.data} columns={columns}
        rowLabel={(row, index) => `${row.subject} · ${row.relation} · ${row.object} (row ${index + 1})`}
        filterText={params.get('q') ?? ''} onFilterTextChange={(value) => {
          const next = new URLSearchParams(params); if (value) next.set('q', value); else next.delete('q'); setParams(next, { replace: true });
        }} />}
    </>}
  </Panel>;
}
