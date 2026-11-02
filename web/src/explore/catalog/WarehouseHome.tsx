import { Button, ErrorText, Input, LoadingText, Muted, Panel, ReadStatus, Table, Td, Th, useReadResource } from '@ui';
import { lazy, Suspense } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { exploreCatalog } from '../api';
import { SourceBrowse } from '../browse/SourceBrowse';
import { useAppStore } from '../../store';
import { MasterDetail } from '../../ui/composites/MasterDetail/MasterDetail';
import { FacetCheckboxes } from '../../ui/composites/FacetCheckboxes/FacetCheckboxes';
import type { ExploreSourceRow } from '../types';
import styles from './WarehouseHome.module.css';

const sourceIdentity = (source: ExploreSourceRow) => source.id_hex ?? source.key;
const EntityDetail = lazy(() => import('../entity/EntityDetail').then(module => ({ default: module.EntityDetail })));

export function WarehouseHome({ embedded = false }: { embedded?: boolean } = {}) {
  const [params, setParams] = useSearchParams();
  const { tenant, quoteId } = useAppStore();
  const catalog = useReadResource({
    key: JSON.stringify(['warehouse', tenant, quoteId]),
    read: signal => exploreCatalog({ tenant, quoteId, signal }),
  });
  const sourceQuery = params.get('sources') ?? '';
  const selected = params.get('source');
  const selectedEntity = params.get('entity');
  const stages = params.getAll('stage'), layers = params.getAll('layer'), roles = params.getAll('role');
  const all = catalog.data?.sources ?? [];
  const selectedSource = all.find(source => sourceIdentity(source) === selected);
  const needle = sourceQuery.trim().toLocaleLowerCase();
  const sources = all.filter(source =>
    (!needle || [source.key, source.stage, source.layer, source.role].some(value => value?.toLocaleLowerCase().includes(needle))) &&
    (!stages.length || stages.includes(source.stage ?? '')) &&
    (!layers.length || layers.includes(source.layer ?? '')) &&
    (!roles.length || roles.includes(source.role ?? '')));
  function address(patch: Record<string, string | string[] | null>) {
    const next = new URLSearchParams(params);
    for (const [key, value] of Object.entries(patch)) {
      next.delete(key);
      if (Array.isArray(value)) value.forEach(item => next.append(key, item));
      else if (value !== null && value !== '') next.set(key, value);
    }
    return next;
  }
  const apply = (patch: Record<string, string | string[] | null>, replace = false) => setParams(address(patch), { replace, preventScrollReset: true });
  return <div className={`${styles.home} ${selected || selectedEntity ? styles.hasSelection : ''}`}>
    {!embedded && <header className={styles.hero}>
      <h2>Sources</h2>
      <Muted>Sources and their recorded assertions. Select a source to inspect it beside the catalogue.</Muted>
    </header>}
    <div className={styles.tableTools}>
      <Input value={sourceQuery} onChange={event => apply({ sources: event.target.value }, true)} placeholder="Filter sources…" aria-label="Filter sources" />
      <Button variant="ghost" onClick={() => apply({ sources: null, stage: null, layer: null, role: null })}>Clear filters</Button>
      <Button variant="ghost" onClick={() => void catalog.refresh()}>Refresh catalogue</Button>
    </div>
    <ReadStatus label="Source catalogue" resource={catalog} />
    {catalog.data && <>
      <div className={styles.facets} role="group" aria-label="Source filters">
        <FacetCheckboxes label="Stage" values={all.map(source => source.stage)} selected={stages} onChange={stage => apply({ stage })} />
        <FacetCheckboxes label="Layer" values={all.map(source => source.layer)} selected={layers} onChange={layer => apply({ layer })} />
        <FacetCheckboxes label="Role" values={all.map(source => source.role)} selected={roles} onChange={role => apply({ role })} />
      </div>
      <Muted>{sources.length.toLocaleString()} of {all.length.toLocaleString()} sources. Select any values within a filter; different filters apply together. Checkbox counts cover the catalogue.</Muted>
      <MasterDetail label="Sources and detail" selected={!!selected || !!selectedEntity} onBack={() => apply({ source: null, entity: null })}
        master={<Panel title="Sources"><Table><thead><tr><Th>Source</Th><Th>Stage / layer</Th><Th>Attestations</Th></tr></thead>
          <tbody>{sources.map(source => <tr key={sourceIdentity(source)} className={sourceIdentity(source) === selected ? styles.selected : undefined}>
            <Td><Link to={`?${address({ source: sourceIdentity(source), entity: null })}`} aria-current={sourceIdentity(source) === selected ? 'true' : undefined} preventScrollReset>{source.key}</Link>
              {source.id_hex && all.filter(item => item.key === source.key).length > 1 && <small className={styles.sourceId}>{source.id_hex}</small>}
            </Td>
            <Td>{source.stage ?? 'Not recorded'}<br />{source.layer ?? 'Not recorded'}</Td>
            <Td>{source.evidence?.toLocaleString() ?? 'Not measured'}</Td>
          </tr>)}</tbody></Table>
          {!sources.length && <p>No sources match these filters. Clear filters to see the full catalogue.</p>}
        </Panel>}>
        {selectedEntity && /^[0-9a-f]{32}$/i.test(selectedEntity) ? <>
          {selected && <Button variant="ghost" onClick={() => apply({ entity: null })}>Back to source</Button>}
          <Suspense fallback={<LoadingText>Reading entity…</LoadingText>}><EntityDetail key={selectedEntity} selectedId={selectedEntity} /></Suspense>
        </> : selected ? <>
          {!selectedSource ? <ErrorText>The selected source is not in this catalogue.</ErrorText> : <>
            {!sources.some(source => sourceIdentity(source) === selected) && <p role="status">The selected source is outside the current filters.</p>}
            <SourceBrowse selectedSource={selectedSource.key} selectedSourceId={selectedSource.id_hex ?? undefined} />
          </>}
        </> : <><h2>Select a source</h2><p>Its identity, metadata and assertions appear here. The source list and filters stay in place.</p></>}
      </MasterDetail>
    </>}
  </div>;
}
