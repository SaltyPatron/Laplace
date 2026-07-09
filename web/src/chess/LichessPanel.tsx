import { useCallback, useEffect, useState } from 'react';
import { apiGet, apiPost } from '../api/client';
import { Alert, Chip, Field, Input, Muted, Panel, Toggle } from '@ui';
import styles from './LichessPanel.module.css';

export interface LichessStatus {
  configured: boolean;
  tokenPreview?: string | null;
  connected: boolean;
  username?: string | null;
  depth: number;
  maxConcurrent: number;
  substrate: boolean;
  gamesRecorded: number;
  recentLog: string[];
  error?: string | null;
}

interface ChatLine {
  gameId: string;
  room: string;
  username: string;
  text: string;
}

export function LichessPanel() {
  const [status, setStatus] = useState<LichessStatus | null>(null);
  const [depth, setDepth] = useState(8);
  const [maxConcurrent, setMaxConcurrent] = useState(2);
  const [substrate, setSubstrate] = useState(true);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [activeGameId, setActiveGameId] = useState<string | null>(null);
  const [chat, setChat] = useState<ChatLine[]>([]);

  const refresh = useCallback(async () => {
    try {
      const s = await apiGet<LichessStatus>('/chess/lichess/status');
      setStatus(s);
      if (s.depth) setDepth(s.depth);
      if (s.maxConcurrent) setMaxConcurrent(s.maxConcurrent);
      setSubstrate(s.substrate);
      setErr(null);
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    }
  }, []);

  useEffect(() => {
    void refresh();
    const t = setInterval(() => void refresh(), status?.connected ? 2000 : 8000);
    return () => clearInterval(t);
  }, [refresh, status?.connected]);

  useEffect(() => {
    if (!activeGameId || !status?.connected) return;
    const poll = setInterval(async () => {
      try {
        const lines = await apiGet<ChatLine[]>(`/chess/lichess/games/${activeGameId}/chat`);
        setChat(lines);
      } catch { /* ignore */ }
    }, 2500);
    return () => clearInterval(poll);
  }, [activeGameId, status?.connected]);

  const setConnected = async (on: boolean) => {
    if (busy) return;
    setBusy(true);
    setErr(null);
    try {
      if (on) {
        await apiPost<LichessStatus>('/chess/lichess/start', {
          depth,
          maxConcurrent,
          substrate,
        });
      } else {
        await apiPost('/chess/lichess/stop', {});
        setActiveGameId(null);
        setChat([]);
      }
      await refresh();
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const configured = status?.configured ?? false;
  const connected = status?.connected ?? false;
  const username = status?.username;

  return (
    <Panel className={styles.panel} title="Lichess connectivity">
      <p className={styles.intro}>
        Listen for challenges on lichess.org. Each ply folds to consensus before the next search;
        finished games get a terminal outcome pass. Token from <code>deploy/secrets/lichess.env</code>.
      </p>

      <div className={styles.statusRow}>
        <Chip variant={configured ? 'engineOk' : 'engineMissing'}>
          Token {configured ? `✓ ${status?.tokenPreview ?? ''}` : '✗ not configured'}
        </Chip>
        <Chip variant={connected ? 'engineOk' : 'default'}>
          {connected ? '● Listening' : '○ Offline'}
        </Chip>
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

      {!configured && (
        <Alert>
          No token visible to the API process. Check <code>deploy/secrets/lichess.env</code>{' '}
          (<code>LICHESS_TOKEN</code> or <code>LICHESS_API</code>) and republish so IIS picks it up in{' '}
          <code>web.config</code>.
        </Alert>
      )}

      {status?.error && !connected && <Alert>{status.error}</Alert>}
      {err && <Alert>{err}</Alert>}

      <div className={styles.controls}>
        <Field label="Listen on Lichess" help="Accepts standard challenges while on. Challenge the bot account from lichess.org." layout="row">
          <Toggle
            checked={connected}
            disabled={!configured || busy}
            onCheckedChange={(on) => void setConnected(on)}
            aria-label="Listen on Lichess"
          />
        </Field>

        <Field label="Search depth" help="Max iterative depth; clock budget on Lichess can stop search earlier. Default 8.">
          <Input
            type="number"
            min={1}
            max={20}
            value={depth}
            disabled={connected || busy}
            aria-label="Search depth"
            onChange={(e) => setDepth(Number(e.target.value))}
          />
        </Field>

        <Field label="Max concurrent" help="Declines new challenges when this many games are active.">
          <Input
            type="number"
            min={1}
            max={8}
            value={maxConcurrent}
            disabled={connected || busy}
            aria-label="Max concurrent"
            onChange={(e) => setMaxConcurrent(Number(e.target.value))}
          />
        </Field>

        <Field label="Substrate bias" help="Fold consensus at root + learned PST refreshed after each ply fold." layout="row">
          <Toggle
            checked={substrate}
            disabled={connected || busy}
            onCheckedChange={setSubstrate}
            aria-label="Substrate bias"
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
          <ul>
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
          <ul>
            {status!.recentLog.slice(-8).map((line, i) => (
              <li key={`${i}-${line}`}>{line}</li>
            ))}
          </ul>
        </div>
      )}

      <Muted className={styles.foot}>
        Token: process env, then <code>deploy/secrets/lichess.env</code>. Stop disconnects after in-flight games finish.
      </Muted>
    </Panel>
  );
}
