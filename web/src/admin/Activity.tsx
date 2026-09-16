import { useMemo, useRef, useState } from 'react';
import { Badge, Banner, Button, ErrorText, Modal, Muted, Panel, ReadStatus, Toggle, useReadResource } from '@ui';
import { useAppStore } from '../store';
import { activity, cancelBackend, terminateBackend, type ActivityRow, type SignalResult } from './api';
import styles from './Admin.module.css';

const REFRESH_MS = 3000;
const AGE_FILTERS = [
  { label: 'everything', seconds: 0 }, { label: 'over 10s', seconds: 10 },
  { label: 'over 1m', seconds: 60 }, { label: 'over 10m', seconds: 600 }, { label: 'over 1h', seconds: 3600 },
];
function age(seconds: number | null): string {
  if (seconds == null) return '—';
  if (seconds < 1) return '<1s';
  if (seconds < 60) return `${Math.round(seconds)}s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ${Math.round(seconds % 60)}s`;
  const hours = Math.floor(minutes / 60);
  return hours < 24 ? `${hours}h ${minutes % 60}m` : `${Math.floor(hours / 24)}d ${hours % 24}h`;
}
function stateClass(row: ActivityRow): string {
  if (row.state === 'active') return styles.running;
  if (row.state === 'idle in transaction') return styles.failed;
  return styles.cancelled;
}

export function Activity() {
  const { tenant } = useAppStore();
  const [live, setLive] = useState(true);
  const [includeIdle, setIncludeIdle] = useState(true);
  const [minSeconds, setMinSeconds] = useState(0);
  const signalling = useRef(false);
  const [busyPid, setBusyPid] = useState<number | null>(null);
  const [signalled, setSignalled] = useState<SignalResult | null>(null);
  const [confirmKill, setConfirmKill] = useState<ActivityRow | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const read = useReadResource({
    key: JSON.stringify(['activity', tenant, minSeconds, includeIdle]),
    read: (signal) => activity(minSeconds, includeIdle, { tenant, signal }),
    refreshMs: live ? REFRESH_MS : 0,
  });
  const rows = read.data?.rows;
  const ordered = useMemo(() => rows ? [...rows].sort((a, b) => (b.query_seconds ?? 0) - (a.query_seconds ?? 0)) : [], [rows]);
  const masked = ordered.filter((row) => row.restricted).length;

  async function signal(row: ActivityRow, kind: 'cancel' | 'terminate') {
    if (signalling.current) return;
    signalling.current = true;
    setBusyPid(row.pid);
    setActionError(null);
    try {
      const result = kind === 'cancel' ? await cancelBackend(row.pid, { tenant }) : await terminateBackend(row.pid, { tenant });
      setSignalled(result.rows?.[0] ?? null);
      await read.reload();
    } catch (error) {
      setActionError(error instanceof Error ? error.message : String(error));
    } finally {
      signalling.current = false;
      setBusyPid(null);
      setConfirmKill(null);
    }
  }

  return <Panel title={`Activity${rows ? ` — ${rows.length} backend${rows.length === 1 ? '' : 's'}` : ''}`} expandable label="Activity"
    actions={<div className={styles.toolbar}>
      <label className={styles.limitLabel}>age
        <select className={styles.limitSelect} value={minSeconds} onChange={(event) => setMinSeconds(Number(event.target.value))}>
          {AGE_FILTERS.map((filter) => <option key={filter.seconds} value={filter.seconds}>{filter.label}</option>)}
        </select>
      </label>
      <label className={styles.liveLabel}><Toggle checked={includeIdle} onCheckedChange={setIncludeIdle} aria-label="Include idle backends" />idle</label>
      <label className={styles.liveLabel}><Toggle checked={live} onCheckedChange={setLive} aria-label="Live refresh" />live ({REFRESH_MS / 1000}s)</label>
      <Button variant="ghost" onClick={() => void read.refresh()} disabled={read.status === 'loading'}>Refresh</Button>
    </div>}>
    <ReadStatus label="Activity" resource={read} />
    {actionError && <ErrorText role="alert" className={styles.runErrBox}>{actionError}</ErrorText>}
    {signalled && <Banner variant={signalled.signalled ? 'info' : 'warning'}>
      {signalled.signalled
        ? `Signalled pid ${signalled.pid} — was ${signalled.was_state ?? 'unknown'} for ${age(signalled.was_seconds)}.`
        : `pid ${signalled.pid} was not signalled. Check its current state and the database role's permissions.`}
      {signalled.was_query && <code className={styles.sig}>{signalled.was_query}</code>}
    </Banner>}
    {masked > 0 && <Muted>{masked} backend{masked === 1 ? '' : 's'} report no state or query: this role lacks pg_read_all_stats. That is not the same as idle.</Muted>}
    {rows && (ordered.length === 0 ? <Muted>No backend matches this filter.</Muted> : <div className={styles.tableWrap}>
      <table className={styles.table}>
        <thead><tr><th scope="col">pid</th><th scope="col">state</th><th scope="col">running</th><th scope="col">wait</th><th scope="col">client</th><th scope="col">query</th><th scope="col">stop</th></tr></thead>
        <tbody>{ordered.map((row) => <tr key={row.pid}>
          <td className={styles.num}>{row.pid}{row.is_self && <Badge className={styles.badge}>this console</Badge>}</td>
          <td className={stateClass(row)}>{row.restricted ? 'masked' : row.state ?? '—'}</td>
          <td className={styles.num}>{age(row.query_seconds)}</td>
          <td>{row.wait_event_type ? `${row.wait_event_type}: ${row.wait_event}` : '—'}</td>
          <td title={row.client_addr ?? undefined}>{row.application_name || row.backend_type || '—'}</td>
          <td>{row.query ? <details>
            <summary><code className={styles.queryCell}>{row.query.length > 90 ? `${row.query.slice(0, 89)}…` : row.query}</code></summary>
            <pre className={styles.sig}>{row.query}</pre>
          </details> : '—'}</td>
          <td>{row.is_self ? <Muted>—</Muted> : <div className={styles.rowActions}>
            <Button variant="ghost" size="sm" disabled={busyPid != null} loading={busyPid === row.pid} onClick={() => void signal(row, 'cancel')}>Cancel</Button>
            <Button variant="ghost" size="sm" disabled={busyPid != null} onClick={() => setConfirmKill(row)}>Terminate</Button>
          </div>}</td>
        </tr>)}</tbody>
      </table>
    </div>)}
    <Modal open={confirmKill != null} onClose={() => setConfirmKill(null)} title={`Terminate pid ${confirmKill?.pid ?? ''}?`}
      actions={<><Button variant="ghost" onClick={() => setConfirmKill(null)}>Keep it</Button>
        <Button loading={busyPid === confirmKill?.pid} onClick={() => confirmKill && void signal(confirmKill, 'terminate')}>Terminate</Button></>}>
      <p>This drops the connection. Any open transaction is rolled back and lost — for an ingest that means the batch in flight, not the batches already committed.</p>
      <p><strong>Try Cancel first.</strong> It ends the statement and lets the session unwind cleanly; Terminate is for a backend that ignored one.</p>
      {confirmKill?.query && <code className={styles.sig}>{confirmKill.query}</code>}
    </Modal>
  </Panel>;
}
