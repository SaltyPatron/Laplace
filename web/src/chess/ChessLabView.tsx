import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiGet, apiPost } from '../api/client';
import { streamLabEvents, type LabEvent, type LabJob, type LabCatalog, type LabJobSpec } from './lab/sse';

// Params a job kind accepts that the catalog `default` block does not advertise
// (secrets / free-text the server reads from config but doesn't seed a default for).
const EXTRA_PARAMS: Record<string, string[]> = {
  review: ['path'],
  'lichess-bot': ['token'],
  'lichess-fetch': ['user'],
};

const FALLBACK_JOBS: LabJobSpec[] = [{ kind: 'substrate-test', label: 'Substrate test', default: { games: '20', depth: '4' } }];

function paramsFor(spec: LabJobSpec | undefined): Record<string, string> {
  const out: Record<string, string> = { ...(spec?.default ?? {}) };
  for (const k of EXTRA_PARAMS[spec?.kind ?? ''] ?? []) if (!(k in out)) out[k] = '';
  return out;
}

export function ChessLabView() {
  const [catalog, setCatalog] = useState<LabCatalog | null>(null);
  const [jobs, setJobs] = useState<LabJob[]>([]);
  const [activeId, setActiveId] = useState<string | null>(null);
  const [events, setEvents] = useState<LabEvent[]>([]);
  const [kind, setKind] = useState('substrate-test');
  const [params, setParams] = useState<Record<string, string>>({ games: '20', depth: '4' });
  const [starting, setStarting] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const esRef = useRef<AbortController | null>(null);

  const refresh = useCallback(async () => {
    try {
      setCatalog(await apiGet<LabCatalog>('/chess/lab/catalog'));
      setJobs(await apiGet<LabJob[]>('/chess/lab/jobs'));
      setErr(null);
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    }
  }, []);

  useEffect(() => { void refresh(); const h = setInterval(refresh, 5000); return () => clearInterval(h); }, [refresh]);
  useEffect(() => () => esRef.current?.abort(), []);

  const jobSpecs = catalog?.jobs ?? FALLBACK_JOBS;
  const activeSpec = useMemo(() => jobSpecs.find((j) => j.kind === kind), [jobSpecs, kind]);

  // Re-seed the param inputs whenever the selected kind (or its catalog spec) changes.
  useEffect(() => { setParams(paramsFor(activeSpec)); }, [activeSpec]);

  const active = jobs.find((j) => j.id === activeId) ?? jobs[0];
  const activeRunning = active?.state === 'Running' || active?.state === 'Pending';

  const startJob = async () => {
    if (starting || activeRunning) return;
    setStarting(true);
    setErr(null);
    try {
      const r = await apiPost<{ jobId: string }>('/chess/lab/start', { kind, config: params });
      setActiveId(r.jobId);
      setEvents([]);
      esRef.current?.abort();
      const ac = new AbortController();
      esRef.current = ac;
      void (async () => {
        try {
          for await (const evt of streamLabEvents(r.jobId, ac.signal)) {
            setEvents((prev) => [...prev.slice(-200), evt]);
          }
        } catch { /* aborted or stream closed */ }
        void refresh();
      })();
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    } finally {
      setStarting(false);
    }
  };

  const stopJob = async () => {
    if (!activeId) return;
    try { await apiPost(`/chess/lab/stop/${activeId}`, {}); } catch (e) { setErr(e instanceof Error ? e.message : String(e)); }
  };

  return (
    <div className="chess-lab">
      <section className="panel">
        <h3>Chess Lab</h3>
        {catalog && (
          <p className="lab-engines">
            {Object.entries(catalog.engines ?? {}).map(([name, e]) => (
              <span key={name} className={e.found ? 'ok' : 'missing'} title={e.path || 'not found'}>
                {name} {e.found ? '✓' : '✗'}
              </span>
            ))}
            {Object.keys(catalog.engines ?? {}).length === 0 && <span className="muted">no engines reported</span>}
          </p>
        )}
        {err && <div className="chess-error">{err}</div>}
        <div className="knobs">
          <label>job
            <select value={kind} onChange={(e) => setKind(e.target.value)}>
              {jobSpecs.map((j) => (
                <option key={j.kind} value={j.kind}>{j.label ?? j.kind}</option>
              ))}
            </select>
          </label>
          {Object.keys(params).map((key) => (
            <label key={key}>{key}
              <input
                type={key === 'token' ? 'password' : 'text'}
                value={params[key]}
                onChange={(e) => setParams((p) => ({ ...p, [key]: e.target.value }))}
              />
            </label>
          ))}
        </div>
        <div className="row">
          <button onClick={() => void startJob()} disabled={starting || activeRunning}>
            {starting ? '…' : activeRunning ? 'Running…' : 'Start'}
          </button>
          <button className="ghost" onClick={() => void stopJob()} disabled={!activeRunning}>Stop</button>
        </div>
      </section>

      {active && (
        <section className="panel">
          <h3>{active.kind} <span className="muted">{active.state}</span></h3>
          <p>{active.summary.message ?? `${active.summary.done}/${active.summary.total}`}</p>
          {active.artifacts?.['games.pgn'] && (
            <div className="row">
              <a href={`/chess/lab/jobs/${active.id}/artifact/games.pgn`} download>Download PGN</a>
              <button onClick={() => apiPost(`/chess/lab/jobs/${active.id}/ingest`, {})}>Ingest to substrate</button>
            </div>
          )}
        </section>
      )}

      <section className="panel lab-feed">
        <h3>Live feed</h3>
        <ul className="lab-events">
          {events.map((e, i) => <LabRow key={i} e={e} />)}
          {events.length === 0 && <li className="muted">no events yet</li>}
        </ul>
      </section>
    </div>
  );
}

function LabRow({ e }: { e: LabEvent }) {
  if (e.title && e.rows) {
    return (
      <li className="lab-table-row">
        <div className="lab-table-title">{e.title}</div>
        <div className="lab-table-scroll">
          <table className="lab-table">
            {e.columns && (
              <thead><tr>{e.columns.map((c, i) => <th key={i}>{c}</th>)}</tr></thead>
            )}
            <tbody>
              {e.rows.map((row, ri) => (
                <tr key={ri}>{row.map((cell, ci) => <td key={ci}>{cell}</td>)}</tr>
              ))}
            </tbody>
          </table>
        </div>
      </li>
    );
  }
  if (e.name !== undefined && e.value !== undefined) {
    return <li className="lab-metric"><span className="lab-metric-name">{e.name}</span><b>{e.value}{e.unit ?? ''}</b></li>;
  }
  if (e.done !== undefined && e.total !== undefined) {
    return <li className="lab-progress">progress {e.done}/{e.total}{e.label ? ` · ${e.label}` : ''}</li>;
  }
  if (e.result !== undefined && (e.white !== undefined || e.black !== undefined)) {
    return <li className="lab-game">#{e.index ?? '?'} {e.white ?? '?'} vs {e.black ?? '?'} — <b>{e.result}</b></li>;
  }
  if (e.finalState !== undefined) {
    return <li className="lab-done">done: {e.finalState}{e.message ? ` (${e.message})` : ''}</li>;
  }
  if (e.level !== undefined && e.message !== undefined) {
    return <li className={`lab-log lvl-${e.level}`}>[{e.level}] {e.message}</li>;
  }
  return <li className="muted">{JSON.stringify(e)}</li>;
}
