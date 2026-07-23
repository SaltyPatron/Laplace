import { useEffect, useState } from 'react';
import { ErrorText, LoadingText, Muted, Panel, Table, Td, Th } from '@ui';
import { Link } from 'react-router-dom';
import { exploreCatalog } from '../api';
import { SearchBar } from '../components/SearchBar';
import { StatCard } from '../components/StatCard';
import type { ExploreCatalogResponse } from '../types';
import styles from './WarehouseHome.module.css';

export function WarehouseHome() {
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

  return (
    <div className={styles.home}>
      <header className={styles.hero}>
        <h2>Substrate warehouse</h2>
        <Muted>Witnessed consensus at every tier — browse, inspect, export for training.</Muted>
        <SearchBar />
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
                <Td>{e.subject}</Td>
                <Td>{e.type}</Td>
                <Td>{e.object}</Td>
                <Td>{e.effectiveMu != null ? Number(e.effectiveMu).toFixed(1) : '—'}</Td>
                <Td>{e.witnesses}</Td>
              </tr>
            ))}
          </tbody>
        </Table>
      </Panel>

      <Panel title="Sources">
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
            {catalog.sources.slice(0, 24).map((s) => (
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
      </Panel>
    </div>
  );
}
