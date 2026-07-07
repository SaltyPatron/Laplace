import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiGet, apiPost } from '../api/client';
import {
  Alert,
  Button,
  Chip,
  Field,
  Input,
  Muted,
  Panel,
  SegmentedControl,
  Select,
  SliderField,
  Toggle,
  Tooltip,
  TooltipContent,
  TooltipTrigger,
} from '@ui';
import { streamLabEvents, type LabEvent, type LabJob, type LabCatalog, type LabJobSpec } from './lab/sse';
import styles from './ChessLabView.module.css';

type FieldDef =
  | { key: string; label: string; type: 'number'; min: number; max: number; step?: number; unit?: string; help?: string }
  | { key: string; label: string; type: 'text' | 'password'; help?: string }
  | { key: string; label: string; type: 'select'; options: string[]; help?: string }
  | { key: string; label: string; type: 'bool'; help?: string };

const JOB_FIELDS: Record<string, FieldDef[]> = {
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

const REQUIRES: Record<string, string[]> = {
  cutechess: ['cutechess', 'stockfish', 'qt', 'laplaceUci'],
};

const FALLBACK_JOBS: LabJobSpec[] = [{ kind: 'substrate-test', label: 'Substrate test', default: { games: '20', depth: '4', mode: 'fold' } }];

function fieldDefault(f: FieldDef): string {
  if (f.type === 'bool') return 'false';
  if (f.type === 'select') return f.options[0];
  return f.type === 'number' ? String(f.min) : '';
}

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

  useEffect(() => { void refresh(); }, [refresh]);
  useEffect(() => () => esRef.current?.abort(), []);

  const jobSpecs = catalog?.jobs ?? FALLBACK_JOBS;
  const activeSpec = useMemo(() => jobSpecs.find((j) => j.kind === kind), [jobSpecs, kind]);
  const fields = JOB_FIELDS[kind] ?? [];

  const seededRef = useRef<string | null>(null);
  useEffect(() => {
    if (seededRef.current === kind) return;
    setParams(paramsFor(kind, activeSpec));
    if (catalog) seededRef.current = kind;
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
    <div className={styles.lab}>
      <Panel>
        <div className={styles.title}>
          <h3>Chess Lab</h3>
          <Muted>headless experiments over the substrate & external engines</Muted>
        </div>
        {catalog && (
          <div className={styles.engines}>
            {Object.entries(engines).map(([name, e]) => (
              <Tooltip key={name}>
                <TooltipTrigger asChild>
                  <Chip variant={e.found ? 'engineOk' : 'engineMissing'}>
                    <b>{name}</b> {e.found ? '✓' : '✗'}
                  </Chip>
                </TooltipTrigger>
                <TooltipContent>{e.path || 'not found'}</TooltipContent>
              </Tooltip>
            ))}
            {Object.keys(engines).length === 0 && <Muted>no engines reported</Muted>}
          </div>
        )}
        {err && <Alert>{err}</Alert>}

        <div className={styles.form}>
          <Field label="experiment">
            <Select value={kind} onChange={(e) => setKind(e.target.value)}>
              {jobSpecs.map((j) => <option key={j.kind} value={j.kind}>{j.label ?? j.kind}</option>)}
            </Select>
          </Field>
          {fields.map((f) => (
            <LabField key={f.key} field={f} value={params[f.key] ?? ''}
                      onChange={(v) => setParams((p) => ({ ...p, [f.key]: v }))} />
          ))}
          {fields.length === 0 && <Muted className={styles.noParams}>no parameters — reads directly from the substrate.</Muted>}
        </div>

        <div className={styles.actions}>
          {blockedReason ? (
            <Tooltip>
              <TooltipTrigger asChild>
                <Button
                  onClick={() => void startJob()}
                  visuallyDisabled
                >
                  {starting ? '…' : activeRunning ? 'Running…' : 'Start'}
                </Button>
              </TooltipTrigger>
              <TooltipContent>{blockedReason}</TooltipContent>
            </Tooltip>
          ) : (
            <Button onClick={() => void startJob()} disabled={starting || activeRunning} loading={starting}>
              {starting ? '…' : activeRunning ? 'Running…' : 'Start'}
            </Button>
          )}
          <Button variant="ghost" onClick={() => void stopJob()} disabled={!activeRunning}>Stop</Button>
          {blockedReason && <span className={styles.blocked}>{blockedReason}</span>}
        </div>
      </Panel>

      {jobs.length > 0 && (
        <Panel className={styles.jobs} title="Jobs">
          <ul className={styles.jobList}>
            {jobs.map((j) => (
              <li key={j.id} className={j.id === active?.id ? styles.jobItemSelected : undefined}>
                <Button variant="ghost" className={styles.jobBtn} onClick={() => openJob(j)}>
                  <span className={stateStyle(j.state)}>{j.state}</span>
                  <span className={styles.jobKind}>{j.kind}</span>
                  <Muted className={styles.jobSummary}>{j.summary.message ?? `${j.summary.done}/${j.summary.total}`}</Muted>
                </Button>
              </li>
            ))}
          </ul>
        </Panel>
      )}

      {active && (
        <Panel title={<>{active.kind} <Muted className={stateStyle(active.state)}>{active.state}</Muted></>}>
          <Muted>{active.summary.message ?? `${active.summary.done}/${active.summary.total}`}</Muted>
          {active.artifacts?.['games.pgn'] && (
            <div className={styles.artifactRow}>
              <a href={`/chess/lab/jobs/${active.id}/artifact/games.pgn`} download>Download PGN</a>
              <Button onClick={() => apiPost(`/chess/lab/jobs/${active.id}/ingest`, {})}>Ingest to substrate</Button>
            </div>
          )}
        </Panel>
      )}

      <Panel className={styles.feed} title="Live feed">
        <ul className={styles.events}>
          {events.map((e, i) => <LabRow key={i} e={e} />)}
          {events.length === 0 && <li><Muted>no events yet — Start an experiment above.</Muted></li>}
        </ul>
      </Panel>
    </div>
  );
}

function LabField({ field, value, onChange }: { field: FieldDef; value: string; onChange: (v: string) => void }) {
  if (field.type === 'bool') {
    const on = value === 'true';
    return (
      <Field label={field.label} help={field.help} layout="row">
        <Toggle checked={on} onCheckedChange={(checked) => onChange(checked ? 'true' : 'false')} />
      </Field>
    );
  }

  if (field.type === 'select') {
    return (
      <Field label={field.label} help={field.help}>
        <SegmentedControl
          value={value}
          onValueChange={onChange}
          options={field.options}
          label={field.label}
        />
      </Field>
    );
  }

  if (field.type === 'number') {
    return (
      <Field
        label={field.label}
        help={field.help}
        valueDisplay={`${value || '—'}${field.unit ?? ''}`}
      >
        <SliderField
          min={field.min}
          max={field.max}
          step={field.step ?? 1}
          value={value}
          onChange={onChange}
        />
      </Field>
    );
  }

  return (
    <Field label={field.label} help={field.help}>
      <Input
        type={field.type === 'password' ? 'password' : 'text'}
        value={value}
        onChange={(e) => onChange(e.target.value)}
      />
    </Field>
  );
}

const STATE_CLASS: Record<string, string> = {
  Running: 'stateRunning',
  Pending: 'statePending',
  Completed: 'stateCompleted',
  Failed: 'stateFailed',
  Cancelled: 'stateCancelled',
};

function stateStyle(state: string): string | undefined {
  const key = STATE_CLASS[state];
  return key ? styles[key as keyof typeof styles] : styles.state;
}

function LabRow({ e }: { e: LabEvent }) {
  if (e.title && e.rows) {
    return (
      <li className={styles.tableRow}>
        <div className={styles.tableTitle}>{e.title}</div>
        <div className={styles.tableScroll}>
          <table className={styles.labTable}>
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
    return <li className={styles.metric}><span className={styles.metricName}>{e.name}</span><b>{e.value}{e.unit ?? ''}</b></li>;
  }
  if (e.done !== undefined && e.total !== undefined) {
    return <li className={styles.progress}>progress {e.done}/{e.total}{e.label ? ` · ${e.label}` : ''}</li>;
  }
  if (e.result !== undefined && (e.white !== undefined || e.black !== undefined)) {
    return <li>#{e.index ?? '?'} {e.white ?? '?'} vs {e.black ?? '?'} — <b>{e.result}</b></li>;
  }
  if (e.finalState !== undefined) {
    return <li className={styles.done}>done: {e.finalState}{e.message ? ` (${e.message})` : ''}</li>;
  }
  if (e.level !== undefined && e.message !== undefined) {
    const lvl = e.level === 'error' ? styles.logError : (e.level === 'warning' || e.level === 'warn') ? styles.logWarn : styles.logInfo;
    return <li className={lvl}>[{e.level}] {e.message}</li>;
  }
  return <li><Muted>{JSON.stringify(e)}</Muted></li>;
}
