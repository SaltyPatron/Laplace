import { useMemo, useState } from 'react';
import { Button, Input, Muted, cn } from '@ui';
import {
  clockOf,
  defaultFilter,
  enginesIn,
  foldTranscript,
  useStickToBottom,
  useTerminal,
  type TerminalDirection,
  type TerminalFilter,
  type TranscriptRow,
} from './terminal';
import styles from './Terminal.module.css';

/** Rendered rows are capped independently of the buffer — 6000 <div>s is not a scrollback. */
const RENDER_LIMIT = 1500;

const STREAM_CLASS: Record<string, string> = {
  command: styles.rowCommand,
  stdout: styles.rowStdout,
  stderr: styles.rowStderr,
  uci: styles.rowUci,
  runner: styles.rowRunner,
};

export interface TerminalProps {
  jobId: string | null;
  /** Shown as the pane's subtitle and offered for copy even before the job emits it. */
  command?: string | null;
  className?: string;
}

/**
 * The raw transcript of an external-process job: what was launched, everything both engines
 * said, and every warning, in the order it happened.
 */
export function Terminal({ jobId, command, className }: TerminalProps) {
  const { lines, missing, connected, error } = useTerminal(jobId);
  const [filter, setFilter] = useState<TerminalFilter>(defaultFilter);
  const [copied, setCopied] = useState(false);

  const engines = useMemo(() => enginesIn(lines), [lines]);
  const shown = useMemo(() => foldTranscript(lines, filter, RENDER_LIMIT), [lines, filter]);

  const { ref, follow, onScroll, resume } = useStickToBottom([shown.length, filterKey(filter)]);

  const commandLine = command ?? lines.find((l) => l.stream === 'command')?.text ?? null;
  const errors = useMemo(() => lines.filter((l) => l.stream === 'stderr').length, [lines]);

  const copy = async () => {
    if (!commandLine) return;
    try {
      await navigator.clipboard.writeText(commandLine);
      setCopied(true);
      setTimeout(() => setCopied(false), 1600);
    } catch {
      setCopied(false);
    }
  };

  return (
    <div className={cn(styles.terminal, className)}>
      <div className={styles.toolbar}>
        <div className={styles.filters} role="group" aria-label="Transcript filters">
          <FilterChip
            label="harness"
            title="cutechess-cli's own output"
            on={filter.stdout}
            onToggle={() => setFilter((f) => ({ ...f, stdout: !f.stdout }))}
          />
          <FilterChip
            label={errors > 0 ? `stderr ${errors}` : 'stderr'}
            title="Warnings and errors from the process"
            tone={errors > 0 ? 'error' : undefined}
            on={filter.stderr}
            onToggle={() => setFilter((f) => ({ ...f, stderr: !f.stderr }))}
          />
          <FilterChip
            label="UCI traffic"
            title="Every line exchanged with the engines"
            on={filter.uci}
            onToggle={() => setFilter((f) => ({ ...f, uci: !f.uci }))}
          />
          {filter.uci && engines.map((name) => (
            <FilterChip
              key={name}
              label={name}
              title={`Show ${name}'s traffic`}
              on={filter.engines.size === 0 || filter.engines.has(name)}
              onToggle={() => setFilter((f) => {
                const next = new Set(f.engines.size === 0 ? engines : f.engines);
                if (next.has(name)) next.delete(name); else next.add(name);
                return { ...f, engines: next.size === engines.length ? new Set() : next };
              })}
            />
          ))}
          {filter.uci && (['send', 'recv'] as TerminalDirection[]).map((dir) => (
            <FilterChip
              key={dir}
              label={dir === 'send' ? '▸ to engine' : '◂ from engine'}
              title={dir === 'send' ? 'Commands the harness sent' : 'Everything the engines replied'}
              on={filter.directions.has(dir)}
              onToggle={() => setFilter((f) => {
                const next = new Set(f.directions);
                if (next.has(dir)) next.delete(dir); else next.add(dir);
                return { ...f, directions: next };
              })}
            />
          ))}
        </div>

        <Input
          className={styles.search}
          type="search"
          value={filter.text}
          placeholder="Filter lines…"
          aria-label="Filter transcript lines"
          onChange={(e) => setFilter((f) => ({ ...f, text: e.target.value }))}
        />

        <div className={styles.toolbarRight}>
          <Muted className={styles.counts}>
            {shown.length.toLocaleString()} / {lines.length.toLocaleString()} lines
            {missing > 0 && <span className={styles.missing}> · {missing.toLocaleString()} dropped</span>}
          </Muted>
          <Button size="sm" variant="ghost" onClick={() => void copy()} disabled={!commandLine}>
            {copied ? 'Copied' : 'Copy command'}
          </Button>
          {jobId && (
            <a className={styles.download} href={`/chess/lab/jobs/${jobId}/terminal.txt`} download>
              Download
            </a>
          )}
        </div>
      </div>

      {error && <div className={styles.streamError}>transcript stream: {error}</div>}

      <div
        ref={ref}
        onScroll={onScroll}
        className={styles.scroll}
        tabIndex={0}
        role="log"
        aria-label="Process transcript"
      >
        {shown.length === 0 && (
          <div className={styles.empty}>
            {jobId
              ? lines.length === 0
                ? connected ? 'Waiting for output…' : 'No output recorded for this run.'
                : 'Every line is filtered out — turn a channel back on above.'
              : 'Start a run, or pick one from the history, to see its transcript.'}
          </div>
        )}
        {shown.map((row) => <Row key={row.line.seq} row={row} />)}
      </div>

      {!follow && (
        <button type="button" className={styles.jump} onClick={resume}>
          Jump to latest ↓
        </button>
      )}
    </div>
  );
}

function Row({ row }: { row: TranscriptRow }) {
  const { line, hidden, lost } = row;
  const tag = line.stream === 'uci' && line.engine
    ? `${line.direction === 'send' ? '▸' : '◂'} ${line.engine}`
    : line.stream;

  return (
    <>
      {/* Two different facts, never merged into one number: what you filtered out is still
          on the server, what was lost is gone. */}
      {hidden > 0 && (
        <div className={styles.elided}>⋯ {hidden.toLocaleString()} {plural(hidden, 'line')} hidden by filter</div>
      )}
      {lost > 0 && (
        <div className={styles.gap}>⋯ {lost.toLocaleString()} {plural(lost, 'line')} never reached this viewer</div>
      )}
      <div className={cn(styles.row, STREAM_CLASS[line.stream])}>
        <span className={styles.clock}>{clockOf(line.at)}</span>
        <span className={styles.tag}>{tag}</span>
        <span className={styles.text}>
          {line.stream === 'command' && <span className={styles.prompt}>$ </span>}
          {line.text}
        </span>
      </div>
    </>
  );
}

function FilterChip({
  label,
  title,
  on,
  tone,
  onToggle,
}: {
  label: string;
  title: string;
  on: boolean;
  tone?: 'error';
  onToggle: () => void;
}) {
  return (
    <button
      type="button"
      title={title}
      aria-pressed={on}
      className={cn(styles.chip, on && styles.chipOn, tone === 'error' && styles.chipError)}
      onClick={onToggle}
    >
      {label}
    </button>
  );
}

function plural(n: number, word: string): string {
  return n === 1 ? word : `${word}s`;
}

/** Filter identity for the stick-to-bottom effect — changing a filter re-anchors the view. */
function filterKey(f: TerminalFilter): string {
  return `${f.stdout}${f.stderr}${f.uci}${[...f.engines].join()}${[...f.directions].join()}${f.text}`;
}
