import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Alert,
  Button,
  Chip,
  Muted,
  Panel,
  Tooltip,
  TooltipContent,
  TooltipTrigger,
  useVisiblePolling,
} from '@ui';
import { apiGet, apiPost } from '../../../api/client';
import { type LabBoardState } from '../LiveBoard';
import { Terminal } from '../Terminal';
import { streamLabEvents, type LabCatalog, type LabEvent, type LabJob } from '../sse';
import { normalizeKind } from '../experiments';
import { DEFAULT_SETUP, ENGINES, RUNNING, type GameRow, type Preview, type Setup } from './gauntletSetup';
import { GauntletSetupForm } from './GauntletSetupForm';
import { GauntletWatch, stateClass } from './GauntletParts';
import styles from './GauntletView.module.css';

export function GauntletView() {
  const [engines, setEngines] = useState<LabCatalog['engines']>({});
  const [connections, setConnections] = useState<NonNullable<LabCatalog['connections']>>([]);
  const [setup, setSetup] = useState<Setup>(DEFAULT_SETUP);
  const [preview, setPreview] = useState<Preview | null>(null);
  const [jobs, setJobs] = useState<LabJob[]>([]);
  const [activeId, setActiveId] = useState<string | null>(null);
  const [logs, setLogs] = useState<{ level: string; message: string }[]>([]);
  const [games, setGames] = useState<GameRow[]>([]);
  const [metrics, setMetrics] = useState<Record<string, number>>({});
  const [boards, setBoards] = useState<Record<number, LabBoardState>>({});
  const [lastGame, setLastGame] = useState<number | null>(null);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const streamRef = useRef<AbortController | null>(null);

  const gauntlets = useMemo(
    () => jobs.filter((j) => normalizeKind(j.kind) === 'cutechess'),
    [jobs],
  );
  const active = gauntlets.find((j) => j.id === activeId) ?? null;
  const running = active !== null && RUNNING.has(active.state);
  const required = setup.conductor === 'fastchess'
    ? [{ key: 'fastchess', label: 'Fastchess', fix: 'LAPLACE_FASTCHESS' }, ...ENGINES]
    : [{ key: 'cutechess', label: 'Cutechess', fix: 'LAPLACE_CUTECHESS' }, ...ENGINES,
      { key: 'qt', label: 'Qt runtime', fix: 'LAPLACE_QT_BIN' }];
  const connectionFor = (key: string) => connections.find(p => p.id ===
    (key === 'laplaceUci' ? setup.laplaceProfile : key === 'stockfish' ? setup.stockfishProfile : undefined));
  const missing = required.filter((r) => !connectionFor(r.key) && !engines[r.key]?.found);

  const refresh = useCallback(async () => {
    try {
      const [catalog, list] = await Promise.all([
        apiGet<LabCatalog>('/chess/lab/catalog'),
        apiGet<LabJob[]>('/chess/lab/jobs'),
      ]);
      setEngines(catalog.engines ?? {});
      setConnections(catalog.connections ?? []);
      setJobs(list);
      setErr(null);
      return list;
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
      return null;
    }
  }, []);

  useEffect(() => { void refresh(); }, [refresh]);
  useEffect(() => () => streamRef.current?.abort(), []);

  // A run outlives any one SSE connection: the job record is the source of truth for state
  // and score, so poll it while something is in flight rather than trusting the stream to
  // stay up for an hour.
  useVisiblePolling(refresh, { intervalMs: 4000, enabled: running, immediate: false });

  // The command preview comes from the server so it shows resolved binary paths and cannot
  // drift from CutechessRunner.BuildArguments. Debounced — the sliders move continuously.
  useEffect(() => {
    const q = new URLSearchParams({
      laplaceProfile: setup.laplaceProfile,
      stockfishProfile: setup.stockfishProfile,
      conductor: setup.conductor,
      rounds: setup.rounds || '2',
      depth: setup.clock === 'depth' ? setup.depth || '1' : '0',
      st: setup.clock === 'seconds' ? setup.st || '1' : '0',
      elo: setup.elo || '2000',
      limitStrength: String(setup.limitStrength),
      concurrency: setup.concurrency || '1',
      stockfishThreads: setup.stockfishThreads,
      stockfishHashMb: setup.stockfishHashMb,
      stockfishNumaPolicy: setup.stockfishNumaPolicy,
      stockfishSyzygyPath: setup.stockfishSyzygyPath,
    });
    const timer = setTimeout(() => {
      void apiGet<Preview>(`/chess/lab/cutechess/preview?${q}`)
        .then(setPreview)
        .catch((e) => { setPreview(null); setErr(e instanceof Error ? e.message : String(e)); });
    }, 180);
    return () => clearTimeout(timer);
  }, [setup]);

  const openJob = useCallback((jobId: string) => {
    setActiveId(jobId);
    setLogs([]);
    setGames([]);
    setMetrics({});
    setBoards({});
    setLastGame(null);
    streamRef.current?.abort();
    const ac = new AbortController();
    streamRef.current = ac;

    void (async () => {
      try {
        for await (const evt of streamLabEvents(jobId, ac.signal)) apply(evt);
      } catch { /* aborted, or the stream closed with the job */ }
      void refresh();
    })();

    function apply(evt: LabEvent) {
      if (evt.fen !== undefined && evt.game !== undefined) {
        const board: LabBoardState = {
          game: evt.game, ply: evt.ply ?? 0, uci: evt.uci ?? '',
          fen: evt.fen, white: evt.white, black: evt.black,
        };
        setBoards((prev) => ({ ...prev, [board.game]: board }));
        setLastGame(board.game);
        return;
      }
      if (evt.name !== undefined && evt.value !== undefined) {
        setMetrics((prev) => ({ ...prev, [evt.name!]: evt.value! }));
        return;
      }
      if (evt.result !== undefined && evt.index !== undefined) {
        setGames((prev) => [
          ...prev.filter((g) => g.index !== evt.index),
          { index: evt.index!, white: evt.white ?? '?', black: evt.black ?? '?', result: evt.result! },
        ].sort((a, b) => a.index - b.index));
        return;
      }
      if (evt.level !== undefined && evt.message !== undefined) {
        setLogs((prev) => [...prev.slice(-60), { level: evt.level!, message: evt.message! }]);
        return;
      }
      if (evt.finalState !== undefined) {
        setLogs((prev) => [...prev.slice(-60), {
          level: evt.finalState === 'Completed' ? 'info' : 'error',
          message: `run ${String(evt.finalState).toLowerCase()}${evt.message ? ` — ${evt.message}` : ''}`,
        }]);
        void refresh();
      }
    }
  }, [refresh]);

  const start = async () => {
    if (busy || running || missing.length > 0) return;
    const rounds = Number(setup.rounds);
    if (!Number.isInteger(rounds) || rounds < 2 || rounds % 2 !== 0) {
      setErr('Games must be an even number of at least 2 so every opening is played once from each color.');
      return;
    }
    for (const [label, value] of [
      ['Stockfish threads', setup.stockfishThreads],
      ['Stockfish hash', setup.stockfishHashMb],
    ]) {
      if (value.trim() && (!Number.isInteger(Number(value)) || Number(value) < 1)) {
        setErr(`${label} must be a positive whole number, or blank to use the engine default.`);
        return;
      }
    }
    setBusy(true);
    setErr(null);
    try {
      const config: Record<string, string> = {
        laplaceProfile: setup.laplaceProfile,
        stockfishProfile: setup.stockfishProfile,
        conductor: setup.conductor,
        rounds: setup.rounds,
        elo: setup.elo || '2000',
        limitStrength: String(setup.limitStrength),
        concurrency: setup.concurrency || '1',
        stockfishThreads: setup.stockfishThreads,
        stockfishHashMb: setup.stockfishHashMb,
        stockfishNumaPolicy: setup.stockfishNumaPolicy,
        stockfishSyzygyPath: setup.stockfishSyzygyPath,
        ingest: String(setup.ingest),
        // Exactly one shared match budget reaches both engines.
        depth: setup.clock === 'depth' ? setup.depth || '1' : '0',
        st: setup.clock === 'seconds' ? setup.st || '1' : '0',
      };
      const r = await apiPost<{ jobId: string }>('/chess/lab/start', { kind: 'cutechess', config });
      openJob(r.jobId);
      await refresh();
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const stop = async () => {
    if (!active) return;
    try {
      await apiPost(`/chess/lab/stop/${active.id}`, {});
      await refresh();
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    }
  };

  const copyCommand = async () => {
    if (!preview) return;
    try {
      await navigator.clipboard.writeText(preview.commandLine);
      setCopied(true);
      setTimeout(() => setCopied(false), 1600);
    } catch { setCopied(false); }
  };

  const total = active?.summary.total || Number(setup.rounds) || 0;
  const done = active?.summary.done ?? 0;
  const pct = total > 0 ? Math.min(100, Math.round((100 * done) / total)) : 0;
  const readbackVerified = (metrics.games_committed_readback ?? 0) > 0
    && (metrics.plies_committed_readback ?? 0) > 0;

  return (
    <div className={styles.gauntlet}>
      <header className={styles.hero}>
        <div>
          <h3>Engine Gauntlet</h3>
          <Muted>
            Laplace against Stockfish, driven by the selected match conductor with paired openings, color swaps,
            captured UCI traffic, and runtime engine-identity verification.
          </Muted>
        </div>
        {active && <span className={stateClass(active.state)}>{active.state}</span>}
      </header>

      <section className={styles.readiness} aria-label="Gauntlet binaries">
        {required.map((r) => {
          const probe = engines[r.key];
          const connection = connectionFor(r.key);
          return (
            <Tooltip key={r.key}>
              <TooltipTrigger asChild>
                <Chip variant={connection ? undefined : probe?.found ? 'engineOk' : 'engineMissing'}>
                  {r.label} {connection ? '· ' + connection.host : probe?.found ? '✓' : '✗'}
                </Chip>
              </TooltipTrigger>
              <TooltipContent>
                {connection ? `${connection.label} · ${connection.environment} · ${connection.knowledge}` : probe?.found
                  ? `${probe.path} (${probe.source})`
                  : `Not installed or unavailable: ${r.fix}`}
              </TooltipContent>
            </Tooltip>
          );
        })}
      </section>

      {missing.length > 0 && (
        <Alert>
          {missing.length === 1 ? 'One binary is' : `${missing.length} binaries are`} missing, so no
          match can start with this conductor: {missing.map((m) => m.label).join(', ')}.
        </Alert>
      )}

      {err && <Alert>{err}</Alert>}

      <div className={styles.columns}>
        <div className={styles.setupCol}>
        <Panel className={styles.setup} title="Match setup">
          <GauntletSetupForm setup={setup} setSetup={setSetup} connections={connections} />

          <div className={styles.commandBlock}>
            <div className={styles.commandHead}>
              <span className={styles.commandLabel}>Command this will run</span>
              <Button size="sm" variant="ghost" onClick={() => void copyCommand()} disabled={!preview}>
                {copied ? 'Copied' : 'Copy'}
              </Button>
            </div>
            <code className={styles.command}>
              {preview ? preview.commandLine : 'resolving binaries…'}
            </code>
            {preview && <Muted className={styles.cwd}>cwd {preview.workingDirectory}</Muted>}
          </div>

          <div className={styles.actions}>
            <Button onClick={() => void start()} disabled={busy || running || missing.length > 0 || !preview?.ready} loading={busy}>
              {running ? 'Running…' : 'Start gauntlet'}
            </Button>
            <Button variant="ghost" onClick={() => void stop()} disabled={!running}>Stop</Button>
          </div>
        </Panel>

        <Panel title="Game results">
          {games.length === 0 ? (
            <Muted>Finished games appear here as the match conductor reports them.</Muted>
          ) : (
            <div className={styles.tableScroll}>
              <table className={styles.table}>
                <thead>
                  <tr><th>#</th><th>White</th><th>Black</th><th>Result</th></tr>
                </thead>
                <tbody>
                  {games.map((g) => (
                    <tr key={g.index}>
                      <td>{g.index}</td><td>{g.white}</td><td>{g.black}</td><td>{g.result}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          {logs.length > 0 && (
            <ul className={styles.notes}>
              {logs.filter((l) => l.level !== 'info').slice(-6).map((l, i) => (
                <li key={i} className={l.level === 'error' ? styles.noteError : styles.noteWarn}>
                  [{l.level}] {l.message}
                </li>
              ))}
            </ul>
          )}
        </Panel>
        </div>

        <GauntletWatch
          metrics={metrics}
          pct={pct}
          done={done}
          total={total}
          active={active}
          readbackVerified={readbackVerified}
          boards={boards}
          lastGame={lastGame}
        />
      </div>

      <Panel title="Transcript" className={styles.terminalPanel} fill>
        <Terminal jobId={activeId} command={preview?.commandLine ?? null} className={styles.terminal} />
      </Panel>

      <Panel title="Run history">
          {gauntlets.length === 0 ? (
            <Muted>No gauntlets on this server yet.</Muted>
          ) : (
            <ul className={styles.history}>
              {gauntlets.map((j) => (
                <li key={j.id}>
                  <button
                    type="button"
                    className={[styles.historyItem, j.id === activeId && styles.historyItemActive]
                      .filter(Boolean).join(' ')}
                    onClick={() => openJob(j.id)}
                  >
                    <span className={stateClass(j.state)}>{j.state}</span>
                    <span className={styles.historyId}>{j.id.slice(0, 8)}</span>
                    <Muted className={styles.historySummary}>
                      {j.summary.message ?? `${j.summary.done}/${j.summary.total}`}
                    </Muted>
                  </button>
                </li>
              ))}
            </ul>
          )}
      </Panel>
    </div>
  );
}
