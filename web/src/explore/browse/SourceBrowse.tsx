import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { useRouteText } from '../../layout/useRouteText';
import { Button, ErrorText, Muted, Panel, ReadStatus, Stack, useReadResource } from '@ui';
import { ResultWorkspace, type ResultColumn } from '../../ui/composites/ResultWorkspace/ResultWorkspace';
import { captureRows } from '../../ui/lib/resultRows';
import { useAppStore } from '../../store';
import { exploreCatalog, exploreSourceRoster } from '../api';
import { useExploreStore } from '../store';
import { EntityLink } from '../components/EntityLink';
import type { ExploreSourceRow, SourceRosterRow } from '../types';
import styles from '../catalog/WarehouseHome.module.css';

/** Catalog, selected source and sampled testimony have separate read scopes. */
export function SourceBrowse({ selectedSource, selectedSourceId }: { selectedSource?: string; selectedSourceId?: string } = {}) {
  const routeSource = useRouteText('/explore/source/:sourceKey', 'sourceKey');
  const sourceKey = selectedSource ?? routeSource;
  const { tenant, quoteId, authUser } = useAppStore();
  const sourceId = selectedSourceId ?? (/^[0-9a-f]{32}$/i.test(routeSource) ? routeSource : undefined);
  return <SourceWorkspace key={JSON.stringify([tenant, authUser?.id, sourceId, sourceKey])} sourceKey={sourceKey} sourceId={sourceId} tenant={tenant} quoteId={quoteId} />;
}
function SourceWorkspace({ sourceKey, sourceId, tenant, quoteId }: { sourceKey: string; sourceId?: string; tenant: string; quoteId: string }) {
  const setBreadcrumb = useExploreStore((state) => state.setBreadcrumb);
  const catalog = useReadResource({
    key: JSON.stringify(['source-catalog', tenant, quoteId]),
    read: (signal) => exploreCatalog({ tenant, quoteId, signal }),
  });
  const matches = catalog.data?.sources.filter(item => sourceId ? item.id_hex === sourceId : item.key === sourceKey) ?? [];
  const source = matches.length === 1 ? matches[0] : undefined;
  useEffect(() => {
    setBreadcrumb({ stage: source?.stage ?? undefined, source: source?.key ?? sourceKey });
  }, [source?.stage, source?.key, sourceKey, setBreadcrumb]);
  return <Stack gap={4}>
    <Panel title={source?.key || sourceKey || 'Source'} actions={<Button variant="ghost" onClick={() => void catalog.refresh()}>Refresh source</Button>}>
      <ReadStatus label="Source catalog" resource={catalog} />
      {catalog.data && !matches.length && <><ErrorText>No source matches “{sourceId ?? sourceKey}” in this catalogue.</ErrorText><Link to="/explore/sources">Browse available sources</Link></>}
      {matches.length > 1 && <><p>Several source records have this name. Select the source identity:</p><ul>{matches.map(item => <li key={item.id_hex}><Link to={`/explore/source/${item.id_hex}`}>{item.key} · {item.id_hex}</Link></li>)}</ul></>}
      {source && <>
        {source.id_hex && <p><strong>Source ID</strong><br /><code>{source.id_hex}</code></p>}
        <dl className={styles.sourceFacts}>
          {source.stage && <div><dt>Stage</dt><dd>{source.stage}</dd></div>}
          {source.layer && <div><dt>Layer</dt><dd>{source.layer}</dd></div>}
          <div><dt>Attestations</dt><dd>{source.evidence?.toLocaleString() ?? 'Not measured'}</dd></div>
          <div><dt>Content entities</dt><dd>{source.content?.toLocaleString() ?? 'Not recorded'}</dd></div>
        </dl>
        {source.role && <Muted>{source.role}</Muted>}
        <p><Link to={`/operator?section=ingest&source=${encodeURIComponent(source.key)}`}>View ingestion receipts</Link>
          {source.id_hex && <> · <EntityLink idHex={source.id_hex} label="Inspect source entity" /></>}</p>
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
    { key: 'subject', label: 'Subject', render: (row) => row.subject_id ? <EntityLink idHex={row.subject_id} label={row.subject || row.subject_id} /> : row.subject },
    { key: 'relation', label: 'Relation' },
    { key: 'object', label: 'Object', render: (row) => row.object_id ? <EntityLink idHex={row.object_id} label={row.object || row.object_id} /> : row.object },
    { key: 'observations', label: 'Observations' },
  ];
  return <Panel title="Assertions" expandable actions={<Button variant="ghost" disabled={!source.id_hex} onClick={() => void roster.refresh()}>Refresh assertions</Button>}>
    {!source.id_hex ? <Muted>No source ID was returned. The testimony read is unavailable.</Muted> : <>
      <label>Requested assertion sample <select value={limit} onChange={(event) => setLimit(Number(event.target.value))}>{[40, 100, 200].map((value) => <option key={value} value={value}>{value}</option>)}</select></label>
      <ReadStatus label="Source assertions" resource={roster} />
      {roster.data && <ResultWorkspace compact scopeKey={JSON.stringify([tenant, source.id_hex])} label="Source assertions" snapshot={roster.data} columns={columns}
        rowLabel={(row, index) => `${row.subject} · ${row.relation} · ${row.object} (row ${index + 1})`}
        filterText={params.get('q') ?? ''} onFilterTextChange={(value) => {
          const next = new URLSearchParams(params); if (value) next.set('q', value); else next.delete('q'); setParams(next, { replace: true });
        }} />}
    </>}
  </Panel>;
}
