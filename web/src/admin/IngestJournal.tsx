import { useRef, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { Button, ErrorText, Modal, Muted, Panel, ReadStatus, Toggle, useReadResource } from '@ui';
import { ResultWorkspace, type ResultColumn } from '../ui/composites/ResultWorkspace/ResultWorkspace';
import { captureRows } from '../ui/lib/resultRows';
import { useAppStore } from '../store';
import { closeIngestRun, ingestFiles, ingestRuns, type IngestFile, type IngestRun } from './api';
import { countText, ingestDuration, ingestStatusTone, OPEN_INGEST_STATES } from './ingestPresentation';
import styles from './Admin.module.css';

const REFRESH_MS = 5000;
function Status({ value }: { value: string | null }) {
  return value ? <span className={`${styles.badge} ${styles[ingestStatusTone(value)]}`}>{value}</span> : <span>Not recorded</span>;
}
export function IngestJournal() {
  const { tenant, authUser } = useAppStore();
  return <RunWorkspace key={JSON.stringify([tenant, authUser?.id])} tenant={tenant} />;
}
function RunWorkspace({ tenant }: { tenant: string }) {
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
    { key: 'status', label: 'Run status', render: (run) => <><Status value={run.status} />{run.phase && <div>{run.phase}</div>}{run.error && <details><summary>Run error</summary><pre className={styles.sig}>{run.error}</pre></details>}</> },
    { key: 'source_name', label: 'Source', render: (run) => <Link to={`/explore/source/${encodeURIComponent(run.source_name)}`}>{run.source_name}</Link> },
    { key: 'run_id', label: 'Files and run', render: (run) => <><code>{run.run_id}</code><Button variant="ghost" aria-expanded={expandedRun === run.run_id} onClick={() => setParam('run', expandedRun === run.run_id ? null : run.run_id)}>{expandedRun === run.run_id ? 'Hide file receipts' : 'Open file receipts'}</Button></> },
    { key: 'layer', label: 'Layer' },
    { key: 'input_units_done', label: 'Input processed / total', render: (run) => <><span>{countText(run.input_units_done)} / {countText(run.input_units_total)}</span><div className={styles.progressPct}>Processed input can exclude already-complete files.</div></> },
    { key: 'files_done', label: 'Files complete / total', render: (run) => <span>{countText(run.files_done)} / {countText(run.files_total)}</span> },
    { key: 'entities', label: 'Staged entities / physicalities / attestations', render: (run) => <><span>{countText(run.entities)} / {countText(run.physicalities)} / {countText(run.attestations)}</span>{run.entities === 0 && run.physicalities === 0 && run.attestations === 0 && <div className={styles.progressPct}>No staged writes reported; inspect file dispositions.</div>}</> },
    { key: 'throughput_status', label: 'Throughput measurement', render: (run) => <><Status value={run.throughput_status} /><div className={styles.progressPct}>{run.throughput_rows_per_s == null ? 'No rate recorded' : `${countText(run.throughput_rows_per_s)} rows/s`}{run.throughput_compared ? ' · compared with baseline' : ' · no baseline comparison'}</div></> },
    { key: 'started_at', label: 'Started / elapsed', render: (run) => <><time dateTime={run.started_at ?? undefined}>{run.started_at ? new Date(run.started_at).toLocaleString() : 'Not recorded'}</time><div>{ingestDuration(run.started_at, run.ended_at)}</div></> },
    { key: 'ended_at', label: 'Receipt control', render: (run) => <Button variant="ghost" disabled={closing || !OPEN_INGEST_STATES.has(run.status.toLowerCase())} onClick={() => { setConfirming(run); setActionError(null); }}>Close run receipt…</Button> },
  ];
  const open = runsRead.data?.rows.filter((run) => OPEN_INGEST_STATES.has(run.status.toLowerCase())).length;
  return <>
    <Panel title="Ingestion runs" expandable>
      <div className={styles.toolbar}>
        <label className={styles.liveLabel}><Toggle checked={live} onCheckedChange={setLive} aria-label="Live refresh" />Refresh after each completed read ({REFRESH_MS / 1000}s)</label>
        <label className={styles.limitLabel}>Requested run window<select className={styles.limitSelect} value={limit} onChange={(event) => setLimit(Number(event.target.value))}>{[10, 25, 50, 100, 500].map((value) => <option key={value} value={value}>{value}</option>)}</select></label>
        <Button variant="ghost" onClick={() => void runsRead.refresh()}>Refresh runs</Button>
      </div>
      <ReadStatus label="Ingestion runs" resource={runsRead} />
      {open != null && <Muted>{open} open runs in the received window. This is not a global readiness verdict.</Muted>}
      {runsRead.data && <ResultWorkspace scopeKey={JSON.stringify(['ingest-runs', tenant])} label="Ingestion run receipts" snapshot={runsRead.data} columns={columns}
        rowLabel={(run) => `${run.source_name} run ${run.run_id}`} filterText={filter} onFilterTextChange={(value) => setParam('source', value, true)} />}
      <Muted>The journal shows executions, not distinct content entities. A completed file may have been processed or skipped because its completion was already recorded.</Muted>
    </Panel>
    {expandedRun && <RunFiles key={JSON.stringify([tenant, expandedRun])} runId={expandedRun} tenant={tenant} live={live} onClose={() => setParam('run', null)} />}
    <Modal open={confirming != null} onClose={() => { if (!closingRef.current) setConfirming(null); }} title="Close this run receipt?"
      actions={<><Button variant="ghost" disabled={closing} onClick={() => setConfirming(null)}>Go back</Button><Button loading={closing} onClick={() => confirming && void closeReceipt(confirming)}>Mark receipt cancelled</Button></>}>
      <p>Source: {confirming?.source_name}. Run: <code>{confirming?.run_id}</code>.</p>
      <p>This changes the shared journal status. It is not confirmation that the ingest process or its database backends stopped. Stop active work first; use Activity to inspect running backends.</p>
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
    <div className={styles.toolbar}><label>Requested file window<select value={limit} onChange={(event) => setLimit(Number(event.target.value))}>{[250, 500, 1000, 5000].map((value) => <option key={value} value={value}>{value}</option>)}</select></label>
      <Button variant="ghost" disabled={!valid} onClick={() => void filesRead.refresh()}>Refresh file receipts</Button></div>
    {!valid ? <ErrorText>The run address is not a UUID. Choose a run from the journal.</ErrorText> : <ReadStatus label="File receipts" resource={filesRead} />}
    {filesRead.data && <ResultWorkspace scopeKey={JSON.stringify(['ingest-files', tenant, runId])} label="Ingested file receipts" snapshot={filesRead.data} columns={columns} rowLabel={(file) => file.file_label} />}
  </Panel>;
}
