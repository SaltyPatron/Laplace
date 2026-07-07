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
  SliderField,
  Toggle,
  Tooltip,
  TooltipContent,
  TooltipTrigger,
} from '@ui';
import {
  ENGINE_LABELS,
  experimentFor,
  experimentsInCategory,
  LAB_CATEGORIES,
  type LabExperiment,
} from './lab/experiments';
import { streamLabEvents, type LabEvent, type LabJob, type LabCatalog, type LabJobSpec } from './lab/sse';
import { LichessPanel } from './LichessPanel';
import styles from './ChessLabView.module.css';

type FieldDef =
  | { key: string; label: string; type: 'number'; min: number; max?: number; step?: number; unit?: string; help?: string; placeholder?: string }
  | { key: string; label: string; type: 'text' | 'password'; help?: string; placeholder?: string }
  | { key: string; label: string; type: 'select'; options: string[]; help?: string; optionLabels?: Record<string, string> }
  | { key: string; label: string; type: 'bool'; help?: string };

const JOB_FIELDS: Record<string, FieldDef[]> = {
  'substrate-test': [
    {
      key: 'mode',
      label: 'Bias mode',
      type: 'select',
      options: ['fold', 'edge', 'off'],
      optionLabels: {
        fold: 'Fold — substructure consensus (recommended)',
        edge: 'Edge — raw move popularity',
        off: 'Off — sanity (pure vs pure)',
      },
      help: 'Where root-move bias comes from. Fold is the honest transfer test.',
    },
    { key: 'games', label: 'Games', type: 'number', min: 1, help: 'More games = tighter Elo estimate. No upper limit.', placeholder: '100' },
    { key: 'depth', label: 'Search depth', type: 'number', min: 1, max: 12, help: 'Fixed depth for both sides. Higher = slower, stronger.' },
    { key: 'maxPlies', label: 'Max plies', type: 'number', min: 10, max: 400, help: 'Declare a draw if the game exceeds this length.' },
    { key: 'concurrency', label: 'Parallel games', type: 'number', min: 0, help: '0 = use all performance CPU cores.', placeholder: '0' },
    { key: 'openings', label: 'Opening book', type: 'bool', help: 'Seed from ingested ECO positions instead of random starts.' },
  ],
  ladder: [
    { key: 'games', label: 'Games per term', type: 'number', min: 1, help: 'Each of 6 eval terms runs this many games. Total = ×6.', placeholder: '100' },
    { key: 'depth', label: 'Search depth', type: 'number', min: 1, max: 12 },
    { key: 'maxPlies', label: 'Max plies', type: 'number', min: 10, max: 400, help: 'Draw adjudication cutoff.' },
    { key: 'concurrency', label: 'Core budget', type: 'number', min: 0, help: 'Shared across all 6 terms in parallel. 0 = all performance cores.', placeholder: '0' },
  ],
  tactics: [
    { key: 'depth', label: 'Search depth', type: 'number', min: 1, max: 16, help: 'Depth for each mate puzzle.' },
  ],
  review: [
    { key: 'path', label: 'PGN path (server)', type: 'text', help: 'Absolute path on the API host.', placeholder: 'D:\\Data\\…\\games.pgn' },
    { key: 'depth', label: 'Analysis depth', type: 'number', min: 1, max: 12 },
    { key: 'maxGames', label: 'Max games', type: 'number', min: 1, max: 100 },
  ],
  'learned-pst': [],
  cutechess: [
    { key: 'rounds', label: 'Rounds', type: 'number', min: 1, max: 100, help: 'Each round = 2 games (color swap).' },
    { key: 'depth', label: 'UCI depth', type: 'number', min: 1, max: 20, help: 'Fixed depth sent to both engines.' },
  ],
  'lichess-fetch': [
    { key: 'user', label: 'Username', type: 'text', placeholder: 'DrNykterstein' },
    { key: 'site', label: 'Site', type: 'select', options: ['lichess', 'chesscom'], optionLabels: { lichess: 'lichess.org', chesscom: 'chess.com' } },
    { key: 'max', label: 'Max games', type: 'number', min: 1, max: 1000, step: 10, help: 'Leave at default for provider limit.' },
  ],
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

function requiresFor(exp: LabExperiment | undefined): string[] {
  return exp?.requires ?? [];
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
  const experiment = experimentFor(kind);
  const fields = JOB_FIELDS[kind] ?? [];

  const seededRef = useRef<string | null>(null);
  useEffect(() => {
    if (seededRef.current === kind) return;
    setParams(paramsFor(kind, activeSpec));
    if (catalog) seededRef.current = kind;
  }, [kind, catalog, activeSpec]);

  const engines = catalog?.engines ?? {};
  const missingEngines = requiresFor(experiment).filter((name) => !engines[name]?.found);
  const blockedReason = missingEngines.length > 0
    ? `Install ${missingEngines.map((n) => ENGINE_LABELS[n] ?? n).join(', ')} on the server`
    : null;

  const active = jobs.find((j) => j.id === activeId) ?? jobs[0];
  const activeRunning = active?.state === 'Running' || active?.state === 'Pending';
  const progressPct = active && active.summary.total > 0
    ? Math.min(100, Math.round((100 * active.summary.done) / active.summary.total))
    : 0;

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
          if (evt.done !== undefined && evt.total !== undefined) {
            setJobs((prev) => prev.map((j) =>
              j.id === job.id
                ? { ...j, summary: { ...j.summary, done: evt.done!, total: evt.total!, message: evt.label ?? j.summary.message } }
                : j));
          }
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
      <header className={styles.hero}>
        <div>
          <h3>Chess Lab</h3>
          <Muted>Run experiments against the substrate and external engines — every result streams live below.</Muted>
        </div>
      </header>

      {catalog && (
        <section className={styles.engineBar} aria-label="Server engine status">
          <span className={styles.engineBarLabel}>Server tools</span>
          {Object.entries(engines).map(([name, e]) => (
            <Tooltip key={name}>
              <TooltipTrigger asChild>
                <Chip variant={e.found ? 'engineOk' : 'engineMissing'}>
                  {ENGINE_LABELS[name] ?? name} {e.found ? '✓' : '✗'}
                </Chip>
              </TooltipTrigger>
              <TooltipContent>
                {e.found ? e.path : 'Not found — check deploy/secrets/chess-lab.env'}
                {e.source ? ` (${e.source})` : ''}
              </TooltipContent>
            </Tooltip>
          ))}
          {Object.keys(engines).length === 0 && <Muted>No engine paths reported by the server.</Muted>}
        </section>
      )}

      {err && <Alert>{err}</Alert>}

      <LichessPanel />

      <Panel title="Choose an experiment">
        {LAB_CATEGORIES.map((cat) => (
          <div key={cat.id} className={styles.categoryBlock}>
            <div className={styles.categoryHead}>
              <strong>{cat.label}</strong>
              <Muted>{cat.blurb}</Muted>
            </div>
            <div className={styles.experimentGrid} role="listbox" aria-label={cat.label}>
              {experimentsInCategory(cat.id).map((exp) => (
                <ExperimentCard
                  key={exp.kind}
                  exp={exp}
                  selected={kind === exp.kind}
                  blocked={requiresFor(exp).some((n) => !engines[n]?.found)}
                  onSelect={() => setKind(exp.kind)}
                />
              ))}
            </div>
          </div>
        ))}
      </Panel>

      {experiment && (
        <Panel className={styles.detailPanel}>
          <div className={styles.detailHead}>
            <div>
              <h4 className={styles.detailTitle}>{experiment.title}</h4>
              <p className={styles.detailTagline}>{experiment.tagline}</p>
            </div>
            <div className={styles.badges}>
              {experiment.recordsLive && <Chip variant="engineOk">Records live</Chip>}
              {!experiment.recordsLive && <Chip>Read-only</Chip>}
              {blockedReason && requiresFor(experiment).length > 0 && (
                <Chip variant="engineMissing">Missing deps</Chip>
              )}
            </div>
          </div>
          <p className={styles.detailDesc}>{experiment.description}</p>
          <div className={styles.detailCols}>
            <div>
              <h5 className={styles.detailSubhead}>What to expect</h5>
              <ul className={styles.detailList}>
                {experiment.expect.map((line) => <li key={line}>{line}</li>)}
              </ul>
            </div>
            <div>
              <h5 className={styles.detailSubhead}>Tips</h5>
              <ul className={styles.detailList}>
                {experiment.tips.map((line) => <li key={line}>{line}</li>)}
              </ul>
            </div>
          </div>
        </Panel>
      )}

      <Panel title="Parameters">
        {fields.length === 0 ? (
          <Muted>No parameters — this experiment reads directly from the substrate.</Muted>
        ) : (
          <div className={styles.form}>
            {fields.map((f) => (
              <LabField key={f.key} field={f} value={params[f.key] ?? ''}
                        onChange={(v) => setParams((p) => ({ ...p, [f.key]: v }))} />
            ))}
          </div>
        )}
        <div className={styles.actions}>
          {blockedReason ? (
            <Tooltip>
              <TooltipTrigger asChild>
                <Button onClick={() => void startJob()} visuallyDisabled>
                  {starting ? 'Starting…' : activeRunning ? 'Running…' : 'Start experiment'}
                </Button>
              </TooltipTrigger>
              <TooltipContent>{blockedReason}</TooltipContent>
            </Tooltip>
          ) : (
            <Button onClick={() => void startJob()} disabled={starting || activeRunning} loading={starting}>
              {starting ? 'Starting…' : activeRunning ? 'Running…' : 'Start experiment'}
            </Button>
          )}
          <Button variant="ghost" onClick={() => void stopJob()} disabled={!activeRunning}>Stop</Button>
          {blockedReason && <span className={styles.blocked}>{blockedReason}</span>}
        </div>
      </Panel>

      {jobs.length > 0 && (
        <Panel className={styles.jobs} title="Recent jobs">
          <ul className={styles.jobList}>
            {jobs.map((j) => {
              const title = experimentFor(j.kind)?.title ?? j.kind;
              return (
                <li key={j.id} className={j.id === active?.id ? styles.jobItemSelected : undefined}>
                  <Button variant="ghost" className={styles.jobBtn} onClick={() => openJob(j)}>
                    <span className={stateStyle(j.state)}>{j.state}</span>
                    <span className={styles.jobKind}>{title}</span>
                    <Muted className={styles.jobSummary}>{j.summary.message ?? `${j.summary.done}/${j.summary.total}`}</Muted>
                  </Button>
                </li>
              );
            })}
          </ul>
        </Panel>
      )}

      {active && (
        <Panel title={<>{experimentFor(active.kind)?.title ?? active.kind} <Muted className={stateStyle(active.state)}>{active.state}</Muted></>}>
          {active.summary.total > 0 && (
            <div className={styles.progressWrap} aria-label="Job progress">
              <div className={styles.progressBar} style={{ width: `${progressPct}%` }} />
              <span className={styles.progressLabel}>{active.summary.done} / {active.summary.total} ({progressPct}%)</span>
            </div>
          )}
          {active.summary.message && <Muted>{active.summary.message}</Muted>}
          {active.artifacts?.['games.pgn'] && (
            <div className={styles.artifactRow}>
              <a href={`/chess/lab/jobs/${active.id}/artifact/games.pgn`} download>Download games.pgn</a>
              {experimentFor(active.kind)?.recordsLive ? (
                <Muted>Games were recorded to substrate during the run. PGN is for archival.</Muted>
              ) : (
                <Button onClick={() => apiPost(`/chess/lab/jobs/${active.id}/ingest`, {})}>Ingest PGN to substrate</Button>
              )}
            </div>
          )}
        </Panel>
      )}

      <Panel className={styles.feed} title="Live feed">
        <Muted className={styles.feedHint}>Logs, progress, metrics, and result tables appear here as the job runs.</Muted>
        <ul className={styles.events}>
          {events.map((e, i) => <LabRow key={i} e={e} />)}
          {events.length === 0 && <li><Muted>Select a job or start an experiment to see live output.</Muted></li>}
        </ul>
      </Panel>
    </div>
  );
}

function ExperimentCard({
  exp,
  selected,
  blocked,
  onSelect,
}: {
  exp: LabExperiment;
  selected: boolean;
  blocked: boolean;
  onSelect: () => void;
}) {
  return (
    <button
      type="button"
      role="option"
      aria-selected={selected}
      className={[styles.experimentCard, selected && styles.experimentCardSelected, blocked && styles.experimentCardBlocked].filter(Boolean).join(' ')}
      onClick={onSelect}
    >
      <span className={styles.experimentTitle}>{exp.title}</span>
      <span className={styles.experimentTagline}>{exp.tagline}</span>
      {exp.recordsLive && <span className={styles.experimentBadge}>records</span>}
      {blocked && <span className={styles.experimentBadgeWarn}>needs setup</span>}
    </button>
  );
}

function LabField({ field, value, onChange }: { field: FieldDef; value: string; onChange: (v: string) => void }) {
  if (field.type === 'bool') {
    const on = value === 'true';
    return (
      <Field label={field.label} help={field.help} layout="row" className={styles.fieldWide}>
        <Toggle checked={on} onCheckedChange={(checked) => onChange(checked ? 'true' : 'false')} />
      </Field>
    );
  }

  if (field.type === 'select') {
    const labeled = field.optionLabels
      ? field.options.map((o) => ({ value: o, label: field.optionLabels![o] ?? o }))
      : field.options;
    if (labeled.length <= 3 && !field.optionLabels) {
      return (
        <Field label={field.label} help={field.help} className={styles.fieldWide}>
          <SegmentedControl value={value} onValueChange={onChange} options={field.options} label={field.label} />
        </Field>
      );
    }
    return (
      <Field label={field.label} help={field.help} className={styles.fieldWide}>
        <SegmentedControl
          value={value}
          onValueChange={onChange}
          options={labeled.map((o) => (typeof o === 'string' ? o : o.value))}
          label={field.label}
        />
        {field.optionLabels && (
          <Muted className={styles.fieldHint}>{field.optionLabels[value] ?? value}</Muted>
        )}
      </Field>
    );
  }

  if (field.type === 'number') {
    if (field.max !== undefined) {
      return (
        <Field label={field.label} help={field.help} valueDisplay={`${value || '—'}${field.unit ?? ''}`}>
          <SliderField min={field.min} max={field.max} step={field.step ?? 1} value={value} onChange={onChange} />
        </Field>
      );
    }
    return (
      <Field label={field.label} help={field.help}>
        <Input type="number" min={field.min} step={field.step ?? 1} value={value} placeholder={field.placeholder}
               onChange={(e) => onChange(e.target.value)} />
      </Field>
    );
  }

  return (
    <Field label={field.label} help={field.help} className={styles.fieldWide}>
      <Input type={field.type === 'password' ? 'password' : 'text'} value={value} placeholder={field.placeholder}
             onChange={(e) => onChange(e.target.value)} />
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
