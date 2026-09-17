import { useState } from 'react';
import { Button, ErrorText, Muted, Panel, ReadStatus, useReadResource } from '@ui';
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
  const [path, setPath] = useState('');
  const [argumentsText, setArgumentsText] = useState('');
  const [starting, setStarting] = useState(false);
  const [stoppingPid, setStoppingPid] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [receipt, setReceipt] = useState<IngestStartReceipt | null>(null);
  const [processNote, setProcessNote] = useState<string | null>(null);
  const processesRead = useReadResource({
    key: JSON.stringify(['ingest-processes', tenant]),
    refreshMs: PROCESS_REFRESH_MS,
    read: (signal) => listIngestProcesses({ tenant, signal }),
  });

  async function start() {
    const sourceName = source.trim();
    if (!sourceName) {
      setError('Source is required. Use the same source key accepted by Laplace.Cli ingest.');
      return;
    }
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
      await processesRead.refresh();
      onStarted?.();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : String(failure));
    } finally {
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
      await processesRead.refresh();
      onStarted?.();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : String(failure));
    } finally {
      setStoppingPid(null);
    }
  }

  return <Panel title="Start ingestion" expandable>
    <Muted>
      Launches the canonical <code>Laplace.Cli ingest</code> process on this host. The CLI owns source
      names, defaults and argument validation; the journal below owns run progress and completion.
    </Muted>
    <div className={styles.ingestFormGrid}>
      <label className={styles.field}>
        <span>Source key</span>
        <input value={source} onChange={(event) => setSource(event.target.value)}
          placeholder="wordnet, ud, repo, chess, document…" autoComplete="off" />
      </label>
      <label className={styles.field}>
        <span>Path <em>optional when the source has a configured/default path</em></span>
        <input value={path} onChange={(event) => setPath(event.target.value)}
          placeholder="/data/corpus or D:\\data\\corpus" autoComplete="off" />
      </label>
      <label className={`${styles.field} ${styles.ingestArgs}`}>
        <span>Additional CLI arguments <em>optional · one argument per line</em></span>
        <textarea value={argumentsText} onChange={(event) => setArgumentsText(event.target.value)}
          rows={3} placeholder={'--recursive\n--lang\nen'} />
      </label>
    </div>
    <div className={styles.toolbar}>
      <Button loading={starting} disabled={starting || !source.trim()} onClick={() => void start()}>
        Start ingest
      </Button>
      <Muted>Launch returns immediately; progress is journaled independently.</Muted>
    </div>
    {error && <ErrorText role="alert">{error}</ErrorText>}
    {receipt && <p className={styles.processEvent} role="status">
      Started <strong>{receipt.source}</strong> as PID <code>{receipt.pid}</code>.
    </p>}
    {processNote && <p className={styles.processEvent} role="status">{processNote}</p>}

    <div className={styles.processHeader}>
      <strong>Processes started by this host</strong>
      <Button variant="ghost" onClick={() => void processesRead.refresh()}>Refresh processes</Button>
    </div>
    <ReadStatus label="Ingest processes" resource={processesRead} />
    {processesRead.data && (processesRead.data.data.length === 0
      ? <Muted>No live API-started ingest processes on this server instance.</Muted>
      : <div className={styles.processList}>
        {processesRead.data.data.map((process) => <section key={process.pid} className={styles.processReceipt} aria-label={`${process.source} process ${process.pid}`}>
          <div>
            <strong>{process.source}</strong>
            <span className={styles.progressPct}>PID {process.pid} · started {new Date(process.started_at).toLocaleString()}</span>
          </div>
          <code className={styles.processCommand} title={[process.cli, ...process.arguments].join(' ')}>
            {[process.cli, ...process.arguments].join(' ')}
          </code>
          <Button variant="ghost" loading={stoppingPid === process.pid} disabled={stoppingPid != null} onClick={() => void stop(process)}>
            Stop process
          </Button>
          <span className={styles.progressPct}>Stops this owned CLI process tree. The canonical run journal remains the execution record.</span>
        </section>)}
      </div>)}
  </Panel>;
}
