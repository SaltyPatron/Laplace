import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError, apiGet, apiPost } from '../../api/client';
import { Alert, Chip, Field, Input, Muted, Panel, Toggle, useVisiblePolling } from '@ui';
import styles from './LichessPanel.module.css';

/** What /chess/lichess/status answers (LichessStatusView): one state, never a guess. */
export type LichessState =
  | 'listening' | 'connecting' | 'token-missing' | 'account-not-ready' | 'failed'
  | 'restarting' | 'starting' | 'stopped' | 'not-installed' | 'unreachable';

export interface LichessServiceView {
  unit: string;
  loadState: string;
  activeState: string;
  subState: string;
  enabled: string;
  operatorStopped: boolean;
}

export interface LichessStatus {
  state: LichessState;
  /** The managed service answered this poll. */
  reachable: boolean;
  /** The managed service is meant to run (null: unknown). A crash loop does not change it. */
  desired?: boolean | null;
  /** Null when the service did not answer: unknown, not missing. */
  configured?: boolean | null;
  tokenPreview?: string | null;
  connected: boolean;
  running?: boolean;
  username?: string | null;
  engine: string;
  engineIdentity?: { name: string; exeSha256: string; via?: { name: string; exeSha256: string } | null } | null;
  maxConcurrent: number;
  gamesRecorded: number;
  recentLog: string[];
  error?: string | null;
  account?: { ready?: boolean; error?: string | null } | null;
  service?: LichessServiceView | null;
  lastSeenAt?: string | null;
}

interface ChatLine {
  gameId: string;
  room: string;
  username: string;
  text: string;
}

type ChipVariant = 'default' | 'engineOk' | 'engineMissing' | 'refute';

const STATE_CHIP: Record<LichessState, { label: string; variant: ChipVariant }> = {
  listening: { label: '● Listening', variant: 'engineOk' },
  connecting: { label: '◌ Connecting', variant: 'default' },
  'token-missing': { label: '✗ No token', variant: 'engineMissing' },
  'account-not-ready': { label: '✗ Account not ready', variant: 'engineMissing' },
  failed: { label: '✗ Failed', variant: 'refute' },
  restarting: { label: '↻ Restarting after failure', variant: 'refute' },
  starting: { label: '◌ Service starting', variant: 'default' },
  stopped: { label: '○ Stopped', variant: 'default' },
  'not-installed': { label: '✗ Service not installed', variant: 'engineMissing' },
  unreachable: { label: '✗ Service unreachable', variant: 'engineMissing' },
};

/** States in which the managed service is on (the toggle's truth when the API cannot say `desired`). */
const ON_STATES: ReadonlySet<LichessState> = new Set([
  'listening', 'connecting', 'token-missing', 'account-not-ready', 'failed', 'restarting', 'starting',
]);
const PENDING_TIMEOUT_MS = 60_000;

interface Pending { target: boolean; since: number }

function isOn(s: LichessStatus): boolean {
  return s.desired ?? ON_STATES.has(s.state);
}

function actionError(e: unknown): string {
  if (e instanceof ApiError && e.status === 503) {
    return 'The API could not control the managed Lichess service. On Windows the site needs the service rights '
      + 'that scripts\\win\\ensure-managed-services.cmd (run elevated) grants; on Linux, the laplace-service-control helper.';
  }
  if (e instanceof ApiError && e.status === 409) return 'A managed deployment is in progress; try again when it finishes.';
  return e instanceof Error ? e.message : String(e);
}

export function LichessPanel() {
  const [status, setStatus] = useState<LichessStatus | null>(null);
  const [pending, setPending] = useState<Pending | null>(null);
  const [pollErr, setPollErr] = useState<string | null>(null);
  const [actionErr, setActionErr] = useState<string | null>(null);
  const [activeGameId, setActiveGameId] = useState<string | null>(null);
  const [chat, setChat] = useState<ChatLine[]>([]);
  // A poll that began before an action must not overwrite what the action showed.
  const epoch = useRef(0);
  const pendingRef = useRef<Pending | null>(null);
  pendingRef.current = pending;

  const refresh = useCallback(async () => {
    const mine = epoch.current;
    try {
      const s = await apiGet<LichessStatus>('/chess/lichess/status');
      if (mine !== epoch.current) return;
      setStatus(s);
      setPollErr(null);
      const p = pendingRef.current;
      if (p) {
        if (isOn(s) === p.target) setPending(null);
        else if (Date.now() - p.since > PENDING_TIMEOUT_MS) {
          setActionErr(`The service did not ${p.target ? 'start' : 'stop'} within ${PENDING_TIMEOUT_MS / 1000} s (now: ${s.state}).`);
          setPending(null);
        }
      }
    } catch (e) {
      if (mine === epoch.current) setPollErr(e instanceof Error ? e.message : String(e));
    }
  }, []);

  useEffect(() => { void refresh(); }, [refresh]);
  const state = status?.state;
  const fast = !!pending || state === 'listening' || state === 'connecting' || state === 'starting';
  useVisiblePolling(refresh, { intervalMs: fast ? 2000 : 8000, immediate: false });
  const connected = state === 'listening';
  useVisiblePolling(async () => {
    if (!activeGameId || !connected) return;
    try {
      const lines = await apiGet<ChatLine[]>(`/chess/lichess/games/${activeGameId}/chat`);
      setChat(lines);
    } catch { /* keep the last chat body */ }
  }, {
    intervalMs: 2500,
    enabled: !!activeGameId && connected,
  });

  const setListening = async (on: boolean) => {
    if (pendingRef.current) return;
    epoch.current += 1;
    setActionErr(null);
    const next = { target: on, since: Date.now() };
    pendingRef.current = next;
    setPending(next);
    try {
      await apiPost(on ? '/chess/lichess/start' : '/chess/lichess/stop', {});
      if (!on) {
        setActiveGameId(null);
        setChat([]);
      }
    } catch (e) {
      setActionErr(actionError(e));
      pendingRef.current = null;
      setPending(null);
    }
    await refresh();
  };

  const chip = state ? STATE_CHIP[state] : { label: '… reading status', variant: 'default' as ChipVariant };
  const username = status?.username;
  const checked = pending ? pending.target : status ? isOn(status) : false;
  const configured = status?.configured;
  const operatorStopped = status?.service?.operatorStopped ?? false;
  const showServerError = !!state && !!status?.error
    && !['token-missing', 'account-not-ready', 'listening', 'connecting', 'stopped'].includes(state);

  return (
    <Panel className={styles.panel} title="Lichess connectivity">
      <p className={styles.intro}>
        Listen for challenges on lichess.org. Each ply folds to consensus before the next search;
        finished games get a terminal outcome pass. One managed service runs the bot{status?.service?.unit ? <> (<code>{status.service.unit}</code>)</> : null}; this panel only starts and stops it.
      </p>

      <div className={styles.statusRow}>
        <Chip variant={chip.variant} aria-label="Lichess service state">
          {pending ? (pending.target ? '◌ Starting…' : '◌ Stopping…') : chip.label}
        </Chip>
        {configured != null && (
          <Chip variant={configured ? 'engineOk' : 'engineMissing'}>
            Token {configured ? '✓' : '✗ missing'}
          </Chip>
        )}
        {username && (
          <>
            <a
              className={styles.profileLink}
              href={`https://lichess.org/@/${username}`}
              target="_blank"
              rel="noreferrer"
            >
              @{username} on Lichess ↗
            </a>
            <Chip>{status?.gamesRecorded ?? 0} completed</Chip>
          </>
        )}
      </div>

      {state === 'token-missing' && (
        <Alert>
          The managed service is running but has no Lichess token. Set <code>LICHESS_TOKEN</code> (or{' '}
          <code>LICHESS_API</code>) in the server-side <code>deploy/secrets/lichess.env</code> and redeploy the
          managed service.
        </Alert>
      )}
      {state === 'account-not-ready' && (
        <Alert>{status?.account?.error ?? status?.error ?? 'The token is not a BOT account with bot:play.'}</Alert>
      )}
      {showServerError && <Alert>{status?.error}</Alert>}
      {state === 'stopped' && !pending && (
        <Muted>
          {operatorStopped
            ? 'Listening is off (stopped by an operator).'
            : 'Listening is off: the managed service is stopped.'}
        </Muted>
      )}
      {actionErr && <Alert>{actionErr}</Alert>}
      {pollErr && <Alert>Status unavailable from the API: {pollErr}</Alert>}

      <div className={styles.controls}>
        <Field label="Listen on Lichess" help="Accepts standard challenges while on. Challenge the bot account from lichess.org." layout="row">
          <Toggle
            checked={checked}
            disabled={!!pending || !status || state === 'not-installed'}
            onCheckedChange={(on) => void setListening(on)}
            aria-label="Listen on Lichess"
          />
        </Field>

        <Field label="Engine" help="The engine every move comes from (LAPLACE_LICHESS_ENGINE): laplace, stockfish or lc0 through laplace-uci, or a UCI executable. The game is recorded the same way whichever plays.">
          <Input
            value={status?.engineIdentity ? `${status.engine} (${status.engineIdentity.via?.name ?? status.engineIdentity.name})` : (status?.engine ?? '')}
            disabled
            readOnly
            aria-label="Engine"
          />
        </Field>

        <Field label="Max concurrent" help="Declines new challenges when this many games are active.">
          <Input
            type="number"
            min={1}
            max={8}
            value={status?.maxConcurrent || ''}
            disabled
            readOnly
            aria-label="Max concurrent"
          />
        </Field>

        <Field label="Watch game chat" help="Paste a lichess game id to poll chat lines (from stream + bot commentary).">
          <Input
            value={activeGameId ?? ''}
            disabled={!connected}
            placeholder="e.g. AbCdEfGh"
            onChange={(e) => setActiveGameId(e.target.value.trim() || null)}
          />
        </Field>
      </div>

      {connected && username && (
        <div className={styles.playHint}>
          <strong>Play now:</strong> open{' '}
          <a href={`https://lichess.org/@/${username}`} target="_blank" rel="noreferrer">
            lichess.org/@/{username}
          </a>
          , click <em>Challenge</em>, pick standard chess — the bot accepts automatically.
        </div>
      )}

      {chat.length > 0 && (
        <div className={styles.log}>
          <Muted>Game chat {activeGameId ? `(${activeGameId})` : ''}</Muted>
          {/* .log ul caps at 6rem and scrolls — without tabindex its content is
              unreachable by keyboard (axe: scrollable-region-focusable). */}
          <ul tabIndex={0} aria-label="Game chat">
            {chat.slice(-12).map((line, i) => (
              <li key={`${i}-${line.username}-${line.text}`}>
                <strong>@{line.username}</strong> [{line.room}]: {line.text}
              </li>
            ))}
          </ul>
        </div>
      )}

      {(status?.recentLog?.length ?? 0) > 0 && (
        <div className={styles.log}>
          <Muted>Activity</Muted>
          <ul tabIndex={0} aria-label="Lichess activity">
            {status!.recentLog.slice(-8).map((line, i) => (
              <li key={`${i}-${line}`}>{line}</li>
            ))}
          </ul>
        </div>
      )}

      <Muted className={styles.foot}>
        Runtime settings come from server-side <code>LAPLACE_LICHESS_*</code> configuration.
        Stop allows a 20-second game drain, then cancels remaining games without inventing outcomes.
      </Muted>
    </Panel>
  );
}
