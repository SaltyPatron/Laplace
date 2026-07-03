import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiGet, apiPost } from '../api/client';
import { streamLabEvents, type LabEvent, type LabJob, type LabCatalog, type LabJobSpec } from './lab/sse';

// Field metadata for every param each job kind actually reads server-side (ChessLabRunners.cs).
// The catalog only advertises a subset as `default`; this is the full, typed set so the form
// stops hiding real knobs (maxPlies/concurrency/openings/… were unreachable before).
type Field =
  | { key: string; label: string; type: 'number'; min: number; max: number; step?: number; unit?: string; help?: string }
  | { key: string; label: string; type: 'text' | 'password'; help?: string }
  | { key: string; label: string; type: 'select'; options: string[]; help?: string }
  | { key: string; label: string; type: 'bool'; help?: string };

const JOB_FIELDS: Record<string, Field[]> = {
  'substrate-test': [
    { key: 'mode', label: 'bias mode', type: 'select', options: ['fold', 'edge', 'off'], help: 'substrate root-bias source (off = pure search baseline)' },
    { key: 'games', label: 'games', type: 'number', min: 1, max: 500 },
    { key: 'depth', label: 'depth', type: 'number', min: 1, max: 12 },
    { key: 'maxPlies', label: 'max plies', type: 'number', min: 10, max: 400, help: 'adjudicate a draw past this length' },
    { key: 'concurrency', label: 'concurrency', type: 'number', min: 1, max: 16 },
    { key: 'openings', label: 'opening book', type: 'bool', help: 'seed games from the opening set' },
  ],
  ladder: [
    { key: 'games', label: 'games / term', type: 'number', min: 1, max: 200 },
    { key: 'depth', label: 'depth', type: 'number', min: 1, max: 12 },
  ],
  tactics: [{ key: 'depth', label: 'depth', type: 'number', min: 1, max: 16 }],
  review: [
    { key: 'path', label: 'PGN path', type: 'text', help: 'server-side path to a .pgn file' },
    { key: 'depth', label: 'depth', type: 'number', min: 1, max: 12 },
    { key: 'maxGames', label: 'max games', type: 'number', min: 1, max: 100 },
  ],
  'learned-pst': [],
  cutechess: [
    { key: 'rounds', label: 'rounds', type: 'number', min: 1, max: 100 },
    { key: 'depth', label: 'depth', type: 'number', min: 1, max: 20 },
  ],
  'lichess-bot': [
    { key: 'token', label: 'API token', type: 'password', help: 'lichess bot OAuth token (or set LICHESS_API)' },
    { key: 'depth', label: 'depth', type: 'number', min: 1, max: 12 },
    { key: 'maxConcurrent', label: 'max games', type: 'number', min: 1, max: 8 },
  ],
  'lichess-fetch': [
    { key: 'user', label: 'username', type: 'text' },
    { key: 'site', label: 'site', type: 'select', options: ['lichess', 'chesscom'] },
    { key: 'max', label: 'max games', type: 'number', min: 1, max: 1000, step: 10 },
  ],
};

// Engines a kind cannot run without. Start is gated when any are missing.
const REQUIRES: Record<string, string[]> = {
  cutechess: ['cutechess', 'stockfish', 'qt', 'laplaceUci'],
};

const FALLBACK_JOBS: LabJobSpec[] = [{ kind: 'substrate-test', label: 'Substrate test', default: { games: '20', depth: '4', mode: 'fold' } }];

function fieldDefault(f: Field): string {
  if (f.type === 'bool') return 'false';
  if (f.type === 'select') return f.options[0];
  return f.type === 'number' ? String(f.min) : '';
}

// Seed the form: catalog-advertised default wins, else the field's own default.
function paramsFor(kind: string, spec: LabJobSpec | undefined): Record<string, string> {
  const out: Record<string, string> = {};
  for (const f of JOB_FIELDS[kind] ?? []) out[f.key] = spec?.default?.[f.key] ?? fieldDefault(f);
  return out;
}

export function ChessLabView() {
  const [catalog, setCatalog] = useState<LabCatalog | null>(null);
  const [jobs, setJobs] = useState<LabJob[]>([]);
  const [activeId, setActiveId] = useState<string | null>(null);
  const [events, setEvents] = useState<LabEvent[]>([]);
  const [kind, setKind] = useState('substrate-test');
  const [params, setParams] = useState<Record<string, string>>(() => paramsFor('substrate-test', undefined));
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

  // Fetch the catalog + job list once. The catalog (engines/specs) is static, and running-job
  // progress arrives over the SSE stream — no polling heartbeat. The list is refreshed on
  // explicit actions (start/stop) and when a stream ends.
  useEffect(() => { void refresh(); }, [refresh]);
  useEffect(() => () => esRef.current?.abort(), []);

  const jobSpecs = catalog?.jobs ?? FALLBACK_JOBS;
  const activeSpec = useMemo(() => jobSpecs.find((j) => j.kind === kind), [jobSpecs, kind]);
  const fields = JOB_FIELDS[kind] ?? [];

  // Seed the form once per kind — NOT on every catalog poll. `refresh()` replaces `catalog`
  // (and therefore `activeSpec`) every 5s with a fresh object; keying the re-seed on
  // activeSpec wiped whatever the user had typed on each poll. Track the kind we last
  // seeded for a real spec and only re-seed on an actual kind switch or first spec arrival.
  const seededRef = useRef<string | null>(null);
  useEffect(() => {
    if (seededRef.current === kind) return;          // already seeded this kind; leave edits alone
    setParams(paramsFor(kind, activeSpec));
    if (catalog) seededRef.current = kind;           // only "lock in" once real defaults exist
  }, [kind, catalog, activeSpec]);

  const engines = catalog?.engines ?? {};
  const missingEngines = (REQUIRES[kind] ?? []).filter((name) => !engines[name]?.found);
  const blockedReason = missingEngines.length > 0 ? `needs ${missingEngines.join(', ')}` : null;

  const active = jobs.find((j) => j.id === activeId) ?? jobs[0];
  const activeRunning = active?.state === 'Running' || active?.state === 'Pending';

  const openJob = useCallback((job: LabJob) => {
    setActiveId(job.id);
    setEvents([]);
    esRef.current?.abort();
    const ac = new AbortController();
    esRef.current = ac;
    void (async () => {
      try {
        for await (const evt of streamLabEvents(job.id, ac.signal)) {
          setEvents((prev) => [...prev.slice(-200), evt]);
        }
      } catch { /* aborted or stream closed */ }
      void refresh();
    })();
  }, [refresh]);

  const startJob = async () => {
    if (starting || activeRunning || blockedReason) return;
    setStarting(true);
    setErr(null);
    try {
      const r = await apiPost<{ jobId: string }>('/chess/lab/start', { kind, config: params });
      openJob({ id: r.jobId, kind, state: 'Pending', summary: { done: 0, total: 0 }, artifacts: {} });
      void refresh();
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    } finally {
      setStarting(false);
    }
  };

  const stopJob = async () => {
    if (!active) return;
    try { await apiPost(`/chess/lab/stop/${active.id}`, {}); void refresh(); } catch (e) { setErr(e instanceof Error ? e.message : String(e)); }
  };

  return (
    <div className="chess-lab">
      <section className="panel">
        <div className="lab-title">
          <h3>Chess Lab</h3>
          <span className="muted">headless experiments over the substrate & external engines</span>
        </div>
        {catalog && (
          <div className="lab-engines">
            {Object.entries(engines).map(([name, e]) => (
              <span key={name} className={`engine-chip ${e.found ? 'ok' : 'missing'}`} title={e.path || 'not found'}>
                <b>{name}</b> {e.found ? '✓' : '✗'}
              </span>
            ))}
            {Object.keys(engines).length === 0 && <span className="muted">no engines reported</span>}
          </div>
        )}
        {err && <div className="chess-error" role="alert">{err}</div>}

        <div className="lab-form">
          <label className="lab-field">
            <span>experiment</span>
            <select value={kind} onChange={(e) => setKind(e.target.value)}>
              {jobSpecs.map((j) => <option key={j.kind} value={j.kind}>{j.label ?? j.kind}</option>)}
            </select>
          </label>
          {fields.map((f) => (
            <LabField key={f.key} field={f} value={params[f.key] ?? ''}
                      onChange={(v) => setParams((p) => ({ ...p, [f.key]: v }))} />
          ))}
          {fields.length === 0 && <p className="muted lab-noparams">no parameters — reads directly from the substrate.</p>}
        </div>

        <div className="row lab-actions">
          <button onClick={() => void startJob()} disabled={starting || activeRunning || !!blockedReason} title={blockedReason ?? undefined}>
            {starting ? '…' : activeRunning ? 'Running…' : 'Start'}
          </button>
          <button className="ghost" onClick={() => void stopJob()} disabled={!activeRunning}>Stop</button>
          {blockedReason && <span className="lab-blocked">{blockedReason}</span>}
        </div>
      </section>

      {jobs.length > 0 && (
        <section className="panel lab-jobs">
          <h3>Jobs</h3>
          <ul className="lab-job-list">
            {jobs.map((j) => (
              <li key={j.id} className={j.id === active?.id ? 'sel' : ''}>
                <button className="ghost" onClick={() => openJob(j)}>
                  <span className={`state state-${j.state.toLowerCase()}`}>{j.state}</span>
                  <span className="jk">{j.kind}</span>
                  <span className="muted">{j.summary.message ?? `${j.summary.done}/${j.summary.total}`}</span>
                </button>
              </li>
            ))}
          </ul>
        </section>
      )}

      {active && (
        <section className="panel">
          <h3>{active.kind} <span className={`muted state-${active.state.toLowerCase()}`}>{active.state}</span></h3>
          <p className="muted">{active.summary.message ?? `${active.summary.done}/${active.summary.total}`}</p>
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
          {events.length === 0 && <li className="muted">no events yet — Start an experiment above.</li>}
        </ul>
      </section>
    </div>
  );
}

function LabField({ field, value, onChange }: { field: Field; value: string; onChange: (v: string) => void }) {
  // bool -> toggle switch
  if (field.type === 'bool') {
    const on = value === 'true';
    return (
      <div className="lab-field lab-field-row" title={field.help}>
        <span>{field.label}</span>
        <button type="button" role="switch" aria-checked={on} className={`toggle${on ? ' on' : ''}`}
                onClick={() => onChange(on ? 'false' : 'true')}><i /></button>
      </div>
    );
  }
  // small choice set -> segmented pills
  if (field.type === 'select') {
    return (
      <div className="lab-field" title={field.help}>
        <span>{field.label}</span>
        <div className="seg" role="radiogroup" aria-label={field.label}>
          {field.options.map((o) => (
            <button type="button" key={o} role="radio" aria-checked={value === o}
                    className={value === o ? 'on' : ''} onClick={() => onChange(o)}>{o}</button>
          ))}
        </div>
      </div>
    );
  }
  // numeric -> slider + editable number box, both bound to the same value
  if (field.type === 'number') {
    const clamp = (n: number) => Math.min(field.max, Math.max(field.min, n));
    const set = (raw: string) => {
      if (raw === '') { onChange(''); return; }
      const n = Number(raw);
      onChange(Number.isFinite(n) ? String(clamp(Math.round(n))) : raw);
    };
    return (
      <div className="lab-field" title={field.help}>
        <span className="lab-field-head">{field.label}<b>{value || '—'}{field.unit ?? ''}</b></span>
        <div className="lab-slider">
          <input type="range" min={field.min} max={field.max} step={field.step ?? 1}
                 value={value === '' ? field.min : value} onChange={(e) => onChange(e.target.value)} />
          <input type="number" className="lab-num" min={field.min} max={field.max} step={field.step ?? 1}
                 value={value} onChange={(e) => set(e.target.value)} onBlur={(e) => set(e.target.value || String(field.min))} />
        </div>
      </div>
    );
  }
  // free text / secret
  return (
    <div className="lab-field" title={field.help}>
      <span>{field.label}</span>
      <input type={field.type === 'password' ? 'password' : 'text'} value={value} onChange={(e) => onChange(e.target.value)} />
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
