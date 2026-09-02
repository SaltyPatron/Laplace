import { useEffect, useState } from 'react';
import { ErrorText, Input, LoadingText, Muted, Panel, Table, Td, Th } from '@ui';
import { Link, useSearchParams } from 'react-router-dom';
import { exploreCatalog } from '../api';
import { StatCard } from '../components/StatCard';
import type { ExploreCatalogResponse } from '../types';
import styles from './WarehouseHome.module.css';

export function WarehouseHome() {
  const [params, setParams] = useSearchParams();
  const [catalog, setCatalog] = useState<ExploreCatalogResponse | null>(null);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => {
    exploreCatalog()
      .then(setCatalog)
      .catch((e) => setErr(e instanceof Error ? e.message : String(e)));
  }, []);

  if (err) return <ErrorText>{err}</ErrorText>;
  if (!catalog) return <LoadingText>Loading warehouse…</LoadingText>;

  const entities = catalog.counts.find((c) => c.metric.startsWith('entities'))?.value ?? 0;
  const attestations = catalog.counts.find((c) => c.metric.startsWith('attestations'))?.value ?? 0;
  const consensus = catalog.consensus?.consensusRows ?? 0;
  const sourceQuery = params.get('sources') ?? '';
  const sourceNeedle = sourceQuery.trim().toLocaleLowerCase();
  const sources = catalog.sources.filter((source) =>
    !sourceNeedle || [source.key, source.stage, source.layer, source.role]
      .some((value) => value?.toLocaleLowerCase().includes(sourceNeedle)));

  function setSourceQuery(value: string) {
    const next = new URLSearchParams(params);
    if (value) next.set('sources', value); else next.delete('sources');
    setParams(next, { replace: true });
  }

  return (
    <div className={styles.home}>
      <header className={styles.hero}>
        <h2>Substrate warehouse</h2>
        <Muted>Witness/source inventory and consensus accounting. <Link to="/explore">Open Browse</Link> to discover and traverse canonical entities.</Muted>
      </header>

      <div className={styles.statGrid}>
        <StatCard label="Entities" value={entities.toLocaleString()} />
        <StatCard label="Attestations" value={attestations.toLocaleString()} />
        <StatCard label="Consensus edges" value={consensus.toLocaleString()} />
        {catalog.multi_source_entity_count != null ? (
          <StatCard label="Multi-source" value={catalog.multi_source_entity_count.toLocaleString()} />
        ) : null}
      </div>

      <Panel title="Cadence stages">
        <div className={styles.stageGrid}>
          {catalog.stages.map((s) => (
            <Link key={s.stage} className={styles.stageCard} to={`/explore/stage/${encodeURIComponent(s.stage)}`}>
              <strong>{s.stage}</strong>
              <span>{s.sources.length} sources</span>
            </Link>
          ))}
        </div>
      </Panel>

      <Panel title="Featured">
        <div className={styles.chipRow}>
          {catalog.featured_refs.map((ref) => (
            <Link key={ref} className={styles.chip} to={`/explore/resolve/${encodeURIComponent(ref)}`}>{ref}</Link>
          ))}
        </div>
      </Panel>

      <Panel title="Top relations">
        <Table>
          <thead>
            <tr>
              <Th>Subject</Th>
              <Th>Type</Th>
              <Th>Object</Th>
              <Th>μ</Th>
              <Th>Wit</Th>
            </tr>
          </thead>
          <tbody>
            {catalog.top_relations.map((e, i) => (
              <tr key={i}>
                <Td><Link to={`/explore/entity/${e.subjectIdHex}`}>{e.subject}</Link></Td>
                <Td>{e.type}</Td>
                <Td><Link to={`/explore/entity/${e.objectIdHex}`}>{e.object}</Link></Td>
                <Td>{e.effectiveMu != null ? Number(e.effectiveMu).toFixed(1) : '—'}</Td>
                <Td>{e.witnesses}</Td>
              </tr>
            ))}
          </tbody>
        </Table>
      </Panel>

      <Panel title="Sources">
        <div className={styles.tableTools}>
          <Input
            value={sourceQuery}
            onChange={(event) => setSourceQuery(event.target.value)}
            placeholder="Filter by source, stage, layer, or role…"
            aria-label="Filter warehouse sources"
          />
          <Muted>{sources.length.toLocaleString()} of {catalog.sources.length.toLocaleString()} sources</Muted>
        </div>
        <Table>
          <thead>
            <tr>
              <Th>Source</Th>
              <Th>Stage</Th>
              <Th>Evidence</Th>
              <Th>Content</Th>
            </tr>
          </thead>
          <tbody>
            {sources.map((s) => (
              <tr key={s.key}>
                <Td>
                  <Link to={`/explore/source/${encodeURIComponent(s.key)}`}>{s.key}</Link>
                </Td>
                <Td>{s.stage ?? '—'}</Td>
                <Td>{s.evidence.toLocaleString()}</Td>
                <Td>{s.content?.toLocaleString() ?? "—"}</Td>
              </tr>
            ))}
          </tbody>
        </Table>
        {sources.length === 0 ? <Muted>No sources match “{sourceQuery}”.</Muted> : null}
      </Panel>
    </div>
  );
}
