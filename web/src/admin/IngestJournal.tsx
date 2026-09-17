import { useEffect, useRef, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { Button, ErrorText, Modal, Muted, Panel, ReadStatus, Toggle, useReadResource } from '@ui';
import { ResultWorkspace, type ResultColumn } from '../ui/composites/ResultWorkspace/ResultWorkspace';
import { captureRows } from '../ui/lib/resultRows';
import { useAppStore } from '../store';
import { closeIngestRun, ingestFiles, ingestRuns, type IngestFile, type IngestRun } from './api';
import { countText, ingestDuration, ingestStatusTone, OPEN_INGEST_STATES, progressText } from './ingestPresentation';
import styles from './Admin.module.css';

const REFRESH_MS = 5000;
function Status({ value }: { value: string | null }) {
  return value ? <span className={`${styles.badge} ${styles[ingestStatusTone(value)]}`}>{value}</span> : <span>Not recorded</span>;
}
function RunProgress({ run }: { run: IngestRun }) {
  const done = run.input_units_done ?? 0;
  const total = run.input_units_total ?? 0;
  const determinate = total > 0 && done <= total;
  const pct = determinate ? Math.max(0, Math.min(100, (done / total) * 100)) : null;
  return <div className={styles.runProgress}>
    <div className={styles.progressHeading}>
      <strong>{progressText(run.input_units_done, run.input_units_total)}</strong>
      {pct != null && <span>{pct.toFixed(pct >= 10 ? 0 : 1)}%</span>}
    </div>
    {determinate && <progress className={styles.progressBar} max={total} value={done} aria-label="Input progress" />}
    <span className={styles.progressPct}>Files {progressText(run.files_done, run.files_total)}</span>
  </div>;
}
export function IngestJournal({ refreshSignal = 0 }: { refreshSignal?: number }) {
  const { tenant, authUser } = useAppStore();
  return <RunWorkspace key={JSON.stringify([tenant, authUser?.id])} tenant={tenant} refreshSignal={refreshSignal} />;
}
function RunWorkspace({ tenant, refreshSignal }: { tenant: string; refreshSignal: number }) {
  const [params, setParams] = useSearchParams();
  const [live, setLive] = useState(true);
  const [limit, setLimit] = useState(25);
  const [confirming, setConfirming] = useState<IngestRun | null>(null);
  const [closing, setClosing] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  const closingRef = useRef(false);
  const expandedRun = params.get('run');
  const filter = params.get('source') ?? '';
  const runsRead = useReadResource({
    key: JSON.stringify(['ingest-runs', tenant, limit]), refreshMs: live ? REFRESH_MS : 0,
    read: async (signal) => {
      const result = await ingestRuns(limit, { tenant, signal });
      return captureRows(result.rows, `Up to ${limit} requested run receipts${result.truncated_at != null ? `; transport truncated at ${result.truncated_at}` : ''}. Older runs may exist.`, { operation: 'ops.ingest_runs', requested_limit: limit });
    },
  });
  useEffect(() => {
    if (refreshSignal > 0) void runsRead.refresh();
  }, [refreshSignal]);
  function setParam(name: string, value: string | null, replace = false) {
    const next = new URLSearchParams(params);
    if (value) next.set(name, value); else next.delete(name);
    setParams(next, { replace });
  }
  async function closeReceipt(run: IngestRun) {
    if (closingRef.current) return;
    closingRef.current = true; setClosing(true); setActionError(null);
    try {
      await closeIngestRun(run.run_id, 'cancelled', { tenant });
      setConfirming(null); await runsRead.refresh();
    } catch (failure) {
      setActionError(`${run.source_name}: ${failure instanceof Error ? failure.message : String(failure)}. The outcome may be unknown after a transport failure; refresh the receipt before trying again.`);
    } finally { closingRef.current = false; setClosing(false); }
  }
  const columns: ResultColumn<IngestRun>[] = [
    { key: 'source_name', label: 'Run', render: (run) => <div className={styles.runIdentity}>
      <Link className={styles.source} to={`/explore/source/${encodeURIComponent(run.source_name)}`}>{run.source_name}</Link>
      <span className={styles.progressPct}>Layer {run.layer ?? '—'}</span>
      <code className={styles.runId} title={run.run_id}>{run.run_id}</code>
    </div> },
    { key: 'status', label: 'State', render: (run) => <div className={styles.runState}>
      <Status value={run.status} />
      {run.phase && <strong>{run.phase}</strong>}
      {run.error && <details className={styles.runErr}><summary>Run error</summary><pre className={styles.sig}>{run.error}</pre></details>}
    </div> },
    { key: 'input_units_done', label: 'Progress', render: (run) => <RunProgress run={run} /> },
    { key: 'entities', label: 'Staged output', render: (run) => <div className={styles.outputCounts}>
      <span><strong>{countText(run.entities)}</strong><small>entities</small></span>
      <span><strong>{countText(run.physicalities)}</strong><small>physicalities</small></span>
      <span><strong>{countText(run.attestations)}</strong><small>attestations</small></span>
      {run.entities === 0 && run.physicalities === 0 && run.attestations === 0 && <em>No staged writes reported</em>}
    </div> },
    { key: 'throughput_status', label: 'Performance', render: (run) => <div className={styles.runPerformance}>
      <div><Status value={run.throughput_status} /></div>
      <strong>{run.throughput_rows_per_s == null ? 'Rate not recorded' : `${countText(run.throughput_rows_per_s)} rows/s`}</strong>
      <span className={styles.progressPct}>{run.throughput_compared ? 'Compared with baseline' : 'No baseline comparison'}</span>
      <span>{ingestDuration(run.started_at, run.ended_at)}</span>
      <time className={styles.progressPct} dateTime={run.started_at ?? undefined}>{run.started_at ? new Date(run.started_at).toLocaleString() : 'Start not recorded'}</time>
    </div> },
    { key: 'ended_at', label: 'Actions', render: (run) => <div className={styles.runActions}>
      <Button variant="ghost" aria-expanded={expandedRun === run.run_id} onClick={() => setParam('run', expandedRun === run.run_id ? null : run.run_id)}>{expandedRun === run.run_id ? 'Hide files' : 'File receipts'}</Button>
      {OPEN_INGEST_STATES.has(run.status.toLowerCase())
        ? <Button variant="ghost" disabled={closing} onClick={() => { setConfirming(run); setActionError(null); }}>Close receipt…</Button>
        : <span className={styles.progressPct}>{run.ended_at ? `Ended ${new Date(run.ended_at).toLocaleString()}` : 'Closed'}</span>}
    </div> },
  ];
  const open = runsRead.data?.rows.filter((run) => OPEN_INGEST_STATES.has(run.status.toLowerCase())).length;
  return <>
    <Panel title="Ingestion runs" expandable>
      <div className={styles.toolbar}>
        <label className={styles.liveLabel}><Toggle checked={live} onCheckedChange={setLive} aria-label="Live refresh" />Live refresh · {REFRESH_MS / 1000}s</label>
        <label className={styles.limitLabel}>Runs<select className={styles.limitSelect} value={limit} onChange={(event) => setLimit(Number(event.target.value))}>{[10, 25, 50, 100, 500].map((value) => <option key={value} value={value}>{value}</option>)}</select></label>
        <Button variant="ghost" onClick={() => void runsRead.refresh()}>Refresh now</Button>
      </div>
      <ReadStatus label="Ingestion runs" resource={runsRead} />
      {open != null && <Muted>{open} open in this {runsRead.data?.rows.length ?? 0}-run response. Older runs may exist.</Muted>}
      {runsRead.data && <ResultWorkspace scopeKey={JSON.stringify(['ingest-runs', tenant])} label="Ingestion run receipts" snapshot={runsRead.data} columns={columns}
        rowLabel={(run) => `${run.source_name} run ${run.run_id}`} filterText={filter} onFilterTextChange={(value) => setParam('source', value, true)} />}
      <Muted>Run receipts report execution state. File receipts show per-file dispositions, including already-complete files.</Muted>
    </Panel>
    {expandedRun && <RunFiles key={JSON.stringify([tenant, expandedRun])} runId={expandedRun} tenant={tenant} live={live} onClose={() => setParam('run', null)} />}
    <Modal open={confirming != null} onClose={() => { if (!closingRef.current) setConfirming(null); }} title="Close this run receipt?"
      actions={<><Button variant="ghost" disabled={closing} onClick={() => setConfirming(null)}>Go back</Button><Button loading={closing} onClick={() => confirming && void closeReceipt(confirming)}>Mark receipt cancelled</Button></>}>
      <p>Source: {confirming?.source_name}. Run: <code>{confirming?.run_id}</code>.</p>
      <p>This closes the shared journal receipt only. It does not stop a CLI process or database backend. Use Activity to inspect and stop active backend work before closing its receipt.</p>
      <Link to="/operator?section=activity">Open Activity</Link>
      <details><summary>Equivalent journal-only SQL</summary><pre className={styles.sig}>{confirming ? `SELECT * FROM ops.ingest_run_close('${confirming.run_id}'::uuid, 'cancelled');` : ''}</pre></details>
      {actionError && <ErrorText role="alert">{actionError}</ErrorText>}
    </Modal>
  </>;
}
function RunFiles({ runId, tenant, live, onClose }: { runId: string; tenant: string; live: boolean; onClose: () => void }) {
  const [limit, setLimit] = useState(250);
  const valid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(runId);
  const filesRead = useReadResource({
    key: JSON.stringify(['ingest-files', tenant, runId, limit]), enabled: valid, refreshMs: live ? REFRESH_MS : 0,
    read: async (signal) => {
      const result = await ingestFiles(runId, limit, { tenant, signal });
      return captureRows(result.rows, `Requested up to ${limit} file receipts for run ${runId}; active and failed files are returned first${result.truncated_at != null ? `; transport truncated at ${result.truncated_at}` : ''}.`, { operation: 'ops.ingest_files', run_id: runId, requested_limit: limit });
    },
  });
  const columns: ResultColumn<IngestFile>[] = [
    { key: 'status', label: 'Disposition', render: (file) => <Status value={file.status} /> },
    { key: 'file_label', label: 'File' }, { key: 'records', label: 'Records' },
    { key: 'entities', label: 'Staged entities' }, { key: 'physicalities', label: 'Staged physicalities' },
    { key: 'attestations', label: 'Staged attestations' }, { key: 'bytes', label: 'Bytes' },
    { key: 'error', label: 'Error' },
  ];
  return <Panel title="File receipts" expandable actions={<Button variant="ghost" onClick={onClose}>Close file receipts</Button>}>
    <code>{runId}</code>
    <div className={styles.toolbar}><label className={styles.limitLabel}>Files<select className={styles.limitSelect} value={limit} onChange={(event) => setLimit(Number(event.target.value))}>{[250, 500, 1000, 5000].map((value) => <option key={value} value={value}>{value}</option>)}</select></label>
      <Button variant="ghost" disabled={!valid} onClick={() => void filesRead.refresh()}>Refresh files</Button></div>
    {!valid ? <ErrorText>The run address is not a UUID. Choose a run from the journal.</ErrorText> : <ReadStatus label="File receipts" resource={filesRead} />}
    {filesRead.data && <ResultWorkspace scopeKey={JSON.stringify(['ingest-files', tenant, runId])} label="Ingested file receipts" snapshot={filesRead.data} columns={columns} rowLabel={(file) => file.file_label} />}
  </Panel>;
}
