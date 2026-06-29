import { useCallback, useEffect, useRef, useState } from 'react';
import { apiGet, apiPost } from '../api/client';
import { streamLabEvents, type LabEvent, type LabJob, type LabCatalog } from './lab/sse';

export function ChessLabView() {
  const [catalog, setCatalog] = useState<LabCatalog | null>(null);
  const [jobs, setJobs] = useState<LabJob[]>([]);
  const [activeId, setActiveId] = useState<string | null>(null);
  const [events, setEvents] = useState<LabEvent[]>([]);
  const [kind, setKind] = useState('substrate-test');
  const [games, setGames] = useState('20');
  const [depth, setDepth] = useState('4');
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

  const startJob = async () => {
    const r = await apiPost<{ jobId: string }>('/chess/lab/start', {
      kind,
      config: { games, depth, mode: 'fold', concurrency: '4' },
    });
    setActiveId(r.jobId);
    setEvents([]);
    esRef.current?.abort();
    const ac = new AbortController();
    esRef.current = ac;
    void (async () => {
      for await (const evt of streamLabEvents(r.jobId, ac.signal)) {
        setEvents((prev) => [...prev.slice(-200), evt]);
      }
      void refresh();
    })();
  };

  const stopJob = async () => {
    if (!activeId) return;
    await apiPost(`/chess/lab/stop/${activeId}`, {});
  };

  const active = jobs.find((j) => j.id === activeId) ?? jobs[0];

  return (
    <div className="chess-lab">
      <section className="panel">
        <h3>Chess Lab</h3>
        {catalog && (
          <p className="muted">
            cutechess {catalog.binaries.cutechessOk ? '✓' : '✗'} · stockfish {catalog.binaries.stockfishOk ? '✓' : '✗'} · Qt {catalog.binaries.qtOk ? '✓' : '✗'}
          </p>
        )}
        {err && <div className="chess-error">{err}</div>}
        <div className="knobs">
          <label>job
            <select value={kind} onChange={(e) => setKind(e.target.value)}>
              {(catalog?.jobs ?? [{ kind: 'substrate-test', label: 'Substrate test' }]).map((j) => (
                <option key={j.kind} value={j.kind}>{j.label ?? j.kind}</option>
              ))}
            </select>
          </label>
          <label>games<input value={games} onChange={(e) => setGames(e.target.value)} /></label>
          <label>depth<input value={depth} onChange={(e) => setDepth(e.target.value)} /></label>
        </div>
        <div className="row">
          <button onClick={() => void startJob()}>Start</button>
          <button onClick={() => void stopJob()} disabled={!activeId}>Stop</button>
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
          {events.map((e, i) => (
            <li key={i}>{formatEvent(e)}</li>
          ))}
        </ul>
      </section>
    </div>
  );
}

function formatEvent(e: LabEvent): string {
  if ('level' in e && 'message' in e) return `[${e.level}] ${e.message}`;
  if ('done' in e && 'total' in e) return `progress ${e.done}/${e.total}${e.label ? ` ${e.label}` : ''}`;
  if ('name' in e && 'value' in e) return `${e.name}=${e.value}${e.unit ?? ''}`;
  if ('title' in e && 'rows' in e) return `${e.title}: ${e.rows?.length ?? 0} rows`;
  if ('finalState' in e) return `done: ${e.finalState}`;
  return JSON.stringify(e);
}
