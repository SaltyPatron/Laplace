import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  Badge, Banner, Button, ErrorText, LoadingText, Modal, Muted, Panel, Toggle,
} from '@ui';
import { useAppStore } from '../store';
import { activity, cancelBackend, terminateBackend, type ActivityRow, type SignalResult } from './api';
import styles from './Admin.module.css';

const REFRESH_MS = 3000;

/** Thresholds a running statement is read against, not tuning knobs. */
const AGE_FILTERS = [
  { label: 'everything', seconds: 0 },
  { label: 'over 10s', seconds: 10 },
  { label: 'over 1m', seconds: 60 },
  { label: 'over 10m', seconds: 600 },
  { label: 'over 1h', seconds: 3600 },
];

function age(seconds: number | null): string {
  if (seconds == null) return '—';
  if (seconds < 1) return '<1s';
  if (seconds < 60) return `${Math.round(seconds)}s`;
  const m = Math.floor(seconds / 60);
  if (m < 60) return `${m}m ${Math.round(seconds % 60)}s`;
  const h = Math.floor(m / 60);
  return h < 24 ? `${h}h ${m % 60}m` : `${Math.floor(h / 24)}d ${h % 24}h`;
}

function stateClass(row: ActivityRow): string {
  if (row.state === 'active') return styles.running;
  if (row.state === 'idle in transaction') return styles.failed;
  return styles.cancelled;
}

/**
 * Live backends, and the two ways to stop one.
 *
 * The console could already START long work — an ingest, an eviction, a reindex —
 * and had no way to see it or stop it. Its only cut-off was ops.ingest_run_close,
 * which edits the JOURNAL ROW: the pipeline gate goes green while the ingest keeps
 * writing. Cancel signals the process; the journal follows.
 *
 * Cancel ends the running statement and leaves the session to roll back cleanly,
 * so it is the default and needs no confirmation — it is the recoverable move.
 * Terminate drops the connection and loses whatever transaction was open, so it
 * is confirmed and reserved for a backend that ignored a cancel.
 */
export function Activity() {
  const { tenant } = useAppStore();
  const [rows, setRows] = useState<ActivityRow[] | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [live, setLive] = useState(true);
  const [includeIdle, setIncludeIdle] = useState(true);
  const [minSeconds, setMinSeconds] = useState(0);
  const [busyPid, setBusyPid] = useState<number | null>(null);
  const [signalled, setSignalled] = useState<SignalResult | null>(null);
  const [confirmKill, setConfirmKill] = useState<ActivityRow | null>(null);

  const load = useCallback(async () => {
    try {
      const res = await activity(minSeconds, includeIdle, { tenant });
      setRows(res.rows ?? []);
      setErr(null);
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    }
  }, [tenant, minSeconds, includeIdle]);

  useEffect(() => {
    void load();
    if (!live) return;
    const t = setInterval(() => void load(), REFRESH_MS);
    return () => clearInterval(t);
  }, [load, live]);

  async function signal(row: ActivityRow, kind: 'cancel' | 'terminate') {
    setBusyPid(row.pid);
    setErr(null);
    try {
      const res = kind === 'cancel'
        ? await cancelBackend(row.pid, { tenant })
        : await terminateBackend(row.pid, { tenant });
      setSignalled(res.rows?.[0] ?? null);
      await load();
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    } finally {
      setBusyPid(null);
      setConfirmKill(null);
    }
  }

  // Longest-running first: "what is wedged" is the question this screen is opened
  // with, and the answer is always at the top of that order.
  const ordered = useMemo(
    () => (rows ? [...rows].sort((a, b) => (b.query_seconds ?? 0) - (a.query_seconds ?? 0)) : []),
    [rows],
  );
  const masked = ordered.filter((r) => r.restricted).length;

  return (
    <Panel
      title={`Activity${rows ? ` — ${rows.length} backend${rows.length === 1 ? '' : 's'}` : ''}`}
      actions={
        <div className={styles.toolbar}>
          <label className={styles.limitLabel}>
            age
            <select
              className={styles.limitSelect}
              value={minSeconds}
              onChange={(e) => setMinSeconds(Number(e.target.value))}
            >
              {AGE_FILTERS.map((f) => (
                <option key={f.seconds} value={f.seconds}>{f.label}</option>
              ))}
            </select>
          </label>
          <label className={styles.liveLabel}>
            <Toggle checked={includeIdle} onCheckedChange={setIncludeIdle} aria-label="Include idle backends" />
            idle
          </label>
          <label className={styles.liveLabel}>
            <Toggle checked={live} onCheckedChange={setLive} aria-label="Live refresh" />
            live ({REFRESH_MS / 1000}s)
          </label>
          <Button variant="ghost" onClick={() => void load()}>Refresh</Button>
        </div>
      }
    >
      {err && <ErrorText className={styles.runErrBox}>{err}</ErrorText>}

      {signalled && (
        <Banner variant={signalled.signalled ? 'info' : 'warning'}>
          {signalled.signalled
            ? `Signalled pid ${signalled.pid} — was ${signalled.was_state ?? 'unknown'} for ${age(signalled.was_seconds)}.`
            : `pid ${signalled.pid} was NOT signalled: the database role lacks pg_signal_backend.`}
          {signalled.was_query && <code className={styles.sig}>{signalled.was_query}</code>}
        </Banner>
      )}

      {masked > 0 && (
        <Muted>
          {masked} backend{masked === 1 ? '' : 's'} report no state or query: this role lacks
          pg_read_all_stats, so Postgres masked them. That is not the same as idle.
        </Muted>
      )}

      {rows == null ? <LoadingText>Reading ops.activity()…</LoadingText>
        : ordered.length === 0 ? <Muted>No backend matches this filter.</Muted>
        : (
          <div className={styles.tableWrap}>
            <table className={styles.table}>
              <thead>
                <tr>
                  <th scope="col">pid</th>
                  <th scope="col">state</th>
                  <th scope="col">running</th>
                  <th scope="col">wait</th>
                  <th scope="col">client</th>
                  <th scope="col">query</th>
                  <th scope="col">stop</th>
                </tr>
              </thead>
              <tbody>
                {ordered.map((r) => (
                  <tr key={r.pid}>
                    <td className={styles.num}>
                      {r.pid}
                      {r.is_self && <Badge className={styles.badge}>this console</Badge>}
                    </td>
                    <td className={stateClass(r)}>{r.restricted ? 'masked' : r.state ?? '—'}</td>
                    <td className={styles.num}>{age(r.query_seconds)}</td>
                    <td>{r.wait_event_type ? `${r.wait_event_type}: ${r.wait_event}` : '—'}</td>
                    <td title={r.client_addr ?? undefined}>
                      {r.application_name || r.backend_type || '—'}
                    </td>
                    <td title={r.query ?? undefined}>
                      <code className={styles.queryCell}>
                        {r.query ? (r.query.length > 90 ? `${r.query.slice(0, 89)}…` : r.query) : '—'}
                      </code>
                    </td>
                    <td>
                      {r.is_self ? <Muted>—</Muted> : (
                        <div className={styles.rowActions}>
                          <Button
                            variant="ghost"
                            size="sm"
                            loading={busyPid === r.pid}
                            onClick={() => void signal(r, 'cancel')}
                          >
                            Cancel
                          </Button>
                          <Button
                            variant="ghost"
                            size="sm"
                            disabled={busyPid === r.pid}
                            onClick={() => setConfirmKill(r)}
                          >
                            Terminate
                          </Button>
                        </div>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

      <Modal
        open={confirmKill != null}
        onClose={() => setConfirmKill(null)}
        title={`Terminate pid ${confirmKill?.pid ?? ''}?`}
        actions={
          <>
            <Button variant="ghost" onClick={() => setConfirmKill(null)}>Keep it</Button>
            <Button
              loading={busyPid === confirmKill?.pid}
              onClick={() => confirmKill && void signal(confirmKill, 'terminate')}
            >
              Terminate
            </Button>
          </>
        }
      >
        <p>
          This drops the connection. Any open transaction is rolled back and lost — for an ingest
          that means the batch in flight, not the batches already committed.
        </p>
        <p>
          <strong>Try Cancel first.</strong> It ends the statement and lets the session unwind
          cleanly; Terminate is for a backend that ignored one.
        </p>
        {confirmKill?.query && <code className={styles.sig}>{confirmKill.query}</code>}
      </Modal>
    </Panel>
  );
}
