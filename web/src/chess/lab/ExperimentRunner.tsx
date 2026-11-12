import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiGet, apiPost } from '../../api/client';
import {
  Alert,
  Button,
  Chip,
  Muted,
  Panel,
  SegmentedControl,
  Tooltip,
  TooltipContent,
  TooltipTrigger,
} from '@ui';
import {
  ENGINE_LABELS,
  experimentFor,
  experimentsInCategory,
  LAB_CATEGORIES,
  LAB_EXPERIMENTS,
  type LabCategory,
} from './experiments';
import { streamLabEvents, type LabEvent, type LabJob, type LabCatalog } from './sse';
import { LiveBoard, type LabBoardState } from './LiveBoard';
import { FALLBACK_JOBS, JOB_FIELDS, paramsFor, requiresFor } from './jobFields';
import { ExperimentCard, LabField, LabRow, stateStyle } from './ExperimentParts';
import styles from './ExperimentRunner.module.css';

export interface ExperimentRunnerProps {
  /** Lab categories shown here; each experiment kind belongs to exactly one category. */
  categories: LabCategory[];
  initialKind: string;
}

/**
 * Pick an experiment from the given categories, set its parameters, start it as a server
 * job, and stream its feed.
 */
export function ExperimentRunner({ categories, initialKind }: ExperimentRunnerProps) {
  const importSurface = categories.length === 1 && categories[0] === 'import';
  const [catalog, setCatalog] = useState<LabCatalog | null>(null);
  const [jobs, setJobs] = useState<LabJob[]>([]);
  const [activeId, setActiveId] = useState<string | null>(null);
  const [events, setEvents] = useState<LabEvent[]>([]);
  const [boards, setBoards] = useState<Record<number, LabBoardState>>({});
  const [lastGame, setLastGame] = useState<number | null>(null);
  const [kind, setKind] = useState(initialKind);
  const [preferredProvider, setPreferredProvider] = useState<'chesscom' | 'lichess'>('chesscom');
  const [params, setParams] = useState<Record<string, string>>(() => paramsFor(initialKind, undefined));
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
    const next = paramsFor(kind, activeSpec);
    if (kind === 'player-profile' || kind === 'lichess-fetch') next.site = preferredProvider;
    setParams(next);
    if (catalog) seededRef.current = kind;
  }, [kind, catalog, activeSpec, preferredProvider]);

  const importSource = kind.startsWith('fide-') ? 'fide' : (params.site === 'lichess' ? 'lichess' : 'chesscom');
  const chooseImportSource = (source: string) => {
    if (source === 'fide') {
      setKind('fide-search');
      return;
    }
    const provider = source === 'lichess' ? 'lichess' : 'chesscom';
    setPreferredProvider(provider);
    const nextKind = kind === 'lichess-fetch' || kind === 'player-profile' ? kind : 'player-profile';
    setKind(nextKind);
    setParams((current) => ({
      ...(nextKind === kind ? current : paramsFor(nextKind, jobSpecs.find((job) => job.kind === nextKind))),
      site: provider,
    }));
  };

  const engines = catalog?.engines ?? {};
  const relevantEngines = useMemo(() => {
    const needed = new Set(
      LAB_EXPERIMENTS.filter((e) => categories.includes(e.category)).flatMap((e) => e.requires ?? []),
    );
    return Object.entries(engines).filter(([name]) => needed.has(name));
  }, [engines, categories]);
  const missingEngines = requiresFor(experiment).filter((name) => !engines[name]?.found);
  const blockedReason = missingEngines.length > 0
    ? `Install ${missingEngines.map((n) => ENGINE_LABELS[n] ?? n).join(', ')} on the server`
    : kind === 'fide-search' && (params.query ?? '').trim().length < 2
      ? 'Enter at least two characters for a FIDE search'
      : null;

  // Jobs whose kind belongs to one of this runner's categories.
  const visibleJobs = useMemo(
    () => jobs.filter((j) => {
      const exp = experimentFor(j.kind);
      return exp !== undefined && categories.includes(exp.category);
    }),
    [jobs, categories],
  );
  const active = visibleJobs.find((j) => j.id === activeId) ?? visibleJobs[0];
  const activeRunning = active?.state === 'Running' || active?.state === 'Pending';
  const progressPct = active && active.summary.total > 0
    ? Math.min(100, Math.round((100 * active.summary.done) / active.summary.total))
    : 0;

  const openJob = useCallback((job: LabJob) => {
    setActiveId(job.id);
    setEvents([]);
    setBoards({});
    setLastGame(null);
    esRef.current?.abort();
    const ac = new AbortController();
    esRef.current = ac;
    void (async () => {
      try {
        for await (const evt of streamLabEvents(job.id, ac.signal)) {
          if (evt.fen !== undefined && evt.game !== undefined) {
            // Board frames arrive at ~4/s/game — route them to the live board, not the log feed.
            const b: LabBoardState = {
              game: evt.game, ply: evt.ply ?? 0, uci: evt.uci ?? '',
              fen: evt.fen, white: evt.white, black: evt.black,
            };
            setBoards((prev) => ({ ...prev, [b.game]: b }));
            setLastGame(b.game);
            continue;
          }
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

  const startTableAction = async (actionKind: string, config: Record<string, string>) => {
    if (starting || activeRunning) return;
    setStarting(true);
    setErr(null);
    try {
      const r = await apiPost<{ jobId: string }>('/chess/lab/start', { kind: actionKind, config });
      setKind(actionKind);
      openJob({ id: r.jobId, kind: actionKind, state: 'Pending', summary: { done: 0, total: 0 }, artifacts: {} });
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
    <div className={styles.runner}>
      {/* Server binary readiness, shown only for binaries an experiment in these categories requires. */}
      {catalog && relevantEngines.length > 0 && (
        <section className={styles.engineBar} aria-label="Server engine status">
          <span className={styles.engineBarLabel}>Server tools</span>
          {relevantEngines.map(([name, e]) => (
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

        </section>
      )}

      {err && <Alert>{err}</Alert>}

      {importSurface && (
        <Panel title="Choose a source">
          <SegmentedControl
            value={importSource}
            onValueChange={chooseImportSource}
            options={['fide', 'chesscom', 'lichess']}
            optionLabels={{ fide: 'FIDE', chesscom: 'CHESS.COM', lichess: 'LICHESS' }}
            label="Profile source"
          />
          <Muted className={styles.sourceHelp}>
            Importing the same unchanged profile or game again is idempotent; only new provider facts or games are witnessed.
          </Muted>
        </Panel>
      )}

      <Panel title="Choose an experiment">
        {LAB_CATEGORIES.filter((cat) => categories.includes(cat.id)).map((cat) => (
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
                  {starting ? 'Starting…' : activeRunning ? 'Running…' : importSurface ? 'Import' : 'Start experiment'}
                </Button>
              </TooltipTrigger>
              <TooltipContent>{blockedReason}</TooltipContent>
            </Tooltip>
          ) : (
            <Button onClick={() => void startJob()} disabled={starting || activeRunning} loading={starting}>
              {starting ? 'Starting…' : activeRunning ? 'Running…' : importSurface ? 'Import' : 'Start experiment'}
            </Button>
          )}
          <Button variant="ghost" onClick={() => void stopJob()} disabled={!activeRunning}>Stop</Button>
          {blockedReason && <span className={styles.blocked}>{blockedReason}</span>}
        </div>
      </Panel>

      {visibleJobs.length > 0 && (
        <Panel className={styles.jobs} title="Recent jobs">
          <ul className={styles.jobList}>
            {visibleJobs.map((j) => {
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
              ) : active.kind.toLowerCase().replaceAll('-', '') === 'lichessfetch' && active.summary.message?.includes('new games') ? (
                <Muted>Games were ingested during this run. PGN is retained as the source artifact.</Muted>
              ) : (
                <Button onClick={() => apiPost(`/chess/lab/jobs/${active.id}/ingest`, {})}>Ingest PGN to substrate</Button>
              )}
            </div>
          )}
        </Panel>
      )}

      {lastGame !== null && (
        <Panel title="Live board">
          <LiveBoard boards={boards} lastGame={lastGame} />
        </Panel>
      )}

      <Panel className={styles.feed} title="Live feed">
        <Muted className={styles.feedHint}>Logs, progress, metrics, and result tables appear here as the job runs.</Muted>
        {/* The feed scrolls, so it needs to be reachable by keyboard — an
            overflow region with no tabindex traps its content for anyone not
            using a pointer (axe: scrollable-region-focusable). */}
        {/* tabIndex only — a role here would override the list semantics and
            orphan the <li> children (axe: listitem). */}
        <ul className={styles.events} tabIndex={0} aria-label="Live feed">
          {events.map((e, i) => <LabRow key={i} e={e} onAction={startTableAction} />)}
          {events.length === 0 && <li><Muted>Select a job or start an experiment to see live output.</Muted></li>}
        </ul>
      </Panel>
    </div>
  );
}
