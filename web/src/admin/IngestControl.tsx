import { useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { Badge, Button, ErrorText, Input, Modal, Muted, Panel, ReadStatus, Select, useReadResource } from '@ui';
import { useAppStore } from '../store';
import {
  listIngestProcesses,
  listIngestSources,
  planIngestSource,
  startIngest,
  stopIngest,
  type IngestPlanResult,
  type IngestProcessReceipt,
  type IngestSourceEntry,
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
  const [planning, setPlanning] = useState(false);
  const [planResult, setPlanResult] = useState<IngestPlanResult | null>(null);
  const [stoppingPid, setStoppingPid] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [receipt, setReceipt] = useState<IngestStartReceipt | null>(null);
  const [processNote, setProcessNote] = useState<string | null>(null);
  const [review, setReview] = useState(false);
  const [stopTarget, setStopTarget] = useState<IngestProcessReceipt | null>(null);
  const submitting = useRef(false);
  const sourcesRead = useReadResource({
    key: JSON.stringify(['ingest-sources', tenant]),
    read: (signal) => listIngestSources({ tenant, signal }),
  });
  const processesRead = useReadResource({
    key: JSON.stringify(['ingest-processes', tenant]),
    refreshMs: PROCESS_REFRESH_MS,
    read: (signal) => listIngestProcesses({ tenant, signal }),
  });

  const entries: IngestSourceEntry[] = sourcesRead.data?.entries ?? [];
  const stagedSources = entries.filter((e) => e.kind === 'recipe_source' && e.staged_on_disk);
  const directLanes = entries.filter((e) => e.kind === 'direct_lane');
  const unstagedSources = entries.filter((e) => e.kind === 'recipe_source' && !e.staged_on_disk);
  const selectedEntry = entries.find((e) => e.name.toLowerCase() === source.trim().toLowerCase()) ?? null;

  async function runPreviewPlan() {
    const sourceName = source.trim();
    if (!sourceName) return;
    setPlanning(true);
    setError(null);
    setPlanResult(null);
    try {
      setPlanResult(await planIngestSource(sourceName, { tenant }));
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : String(failure));
    } finally {
      setPlanning(false);
    }
  }

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
        <Select value={custom ? '__custom' : source} onChange={(event) => {
          setCustom(event.target.value === '__custom');
          setSource(event.target.value === '__custom' ? '' : event.target.value);
          setPlanResult(null);
        }} disabled={starting}>
          <option value="">Choose a source…</option>
          {entries.length > 0 ? (
            <>
              {stagedSources.length > 0 && (
                <optgroup label={`Staged on disk (${stagedSources.length})`}>
                  {stagedSources.map((entry) => (
                    <option key={entry.name} value={entry.name}>
                      {`${sourceName(entry.name)}${entry.admitted_attestations != null ? ` (${entry.admitted_attestations.toLocaleString()} admitted)` : ''}`}
                    </option>
                  ))}
                </optgroup>
              )}
              {directLanes.length > 0 && (
                <optgroup label="Direct path / media lanes">
                  {directLanes.map((entry) => (
                    <option key={entry.name} value={entry.name}>{sourceName(entry.name)}</option>
                  ))}
                </optgroup>
              )}
              {unstagedSources.length > 0 && (
                <optgroup label={`Configured recipes — not staged on host (${unstagedSources.length})`}>
                  {unstagedSources.map((entry) => (
                    <option key={entry.name} value={entry.name}>
                      {`${sourceName(entry.name)}${entry.admitted_attestations != null ? ` (${entry.admitted_attestations.toLocaleString()} admitted)` : ''}`}
                    </option>
                  ))}
                </optgroup>
              )}
            </>
          ) : (
            sourcesRead.data?.sources.map((key) => <option key={key} value={key}>{sourceName(key)}</option>)
          )}
          <option value="__custom">Other CLI source…</option>
        </Select>
      </label>
      <label className={styles.field}>
        <span>2. Server file or folder <em>{selectedEntry?.kind === 'direct_lane' ? 'required for direct lane' : 'optional for a configured source'}</em></span>
        <Input value={path} onChange={(event) => setPath(event.target.value)}
          placeholder={selectedEntry?.matched_path ?? 'Leave empty to use the configured source location'} autoComplete="off" disabled={starting} />
      </label>
      {custom && <label className={styles.field}><span>CLI source name</span><Input value={source} onChange={(event) => setSource(event.target.value)} placeholder="chess, model, recipe…" disabled={starting} /></label>}
    </div>
    {selectedEntry && (
      <div className={styles.processReceipt}>
        <Badge>{selectedEntry.stage_status}</Badge>
        {selectedEntry.trust_class && <Badge>{selectedEntry.trust_class}</Badge>}
        {selectedEntry.admitted_attestations != null && <Badge>{selectedEntry.admitted_attestations.toLocaleString()} admitted attestations</Badge>}
        {selectedEntry.matched_path && <code>{selectedEntry.matched_path}</code>}
        {selectedEntry.after.length > 0 && <Muted>{`After: ${selectedEntry.after.join(', ')}`}</Muted>}
      </div>
    )}
    <details className={styles.advanced}><summary>Advanced options</summary><label className={styles.field}>
      <span>Additional arguments <em>one argument per line</em></span><textarea value={argumentsText} onChange={(event) => setArgumentsText(event.target.value)} rows={3} placeholder={'--recursive\n--lang\nen'} disabled={starting} />
    </label><Muted>Source names and options use the installed ingestion runner. Arguments are passed directly without a shell.</Muted></details>
    <div className={styles.toolbar}>
      {selectedEntry?.kind === 'recipe_source' && (
        <Button variant="ghost" loading={planning} disabled={starting || planning || !source.trim() || !sourcesRead.data?.cliAvailable} onClick={() => void runPreviewPlan()}>
          Preview (--plan)
        </Button>
      )}
      <Button disabled={starting || !source.trim() || !sourcesRead.data?.cliAvailable || !!sourcesRead.error} onClick={() => { setError(null); setReview(true); }}>
        Review run
      </Button>
      <Muted>Preview resolved files (--plan) or review destination before starting.</Muted>
    </div>
    {error && <ErrorText role="alert">{error}</ErrorText>}
    {planResult && (
      <div className={styles.processReceipt}>
        <strong>{`Dry-run plan — ${sourceName(planResult.source)} (${planResult.ok ? 'ok' : `exit ${planResult.exit_code}`}, ${planResult.elapsed_ms}ms)`}</strong>
        {planResult.stdout && <pre className={styles.processCommand}>{planResult.stdout}</pre>}
        {planResult.stderr && <pre className={styles.processCommand}>{planResult.stderr}</pre>}
      </div>
    )}
    {receipt && <p className={styles.processEvent} role="status">
      Launched <strong>{sourceName(receipt.source)}</strong>. Follow live job output below and witness testimony in the table below.
    </p>}
    {processNote && <p className={styles.processEvent} role="status">{processNote}</p>}

    <div className={styles.processHeader}>
      <strong>Active ingestion jobs</strong>
      <Button variant="ghost" onClick={() => void processesRead.refresh()}>Refresh jobs</Button>
    </div>
    <ReadStatus label="Ingest processes" resource={processesRead} />
    {processesRead.data && (processesRead.data.data.length === 0
      ? <Muted>No active jobs started by this server. Earlier and externally started runs appear below.</Muted>
      : <div className={styles.processList}>
        {processesRead.data.data.map((process) => <section key={process.pid} className={styles.processReceipt} aria-label={`${process.source} process ${process.pid}`}>
          <div>
            <strong>{sourceName(process.source)}</strong>
            <span className={styles.progressPct}>Started {new Date(process.started_at).toLocaleString()}</span>
          </div>
          <details open><summary>Job details & live output</summary>
            <p>Process {process.pid}</p>
            <code className={styles.processCommand}>{[process.cli, ...process.arguments].join(' ')}</code>
            {process.stdout && <pre className={styles.processCommand}>{process.stdout}</pre>}
            {process.stderr && <pre className={styles.processCommand}>{process.stderr}</pre>}
          </details>
          <Button variant="ghost" loading={stoppingPid === process.pid} disabled={stoppingPid != null} onClick={() => setStopTarget(process)}>
            Stop job…
          </Button>
        </section>)}
      </div>)}
    {(processesRead.data?.completed?.length ?? 0) > 0 && (
      <div className={styles.processList}>
        <strong>Completed / failed runs</strong>
        {processesRead.data!.completed!.map((done) => (
          <section key={`${done.pid}:${done.ended_at}`} className={styles.processReceipt}>
            <div>
              <strong>{`${sourceName(done.source)} — ${done.status}${done.exit_code != null ? ` (exit ${done.exit_code})` : ''}`}</strong>
              <span className={styles.progressPct}>{`Ended ${new Date(done.ended_at).toLocaleString()}`}</span>
            </div>
            {(done.stdout || done.stderr || done.cli) && (
              <details>
                <summary>Exit log</summary>
                {done.cli && <code className={styles.processCommand}>{[done.cli, ...(done.arguments ?? [])].join(' ')}</code>}
                {done.stdout && <pre className={styles.processCommand}>{done.stdout}</pre>}
                {done.stderr && <pre className={styles.processCommand}>{done.stderr}</pre>}
              </details>
            )}
          </section>
        ))}
      </div>
    )}
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
