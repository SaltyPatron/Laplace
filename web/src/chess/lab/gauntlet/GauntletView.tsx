import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
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
import { apiGet, apiPost } from '../../../api/client';
import { LiveBoard, type LabBoardState } from '../LiveBoard';
import { Terminal } from '../Terminal';
import { streamLabEvents, type LabCatalog, type LabEvent, type LabJob } from '../sse';
import { normalizeKind } from '../experiments';
import styles from './GauntletView.module.css';

/** The four binaries a gauntlet needs, in the order the run touches them. */
const REQUIRED: { key: string; label: string; fix: string }[] = [
  { key: 'cutechess', label: 'cutechess-cli', fix: 'LAPLACE_CUTECHESS' },
  { key: 'stockfish', label: 'Stockfish', fix: 'LAPLACE_STOCKFISH' },
  { key: 'qt', label: 'Qt runtime', fix: 'LAPLACE_QT_BIN' },
  { key: 'laplaceUci', label: 'laplace-uci', fix: 'ships beside the API host — publish it' },
];

interface Preview {
  fileName: string;
  arguments: string[];
  commandLine: string;
  workingDirectory: string;
  games: number;
  ready: boolean;
  missing: { name: string; hint: string; looked: string | null; source: string }[];
}

interface Setup {
  clock: 'seconds' | 'depth';
  st: string;
  depth: string;
  rounds: string;
  elo: string;
  limitStrength: boolean;
  concurrency: string;
  ingest: boolean;
}

const DEFAULT_SETUP: Setup = {
  clock: 'seconds',
  st: '1',
  depth: '8',
  rounds: '10',
  elo: '2000',
  limitStrength: true,
  concurrency: '1',
  ingest: true,
};

interface GameRow {
  index: number;
  white: string;
  black: string;
  result: string;
}

const RUNNING = new Set(['Running', 'Pending']);

export function GauntletView() {
  const [engines, setEngines] = useState<LabCatalog['engines']>({});
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
  const missing = REQUIRED.filter((r) => !engines[r.key]?.found);

  const refresh = useCallback(async () => {
    try {
      const [catalog, list] = await Promise.all([
        apiGet<LabCatalog>('/chess/lab/catalog'),
        apiGet<LabJob[]>('/chess/lab/jobs'),
      ]);
      setEngines(catalog.engines ?? {});
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
  useEffect(() => {
    if (!running) return;
    const t = setInterval(() => void refresh(), 4000);
    return () => clearInterval(t);
  }, [running, refresh]);

  // The command preview comes from the server so it shows resolved binary paths and cannot
  // drift from CutechessRunner.BuildArguments. Debounced — the sliders move continuously.
  useEffect(() => {
    const q = new URLSearchParams({
      rounds: setup.rounds || '1',
      depth: setup.clock === 'depth' ? setup.depth || '1' : '0',
      st: setup.clock === 'seconds' ? setup.st || '1' : '0',
      elo: setup.elo || '2000',
      limitStrength: String(setup.limitStrength),
      concurrency: setup.concurrency || '1',
    });
    const timer = setTimeout(() => {
      void apiGet<Preview>(`/chess/lab/cutechess/preview?${q}`)
        .then(setPreview)
        .catch(() => setPreview(null));
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
    setBusy(true);
    setErr(null);
    try {
      const config: Record<string, string> = {
        rounds: setup.rounds || '1',
        elo: setup.elo || '2000',
        limitStrength: String(setup.limitStrength),
        concurrency: setup.concurrency || '1',
        ingest: String(setup.ingest),
        // Exactly one clock reaches the runner: depth>0 is what selects unclocked mode.
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

  return (
    <div className={styles.gauntlet}>
      <header className={styles.hero}>
        <div>
          <h3>Engine Gauntlet</h3>
          <Muted>
            laplace-uci against Stockfish, driven by cutechess-cli. Every process line is
            captured — the command, both engines' UCI traffic, and anything either writes to stderr.
          </Muted>
        </div>
        {active && <span className={stateClass(active.state)}>{active.state}</span>}
      </header>

      <section className={styles.readiness} aria-label="Gauntlet binaries">
        {REQUIRED.map((r) => {
          const probe = engines[r.key];
          return (
            <Tooltip key={r.key}>
              <TooltipTrigger asChild>
                <Chip variant={probe?.found ? 'engineOk' : 'engineMissing'}>
                  {r.label} {probe?.found ? '✓' : '✗'}
                </Chip>
              </TooltipTrigger>
              <TooltipContent>
                {probe?.found
                  ? `${probe.path} (${probe.source})`
                  : `Not found. Set ${r.fix} in deploy/secrets/chess-lab.env`}
              </TooltipContent>
            </Tooltip>
          );
        })}
      </section>

      {missing.length > 0 && (
        <Alert>
          {missing.length === 1 ? 'One binary is' : `${missing.length} binaries are`} missing, so no
          gauntlet can start: {missing.map((m) => `${m.label} (${m.fix})`).join(', ')}. Build them with
          scripts/bootstrap-chess-lab.sh, or point chess-lab.env at existing ones.
        </Alert>
      )}
      {err && <Alert>{err}</Alert>}

      <div className={styles.columns}>
        <div className={styles.setupCol}>
        <Panel className={styles.setup} title="Match setup">
          <div className={styles.form}>
            <Field
              label="Clock"
              help="Per-move seconds is watchable. Fixed depth has no clock at all — one move can take minutes."
              className={styles.wide}
            >
              <SegmentedControl
                value={setup.clock}
                onValueChange={(v) => setSetup((s) => ({ ...s, clock: v as Setup['clock'] }))}
                options={['seconds', 'depth']}
                label="Clock"
              />
            </Field>

            {setup.clock === 'seconds' ? (
              <Field label="Seconds per move" valueDisplay={`${setup.st}s`} help="cutechess st, with a 2s time margin.">
                <SliderField
                  min={0.1}
                  max={10}
                  step={0.1}
                  value={setup.st}
                  label="Seconds per move"
                  onChange={(v) => setSetup((s) => ({ ...s, st: v }))}
                />
              </Field>
            ) : (
              <Field label="Search depth" valueDisplay={setup.depth} help="Sent to both engines as go depth N.">
                <SliderField
                  min={1}
                  max={20}
                  value={setup.depth}
                  label="Search depth"
                  onChange={(v) => setSetup((s) => ({ ...s, depth: v }))}
                />
              </Field>
            )}

            {/* cutechess's -rounds IS the game count for a two-engine match; colours
                alternate between games on their own. Calling it "rounds" in the UI is what
                made everyone assume it doubled. */}
            <Field label="Games" help="Colours alternate each game.">
              <Input
                type="number"
                min={1}
                value={setup.rounds}
                aria-label="Games"
                onChange={(e) => setSetup((s) => ({ ...s, rounds: e.target.value }))}
              />
            </Field>

            <Field label="Stockfish strength" help="Full strength disables UCI_LimitStrength; Elo estimates depend on the engine version and time control.">
              <Toggle
                checked={setup.limitStrength}
                onCheckedChange={(value) => setSetup((s) => ({ ...s, limitStrength: value }))}
                aria-label="Limit Stockfish strength"
              />
            </Field>
            <Field label="Stockfish Elo cap" help="2000 is a default, not a fixed level. The installed engine reports its supported range in the transcript.">
              <Input
                type="number"
                min={100}
                step={50}
                value={setup.elo}
                disabled={!setup.limitStrength}
                aria-label="Stockfish Elo cap"
                onChange={(e) => setSetup((s) => ({ ...s, elo: e.target.value }))}
              />
            </Field>

            <Field label="Games in flight" help="1 keeps the transcript and the live board readable.">
              <Input
                type="number"
                min={1}
                value={setup.concurrency}
                aria-label="Games in flight"
                onChange={(e) => setSetup((s) => ({ ...s, concurrency: e.target.value }))}
              />
            </Field>

            <Field
              label="Ingest to substrate"
              help="laplace-uci cannot record its own plies, so the PGN is re-ingested when the run completes."
              layout="row"
              className={styles.wide}
            >
              <Toggle
                checked={setup.ingest}
                onCheckedChange={(v) => setSetup((s) => ({ ...s, ingest: v }))}
                aria-label="Ingest to substrate"
              />
            </Field>
          </div>

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
            <Button onClick={() => void start()} disabled={busy || running || missing.length > 0} loading={busy}>
              {running ? 'Running…' : 'Start gauntlet'}
            </Button>
            <Button variant="ghost" onClick={() => void stop()} disabled={!running}>Stop</Button>
          </div>
        </Panel>

        <Panel title="Game results">
          {games.length === 0 ? (
            <Muted>Finished games land here as cutechess adjudicates them.</Muted>
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

        <div className={styles.watch}>
          <Panel title="Score">
            <div className={styles.scoreboard}>
              <Stat label="Laplace wins" value={metrics.wins} tone="win" />
              <Stat label="Draws" value={metrics.draws} />
              <Stat label="Losses" value={metrics.losses} tone="loss" />
              <Stat
                label="Elo diff"
                value={metrics.elo_diff}
                // cutechess prints "inf"/"nan" until each side has won at least once, and a
                // non-finite Elo is not a number to render as one.
                title="Undefined until both engines have won a game"
                format={(v) => `${v >= 0 ? '+' : ''}${v.toFixed(0)}`}
              />
            </div>
            <div className={styles.progressWrap} aria-label="Games played">
              <div className={styles.progressBar} style={{ width: `${pct}%` }} />
              <span className={styles.progressLabel}>
                {done} / {total} games{active?.summary.message ? ` · ${active.summary.message}` : ''}
              </span>
            </div>
            {active?.artifacts?.['games.pgn'] && (
              <div className={styles.artifact}>
                <a href={`/chess/lab/jobs/${active.id}/artifact/games.pgn`} download>games.pgn</a>
                <Muted>
                  {metrics.games_ingested !== undefined
                    ? `${metrics.games_ingested} games ingested to substrate`
                    : 'not yet ingested'}
                </Muted>
              </div>
            )}
          </Panel>

          <Panel title="Live board" className={styles.boardPanel}>
            {lastGame !== null
              ? <LiveBoard boards={boards} lastGame={lastGame} />
              : <Muted>The board fills in from the engines' own UCI traffic once a game starts.</Muted>}
          </Panel>
        </div>
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

function Stat({
  label,
  value,
  tone,
  title,
  format,
}: {
  label: string;
  value: number | undefined;
  tone?: 'win' | 'loss';
  title?: string;
  format?: (v: number) => string;
}) {
  return (
    <div className={styles.stat} title={value === undefined ? title : undefined}>
      <span className={styles.statLabel}>{label}</span>
      <b className={[styles.statValue, tone === 'win' && styles.statWin, tone === 'loss' && styles.statLoss]
        .filter(Boolean).join(' ')}>
        {value === undefined ? '—' : format ? format(value) : value}
      </b>
    </div>
  );
}

const STATE_CLASS: Record<string, string> = {
  Running: styles.stateRunning,
  Pending: styles.stateRunning,
  Completed: styles.stateCompleted,
  Failed: styles.stateFailed,
  Cancelled: styles.stateCancelled,
};

function stateClass(state: string): string {
  return [styles.state, STATE_CLASS[state]].filter(Boolean).join(' ');
}
