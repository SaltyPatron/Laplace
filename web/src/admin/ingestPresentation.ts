export const OPEN_INGEST_STATES = new Set(['running', 'composed', 'started', 'pending', 'in_progress']);
export function ingestStatusTone(status: string): 'ok' | 'running' | 'failed' | 'cancelled' {
  const normalized = status.toLowerCase();
  if (['ok', 'complete', 'completed', 'skipped-complete'].includes(normalized)) return 'ok';
  if (OPEN_INGEST_STATES.has(normalized)) return 'running';
  if (['failed', 'failure', 'error'].includes(normalized)) return 'failed';
  // No comparison/no measurement and unfamiliar future statuses are not failures.
  return 'cancelled';
}
export function ingestDuration(started: string | null, ended: string | null): string {
  if (!started) return 'Not recorded';
  const start = Date.parse(started), end = ended ? Date.parse(ended) : Date.now();
  if (!Number.isFinite(start) || !Number.isFinite(end)) return 'Not recorded';
  const seconds = Math.max(0, Math.round((end - start) / 1000));
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  return minutes < 60 ? `${minutes}m ${seconds % 60}s` : `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
}
export function countText(value: number | null | undefined): string {
  return value == null ? 'Not recorded' : value.toLocaleString();
}

/**
 * Ingest inventories are allowed to discover totals while streaming. A persisted zero
 * total with positive completed work therefore means "not known yet", not a literal
 * zero-sized workload. Never render impossible progress such as 34,359 / 0.
 */
export function progressText(done: number | null | undefined, total: number | null | undefined): string {
  const doneText = countText(done);
  if (total == null || ((total ?? 0) <= 0 && (done ?? 0) > 0)) return `${doneText} / total pending discovery`;
  return `${doneText} / ${countText(total)}`;
}

export interface RunFailure {
  /** Plain one-line explanation that never ends at a bare exit status. */
  summary: string;
  /** Pipeline stage the run last reported, if any. */
  stage: string | null;
  /** File the runner was working on, when its output names one. */
  file: string | null;
  /** Last lines the runner wrote to its error stream, if it wrote any. */
  excerpt: string | null;
  /** The exit status when the runner reported one. */
  exitStatus: number | null;
}

const EXCERPT_LINES = 12;

/**
 * Turns a run's stored error into stage, file and log excerpt. The CLI records a
 * failed native run as `Native ingestion exited with status N:` followed by the tail
 * of its stderr, or `... and wrote nothing to stderr.` when there was none.
 */
export function describeRunFailure(error: string, phase: string | null, runStatus = 'failed'): RunFailure {
  const ended = ingestStatusTone(runStatus) === 'failed' ? 'failed' : 'stopped';
  const text = error.replace(/\r\n/g, '\n').trim();
  const status = /exited with status (-?\d+)/i.exec(text);
  const exitStatus = status ? Number(status[1]) : null;
  const silent = /wrote nothing to stderr/i.test(text);
  const [first, ...rest] = text.split('\n');
  const body = (status ? rest : [first, ...rest]).map((line) => line.trimEnd()).filter((line) => line.trim().length > 0);
  const excerpt = silent || body.length === 0 ? null : body.slice(-EXCERPT_LINES).join('\n');
  const file = excerpt ? fileFrom(excerpt) : null;
  const stage = phase?.trim() || null;
  let summary: string;
  if (excerpt && status) {
    summary = `The runner stopped with status ${exitStatus}${stage ? ` during ${stage}` : ''}${file ? ` while reading ${file}` : ''}. Its last output is below.`;
  } else if (status) {
    summary = stage
      ? `The run exited with status ${exitStatus} during ${stage} without reporting a file or writing any error output.`
      : `The run exited with status ${exitStatus} without reporting a stage or file.`;
  } else {
    summary = stage ? `The run ${ended} during ${stage}.` : `The run ${ended}.`;
  }
  return { summary, stage, file, excerpt, exitStatus };
}

/** Finds a file name the runner printed, e.g. `file="a/b.xml"` or `file=a/b.xml`. */
function fileFrom(output: string): string | null {
  const quoted = /\bfile\s*[=:]\s*"([^"\n]+)"/i.exec(output);
  if (quoted) return quoted[1];
  const bare = /\bfile\s*[=:]\s*([^\s;,]+)/i.exec(output);
  if (bare) return bare[1];
  const path = /((?:[A-Za-z]:)?[\/]?(?:[\w.-]+[\/])+[\w.-]+\.\w{1,8})/.exec(output);
  return path ? path[1] : null;
}
