import { useState } from 'react';
import { Button, ErrorText, Muted, Panel } from '@ui';
import { useAppStore } from '../store';
import { startIngest, stopIngest, type IngestStartReceipt } from './ingestControlApi';
import styles from './Admin.module.css';

export function IngestControl({ onStarted }: { onStarted?: () => void }) {
  const { tenant } = useAppStore();
  const [source, setSource] = useState('');
  const [path, setPath] = useState('');
  const [argumentsText, setArgumentsText] = useState('');
  const [starting, setStarting] = useState(false);
  const [stopping, setStopping] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [receipt, setReceipt] = useState<IngestStartReceipt | null>(null);
  const [processNote, setProcessNote] = useState<string | null>(null);

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
      onStarted?.();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : String(failure));
    } finally {
      setStarting(false);
    }
  }

  async function stop() {
    if (!receipt || stopping) return;
    setStopping(true);
    setError(null);
    try {
      const result = await stopIngest(receipt.pid, { tenant });
      setProcessNote(result.note);
      onStarted?.();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : String(failure));
    } finally {
      setStopping(false);
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
    {receipt && <section className={styles.processReceipt} aria-label="Started ingest process">
      <div>
        <strong>{receipt.source}</strong>
        <span className={styles.progressPct}>PID {receipt.pid}</span>
      </div>
      <code className={styles.processCommand} title={[receipt.cli, ...receipt.arguments].join(' ')}>
        {[receipt.cli, ...receipt.arguments].join(' ')}
      </code>
      <Button variant="ghost" loading={stopping} disabled={stopping || processNote != null} onClick={() => void stop()}>
        Stop process
      </Button>
      <span className={styles.progressPct}>{processNote ?? 'The stop control terminates this API-started CLI process tree; it does not rewrite the run receipt.'}</span>
    </section>}
  </Panel>;
}
