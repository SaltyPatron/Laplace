import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { exploreCatalog } from '../api';
import { SearchBar } from '../components/SearchBar';
import { StatCard } from '../components/StatCard';
import type { ExploreCatalogResponse } from '../types';

export function WarehouseHome() {
  const [catalog, setCatalog] = useState<ExploreCatalogResponse | null>(null);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => {
    exploreCatalog()
      .then(setCatalog)
      .catch((e) => setErr(e instanceof Error ? e.message : String(e)));
  }, []);

  if (err) return <p className="error">{err}</p>;
  if (!catalog) return <p className="muted">Loading warehouse…</p>;

  const entities = catalog.counts.find((c) => c.metric.startsWith('entities'))?.value ?? 0;
  const attestations = catalog.counts.find((c) => c.metric.startsWith('attestations'))?.value ?? 0;
  const consensus = catalog.consensus?.consensus_rows ?? 0;

  return (
    <div className="warehouse-home">
      <header className="explore-hero">
        <h2>Substrate warehouse</h2>
        <p className="muted">Witnessed consensus at every tier — browse, inspect, export for training.</p>
        <SearchBar />
      </header>

      <div className="stat-grid">
        <StatCard label="Entities" value={entities.toLocaleString()} />
        <StatCard label="Attestations" value={attestations.toLocaleString()} />
        <StatCard label="Consensus edges" value={consensus.toLocaleString()} />
        {catalog.multi_source_entity_count != null ? (
          <StatCard label="Multi-source" value={catalog.multi_source_entity_count.toLocaleString()} />
        ) : null}
      </div>

      <section>
        <h3>Cadence stages</h3>
        <div className="stage-grid">
          {catalog.stages.map((s) => (
            <Link key={s.stage} className="stage-card" to={`/explore/stage/${encodeURIComponent(s.stage)}`}>
              <strong>{s.stage}</strong>
              <span>{s.sources.length} sources</span>
            </Link>
          ))}
        </div>
      </section>

      <section>
        <h3>Featured</h3>
        <div className="chip-row">
          {catalog.featured_refs.map((ref) => (
            <Link key={ref} className="chip" to={`/explore/resolve/${encodeURIComponent(ref)}`}>{ref}</Link>
          ))}
        </div>
      </section>

      <section>
        <h3>Top relations</h3>
        <table className="explore-table">
          <thead><tr><th>Subject</th><th>Type</th><th>Object</th><th>μ</th><th>Wit</th></tr></thead>
          <tbody>
            {catalog.top_relations.map((e, i) => (
              <tr key={i}>
                <td>{e.subject}</td>
                <td>{e.type}</td>
                <td>{e.object}</td>
                <td>{e.effective_mu.toFixed(1)}</td>
                <td>{e.witnesses}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>

      <section>
        <h3>Sources</h3>
        <table className="explore-table">
          <thead><tr><th>Source</th><th>Stage</th><th>Evidence</th><th>Content</th></tr></thead>
          <tbody>
            {catalog.sources.slice(0, 24).map((s) => (
              <tr key={s.key}>
                <td><Link to={`/explore/source/${encodeURIComponent(s.key)}`}>{s.key}</Link></td>
                <td>{s.stage ?? '—'}</td>
                <td>{s.evidence.toLocaleString()}</td>
                <td>{s.content.toLocaleString()}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
    </div>
  );
}
