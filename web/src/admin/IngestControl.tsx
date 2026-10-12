import { useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { Button, ErrorText, Input, Modal, Muted, Panel, ReadStatus, Select, useReadResource } from '@ui';
import { apiGet } from '../api/client';
import { useAppStore } from '../store';
import {
  listIngestProcesses,
  startIngest,
  stopIngest,
  type IngestProcessReceipt,
  type IngestStartReceipt,
} from './ingestControlApi';
import styles from './Admin.module.css';

const PROCESS_REFRESH_MS = 5000;

export function IngestControl({ onStarted }: { onStarted?: () => void }) {
  const { tenant } = useAppStore();
  const [source, setSource] = useState('');
  const [custom, setCustom] = useState(false);
  const [path, setPath] = useState('');
  const [argumentsText, setArgumentsText] = useState('');
  const [starting, setStarting] = useState(false);
  const [stoppingPid, setStoppingPid] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [receipt, setReceipt] = useState<IngestStartReceipt | null>(null);
  const [processNote, setProcessNote] = useState<string | null>(null);
  const [review, setReview] = useState(false);
  const [stopTarget, setStopTarget] = useState<IngestProcessReceipt | null>(null);
  const submitting = useRef(false);
  const sourcesRead = useReadResource({ key: JSON.stringify(['ingest-sources', tenant]),
    read: (signal) => apiGet<{ sources: string[]; cliAvailable: boolean; host: string }>('/v1/admin/ingest/sources', { signal }) });
  const processesRead = useReadResource({
    key: JSON.stringify(['ingest-processes', tenant]),
    refreshMs: PROCESS_REFRESH_MS,
    read: (signal) => listIngestProcesses({ tenant, signal }),
  });

  async function start() {
    if (submitting.current) return;
    const sourceName = source.trim();
    if (!sourceName) {
      setError('Choose a source before starting ingestion.');
      return;
    }
    submitting.current = true;
    setStarting(true);
    setError(null);
    setReceipt(null);
    setProcessNote(null);
    try {
      const args = argumentsText.split('\n').map((value) => value.trim()).filter(Boolean);
      const result = await startIngest({
        source: sourceName,
        ...(path.trim() ? { path: path.trim() } : {}),
        ...(args.length ? { arguments: args } : {}),
      }, { tenant });
      setReceipt(result);
      setReview(false);
      await processesRead.refresh();
      onStarted?.();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : String(failure));
    } finally {
      submitting.current = false;
      setStarting(false);
    }
  }

  async function stop(process: IngestProcessReceipt) {
    if (stoppingPid != null) return;
    setStoppingPid(process.pid);
    setError(null);
    setProcessNote(null);
    try {
      const result = await stopIngest(process.pid, { tenant });
      setProcessNote(result.note);
      setStopTarget(null);
      await processesRead.refresh();
      onStarted?.();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : String(failure));
    } finally {
      setStoppingPid(null);
    }
  }

  return <Panel title="New ingestion run">
    <p>Import a source already available on the server. For files on your computer, <Link to="/data">open the upload workspace</Link>.</p>
    <ReadStatus label="Available sources" resource={sourcesRead} />
    {sourcesRead.data?.cliAvailable === false && <ErrorText>The ingestion runner is not installed on this host. A platform administrator needs to repair the installation before a run can start.</ErrorText>}
    <div className={styles.ingestFormGrid}>
      <label className={styles.field}>
        <span>1. Source</span>
        <Select value={custom ? '__custom' : source} onChange={(event) => { setCustom(event.target.value === '__custom'); setSource(event.target.value === '__custom' ? '' : event.target.value); }} disabled={starting}>
          <option value="">Choose a source…</option>{sourcesRead.data?.sources.map((key) => <option key={key} value={key}>{sourceName(key)}</option>)}
          <option value="__custom">Other CLI source…</option>
        </Select>
      </label>
      <label className={styles.field}>
        <span>2. Server file or folder <em>optional for a configured source</em></span>
        <Input value={path} onChange={(event) => setPath(event.target.value)}
          placeholder="Leave empty to use the configured source location" autoComplete="off" disabled={starting} />
      </label>
      {custom && <label className={styles.field}><span>CLI source name</span><Input value={source} onChange={(event) => setSource(event.target.value)} placeholder="chess, model, recipe…" disabled={starting} /></label>}
    </div>
    <details className={styles.advanced}><summary>Advanced options</summary><label className={styles.field}>
      <span>Additional arguments <em>one argument per line</em></span><textarea value={argumentsText} onChange={(event) => setArgumentsText(event.target.value)} rows={3} placeholder={'--recursive\n--lang\nen'} disabled={starting} />
    </label><Muted>Source names and options use the installed ingestion runner. Arguments are passed directly without a shell.</Muted></details>
    <div className={styles.toolbar}>
      <Button disabled={starting || !source.trim() || !sourcesRead.data?.cliAvailable || !!sourcesRead.error} onClick={() => { setError(null); setReview(true); }}>
        Review run
      </Button>
      <Muted>Review the source and destination before starting.</Muted>
    </div>
    {error && <ErrorText role="alert">{error}</ErrorText>}
    {receipt && <p className={styles.processEvent} role="status">
      Launched <strong>{sourceName(receipt.source)}</strong>. Follow its progress and file receipts in Ingestion runs below.
    </p>}
    {processNote && <p className={styles.processEvent} role="status">{processNote}</p>}

    <div className={styles.processHeader}>
      <strong>Active ingestion jobs</strong>
      <Button variant="ghost" onClick={() => void processesRead.refresh()}>Refresh jobs</Button>
    </div>
    <ReadStatus label="Ingest processes" resource={processesRead} />
    {processesRead.data && (processesRead.data.data.length === 0
      ? <Muted>No active jobs started by this server. Earlier and externally started runs appear in the journal below.</Muted>
      : <div className={styles.processList}>
        {processesRead.data.data.map((process) => <section key={process.pid} className={styles.processReceipt} aria-label={`${process.source} process ${process.pid}`}>
          <div>
            <strong>{sourceName(process.source)}</strong>
            <span className={styles.progressPct}>Started {new Date(process.started_at).toLocaleString()}</span>
          </div>
          <details><summary>Job details</summary><p>Process {process.pid}</p><code className={styles.processCommand}>{[process.cli, ...process.arguments].join(' ')}</code></details>
          <Button variant="ghost" loading={stoppingPid === process.pid} disabled={stoppingPid != null} onClick={() => setStopTarget(process)}>
            Stop job…
          </Button>
        </section>)}
      </div>)}
    <Modal open={review} onClose={() => { if (!starting) setReview(false); }} title="Review ingestion run" actions={<>
      <Button variant="ghost" disabled={starting} onClick={() => setReview(false)}>Back</Button>
      <Button loading={starting} disabled={starting} onClick={() => void start()}>Start run</Button></>}>
      <dl className={styles.reviewDetails}><dt>Source</dt><dd>{sourceName(source)}</dd><dt>Server</dt><dd>{sourcesRead.data?.host ?? window.location.host}</dd><dt>Location</dt><dd>{path.trim() || 'Configured source location'}</dd><dt>Destination</dt><dd>Shared substrate</dd></dl>
      {argumentsText.trim() && <details><summary>Additional arguments</summary><pre>{argumentsText}</pre></details>}
      <p>The run continues if you leave this page. Progress and completion appear in the ingestion journal.</p>{error && <ErrorText role="alert">{error}</ErrorText>}
    </Modal>
    <Modal open={stopTarget !== null} onClose={() => { if (stoppingPid === null) setStopTarget(null); }} title="Stop ingestion job?" actions={<>
      <Button variant="ghost" disabled={stoppingPid !== null} onClick={() => setStopTarget(null)}>Keep running</Button>
      <Button loading={stoppingPid !== null} disabled={stoppingPid !== null} onClick={() => stopTarget && void stop(stopTarget)}>Stop job</Button></>}>
      <p>Stop {sourceName(stopTarget?.source ?? '')} on this server. Data already committed remains available; stopping does not undo an ingestion.</p>{error && <ErrorText role="alert">{error}</ErrorText>}
    </Modal>
  </Panel>;
}

const SOURCE_NAMES: Record<string, string> = { document: 'Documents', repo: 'Code repository', code: 'Source code', tabular: 'Tables', parquet: 'Parquet data',
  wordnet: 'WordNet', wiktionary: 'Wiktionary', framenet: 'FrameNet', ud: 'Universal Dependencies', omw: 'Open Multilingual WordNet',
  unicode: 'Unicode', conceptnet: 'ConceptNet', chess: 'Chess games', model: 'Model snapshot', 'rgba-image': 'Images', 'track-audio': 'Audio', 'frame-video': 'Video' };
function sourceName(source: string) { return SOURCE_NAMES[source] ?? source; }
