import { useState } from 'react';
import { Button, ErrorText, Muted, Panel } from '@ui';
import { useAppStore } from '../store';
import { startIngest, type IngestStartReceipt } from './ingestControlApi';
import styles from './Admin.module.css';

export function IngestControl() {
  const { tenant } = useAppStore();
  const [source, setSource] = useState('');
  const [path, setPath] = useState('');
  const [argumentsText, setArgumentsText] = useState('');
  const [starting, setStarting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [receipt, setReceipt] = useState<IngestStartReceipt | null>(null);

  async function start() {
    const sourceName = source.trim();
    if (!sourceName) {
      setError('Source is required. Use the same source key accepted by Laplace.Cli ingest.');
      return;
    }
    setStarting(true);
    setError(null);
    setReceipt(null);
    try {
      const args = argumentsText.split('\n').map((value) => value.trim()).filter(Boolean);
      const result = await startIngest({
        source: sourceName,
        ...(path.trim() ? { path: path.trim() } : {}),
        ...(args.length ? { arguments: args } : {}),
      }, { tenant });
      setReceipt(result);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : String(failure));
    } finally {
      setStarting(false);
    }
  }

  return <Panel title="Start ingestion" expandable>
    <Muted>
      Starts the canonical <code>Laplace.Cli ingest</code> lane on this host. The CLI remains the
      source/argument authority; progress and completion appear in the run and file journals below.
    </Muted>
    <label className={styles.field}>
      <span>Source key</span>
      <input value={source} onChange={(event) => setSource(event.target.value)}
        placeholder="wordnet, ud, repo, chess, document…" autoComplete="off" />
    </label>
    <label className={styles.field}>
      <span>Path (optional when the source has a configured/default path)</span>
      <input value={path} onChange={(event) => setPath(event.target.value)}
        placeholder="/data/corpus or D:\\data\\corpus" autoComplete="off" />
    </label>
    <label className={styles.field}>
      <span>Additional CLI arguments (optional; one argument per line)</span>
      <textarea value={argumentsText} onChange={(event) => setArgumentsText(event.target.value)}
        rows={4} placeholder={'--recursive\n--lang\nen'} />
    </label>
    <div className={styles.toolbar}>
      <Button loading={starting} disabled={starting || !source.trim()} onClick={() => void start()}>
        Start ingest
      </Button>
      <Muted>No HTTP request is held open for the ingest lifetime.</Muted>
    </div>
    {error && <ErrorText role="alert">{error}</ErrorText>}
    {receipt && <div>
      <strong>Started {receipt.source}</strong> as process <code>{receipt.pid}</code>.
      <div className={styles.progressPct}>Journal refresh will show the run after the CLI opens its canonical receipt.</div>
    </div>}
  </Panel>;
}
