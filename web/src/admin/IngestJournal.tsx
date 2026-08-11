import { useCallback, useEffect, useState } from 'react';
import { Button, ErrorText, LoadingText, Muted, Panel, Toggle } from '@ui';
import { useAppStore } from '../store';
import { closeIngestRun, ingestRuns, type IngestRun } from './api';
import styles from './Admin.module.css';

const REFRESH_MS = 5000;

/** Runs that a pipeline waiting on this journal would still be blocked by. */
const OPEN_STATES = new Set(['running', 'started', 'pending', 'in_progress']);

function duration(run: IngestRun): string {
  if (!run.started_at) return '—';
  const start = Date.parse(run.started_at);
  const end = run.ended_at ? Date.parse(run.ended_at) : Date.now();
  if (!Number.isFinite(start) || !Number.isFinite(end)) return '—';
  const s = Math.max(0, Math.round((end - start) / 1000));
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  return m < 60 ? `${m}m ${s % 60}s` : `${Math.floor(m / 60)}h ${m % 60}m`;
}

function pct(done: number | null, total: number | null): string {
  if (!total || total <= 0 || done == null) return '—';
  return `${Math.min(100, Math.round((done / total) * 100))}%`;
}

function statusClass(status: string): string {
  const s = status.toLowerCase();
  if (s === 'ok' || s === 'complete' || s === 'completed') return styles.ok;
  if (OPEN_STATES.has(s)) return styles.running;
  if (s === 'cancelled' || s === 'canceled') return styles.cancelled;
  return styles.failed;
}

/**
 * The ingest journal — the gate CI/CD waits on.
 *
 * This is a read of `ops.ingest_runs`, which is the same row a pipeline polls.
 * Forcing a run closed is `ops.ingest_run_close`, which exists in the catalog
 * but cannot be called from here: POST /v1/op binds a read-only data source
 * (SubstrateClient.InvokeOpAsync), so every write op in the catalog is
 * unreachable over HTTP. The action is shown disabled with the equivalent
 * command rather than hidden, so the gap is visible instead of implied.
 */
export function IngestJournal() {
  const { tenant } = useAppStore();
  const [runs, setRuns] = useState<IngestRun[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [live, setLive] = useState(true);
  const [limit, setLimit] = useState(25);
  const [copied, setCopied] = useState<string | null>(null);
  const [closing, setClosing] = useState<string | null>(null);
  const [confirming, setConfirming] = useState<string | null>(null);
  const [closeErr, setCloseErr] = useState<string | null>(null);

  const load = useCallback(() => {
    ingestRuns(limit, { tenant })
      .then((r) => { setRuns(r.rows ?? []); setError(null); })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, [limit, tenant]);

  useEffect(() => { load(); }, [load]);

  useEffect(() => {
    if (!live) return;
    const t = setInterval(load, REFRESH_MS);
    return () => clearInterval(t);
  }, [live, load]);

  const open = runs?.filter((r) => OPEN_STATES.has(r.status.toLowerCase())) ?? [];

  /**
   * Force the run closed. Two clicks, because this releases a CI/CD gate and
   * there is no undo — the second click is the confirmation.
   */
  async function forceClose(run: IngestRun) {
    if (confirming !== run.run_id) {
      setConfirming(run.run_id);
      setCloseErr(null);
      setTimeout(() => setConfirming((c) => (c === run.run_id ? null : c)), 5000);
      return;
    }
    setConfirming(null);
    setClosing(run.run_id);
    setCloseErr(null);
    try {
      await closeIngestRun(run.run_id, 'cancelled', { tenant });
      load();
    } catch (e) {
      setCloseErr(`${run.source_name}: ${e instanceof Error ? e.message : String(e)}`);
    } finally {
      setClosing(null);
    }
  }

  async function copyClose(run: IngestRun) {
    const cmd = `SELECT * FROM ops.ingest_run_close('${run.run_id}'::uuid, 'cancelled');`;
    try {
      await navigator.clipboard.writeText(cmd);
      setCopied(run.run_id);
      setTimeout(() => setCopied(null), 2000);
    } catch { /* clipboard unavailable; the command is on screen anyway */ }
  }

  return (
    <Panel title="Ingest journal — the CI/CD gate">
      <div className={styles.toolbar}>
        <label className={styles.liveLabel}>
          <Toggle checked={live} onCheckedChange={setLive} aria-label="Live refresh" />
          live ({REFRESH_MS / 1000}s)
        </label>
        <label className={styles.limitLabel}>
          rows
          <select
            className={styles.limitSelect}
            value={limit}
            onChange={(e) => setLimit(Number(e.target.value))}
          >
            {[10, 25, 50, 100].map((n) => <option key={n} value={n}>{n}</option>)}
          </select>
        </label>
        <Button variant="ghost" onClick={load}>Refresh</Button>
        <Muted className={styles.gateNote}>
          {runs == null ? '' : open.length === 0
            ? 'No open runs — a pipeline gating on this journal would proceed.'
            : `${open.length} open run${open.length === 1 ? '' : 's'} — a pipeline gating on this journal is still blocked.`}
        </Muted>
      </div>

      {error ? <ErrorText>{error}</ErrorText>
        : runs == null ? <LoadingText>Reading the journal…</LoadingText>
        : runs.length === 0 ? <Muted>No ingest runs recorded.</Muted>
        : (
          <div className={styles.tableWrap}>
            <table className={styles.table}>
              <thead>
                <tr>
                  <th scope="col">Status</th>
                  <th scope="col">Source</th>
                  <th scope="col" className={styles.num}>Layer</th>
                  <th scope="col" className={styles.num}>Units</th>
                  <th scope="col" className={styles.num}>Input</th>
                  <th scope="col" className={styles.num}>Files</th>
                  <th scope="col" className={styles.num}>Entities</th>
                  <th scope="col" className={styles.num}>Took</th>
                  <th scope="col">Force close</th>
                </tr>
              </thead>
              <tbody>
                {runs.map((r) => (
                  <tr key={r.run_id} className={OPEN_STATES.has(r.status.toLowerCase()) ? styles.openRow : undefined}>
                    <td>
                      <span className={`${styles.badge} ${statusClass(r.status)}`}>{r.status}</span>
                      {r.error && <div className={styles.runErr} title={r.error}>{r.error.slice(0, 90)}</div>}
                    </td>
                    <td>
                      <div className={styles.source}>{r.source_name}</div>
                      <code className={styles.runId}>{r.run_id.slice(0, 8)}</code>
                    </td>
                    <td className={styles.num}>{r.layer ?? '—'}</td>
                    <td className={styles.num}>
                      {r.units_applied ?? 0}/{r.units_attempted ?? 0}
                      {(r.units_failed ?? 0) > 0 && <span className={styles.failedUnits}> ({r.units_failed} failed)</span>}
                    </td>
                    <td className={styles.num}>{pct(r.input_units_done, r.input_units_total)}</td>
                    <td className={styles.num}>{pct(r.files_done, r.files_total)}</td>
                    <td className={styles.num}>{(r.entities ?? 0).toLocaleString()}</td>
                    <td className={styles.num}>{duration(r)}</td>
                    <td>
                      <Button
                        variant="ghost"
                        loading={closing === r.run_id}
                        disabled={closing != null}
                        onClick={() => void forceClose(r)}
                        title={`Force ops.ingest_run_close on ${r.run_id}`}
                      >
                        {confirming === r.run_id ? 'Confirm?' : 'Close'}
                      </Button>
                      <button type="button" className={styles.copyCmd} onClick={() => void copyClose(r)}>
                        {copied === r.run_id ? 'copied' : 'copy SQL'}
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

      {closeErr && <ErrorText className={styles.foot}>{closeErr}</ErrorText>}

      <Muted className={styles.foot}>
        Read via <code>ops.ingest_runs</code>; force close calls{' '}
        <code>ops.ingest_run_close(p_run_id uuid, p_status text)</code>, which is on the endpoint&rsquo;s
        write allow-list (<code>InstalledOpInvoker.WritableOps</code>) and so resolves onto a
        writable connection. Requires an endpoint built after that change — against an older
        build the call fails read-only, and <em>copy SQL</em> gives the equivalent statement.
      </Muted>
    </Panel>
  );
}
