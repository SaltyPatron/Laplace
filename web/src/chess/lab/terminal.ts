import { useEffect, useRef, useState } from 'react';
import { sseJson } from './sse';

export type TerminalStream = 'command' | 'stdout' | 'stderr' | 'uci' | 'runner';
export type TerminalDirection = 'send' | 'recv';

export interface TerminalLine {
  /**
   * Monotonic per job. A gap is not noise — it is exactly the lines this viewer missed,
   * to ring eviction or to backpressure, and the pane draws it rather than pretending
   * the transcript is continuous.
   */
  seq: number;
  at: string;
  stream: TerminalStream;
  text: string;
  engine?: string | null;
  direction?: TerminalDirection | null;
}

/** Client-side scrollback. The server ring is 4000; keeping a little more costs nothing. */
const MAX_LINES = 6000;

/**
 * Coalescing window. A UCI engine can emit hundreds of `info` lines a second; one React
 * commit per line drops frames on any machine. Lines land in a ref and flush on a timer.
 */
const FLUSH_MS = 90;

export interface TerminalState {
  lines: TerminalLine[];
  /** Lines the server produced that this viewer never received (seq gaps). */
  missing: number;
  connected: boolean;
  error: string | null;
}

/**
 * Follows a job's raw transcript. Safe to point at a finished job: the endpoint replays
 * whatever scrollback the server still holds and then ends, so a job opened from the
 * history list shows its transcript instead of an empty pane.
 */
export function useTerminal(jobId: string | null): TerminalState {
  const [state, setState] = useState<TerminalState>({ lines: [], missing: 0, connected: false, error: null });

  useEffect(() => {
    setState({ lines: [], missing: 0, connected: false, error: null });
    if (!jobId) return;

    const ac = new AbortController();
    const pending: TerminalLine[] = [];
    let flushHandle: ReturnType<typeof setInterval> | null = null;

    const flush = () => {
      if (pending.length === 0) return;
      const batch = pending.splice(0, pending.length);
      setState((prev) => {
        let missing = prev.missing;
        let last = prev.lines.length > 0 ? prev.lines[prev.lines.length - 1].seq : -1;
        for (const line of batch) {
          if (last >= 0 && line.seq > last + 1) missing += line.seq - last - 1;
          last = line.seq;
        }
        const lines = prev.lines.concat(batch);
        return {
          ...prev,
          missing,
          lines: lines.length > MAX_LINES ? lines.slice(lines.length - MAX_LINES) : lines,
        };
      });
    };

    void (async () => {
      let after = -1;
      // One reconnect: a stream cut mid-run resumes from the last rendered seq rather than
      // restarting the transcript or, worse, silently going dead while the match continues.
      for (let attempt = 0; attempt < 2 && !ac.signal.aborted; attempt++) {
        try {
          setState((p) => ({ ...p, connected: true, error: null }));
          flushHandle ??= setInterval(flush, FLUSH_MS);
          for await (const line of sseJson<TerminalLine>(
            `/chess/lab/jobs/${jobId}/terminal?after=${after}`, ac.signal)) {
            after = line.seq;
            pending.push(line);
          }
          break; // clean end of stream — the job finished
        } catch (e) {
          if (ac.signal.aborted) return;
          if (attempt === 1) {
            setState((p) => ({ ...p, error: e instanceof Error ? e.message : String(e) }));
          }
        }
      }
      flush();
      setState((p) => ({ ...p, connected: false }));
    })();

    return () => {
      ac.abort();
      if (flushHandle) clearInterval(flushHandle);
    };
  }, [jobId]);

  return state;
}

export interface TerminalFilter {
  stdout: boolean;
  stderr: boolean;
  uci: boolean;
  engines: Set<string>;
  directions: Set<TerminalDirection>;
  text: string;
}

export function defaultFilter(): TerminalFilter {
  return {
    stdout: true,
    stderr: true,
    // Off by default: the engine handshake alone is ~200 lines, and the harness's own
    // narration is what you want first. One click turns the firehose on.
    uci: false,
    engines: new Set(),
    directions: new Set<TerminalDirection>(['send', 'recv']),
    text: '',
  };
}

export function matchesFilter(l: TerminalLine, f: TerminalFilter, needle: string): boolean {
  if (l.stream === 'uci') {
    if (!f.uci) return false;
    if (f.engines.size > 0 && (!l.engine || !f.engines.has(l.engine))) return false;
    if (l.direction && !f.directions.has(l.direction)) return false;
  } else if (l.stream === 'stdout' && !f.stdout) return false;
  else if (l.stream === 'stderr' && !f.stderr) return false;
  if (needle && !l.text.toLowerCase().includes(needle)) return false;
  return true;
}

export interface TranscriptRow {
  line: TerminalLine;
  /** Lines before this one the reader chose not to see. */
  hidden: number;
  /**
   * Lines before this one that never reached this viewer — evicted from the server ring
   * before it connected, or dropped under backpressure. Not the same thing as `hidden`, and
   * conflating them is how a pane quietly turns "you lost data" into "you filtered it out".
   */
  lost: number;
}

/**
 * Fold the buffer into renderable rows, carrying forward what was elided between them and
 * why. Both counts come from the buffer's own seq numbering, so neither is inferred from
 * what happens to be on screen.
 */
export function foldTranscript(
  lines: TerminalLine[],
  filter: TerminalFilter,
  renderLimit: number,
): TranscriptRow[] {
  const needle = filter.text.trim().toLowerCase();
  const rows: TranscriptRow[] = [];
  let prevSeq = -1;
  let hidden = 0;
  let lost = 0;

  for (const line of lines) {
    if (prevSeq >= 0 && line.seq > prevSeq + 1) lost += line.seq - prevSeq - 1;
    prevSeq = line.seq;
    if (matchesFilter(line, filter, needle)) {
      rows.push({ line, hidden, lost });
      hidden = 0;
      lost = 0;
    } else {
      hidden++;
    }
  }

  if (rows.length <= renderLimit) return rows;
  // Rows past the render cap are elided too — fold them into the first row that survives
  // rather than letting the pane start mid-transcript with no explanation.
  const kept = rows.slice(rows.length - renderLimit);
  const dropped = rows.slice(0, rows.length - renderLimit);
  kept[0] = {
    ...kept[0],
    hidden: kept[0].hidden + dropped.length + dropped.reduce((n, r) => n + r.hidden, 0),
    lost: kept[0].lost + dropped.reduce((n, r) => n + r.lost, 0),
  };
  return kept;
}

/** Distinct engine names seen so far, for the filter chips. */
export function enginesIn(lines: TerminalLine[]): string[] {
  const names = new Set<string>();
  for (const l of lines) if (l.engine) names.add(l.engine);
  return [...names].sort();
}

/** Live-region-safe clock for a transcript row. */
export function clockOf(at: string): string {
  const d = new Date(at);
  return Number.isNaN(d.getTime()) ? '' : d.toISOString().slice(11, 23);
}

/**
 * Keeps a scroll region pinned to the bottom until the reader scrolls up, and re-pins when
 * they come back down.
 *
 * The naive version — recompute `follow` from every scroll event — detaches by itself during
 * a busy run. Appending lines fires scroll, trimming the rendered window moves scrollTop out
 * from under the reader, and the browser clamps scrollTop when content shrinks; all three
 * look exactly like "the user scrolled up" to a distance-from-bottom test. Only a scroll that
 * moves the viewport UP is the reader's intent, and content growth can never do that.
 */
export function useStickToBottom(deps: unknown[]) {
  const ref = useRef<HTMLDivElement | null>(null);
  const [follow, setFollow] = useState(true);
  const followRef = useRef(true);
  const lastTop = useRef(0);
  followRef.current = follow;

  const pin = () => {
    const el = ref.current;
    if (!el) return;
    el.scrollTop = el.scrollHeight;
    lastTop.current = el.scrollTop;
  };

  useEffect(() => {
    if (followRef.current) pin();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);

  const onScroll = () => {
    const el = ref.current;
    if (!el) return;
    const top = el.scrollTop;
    const wentUp = top < lastTop.current - 1;
    lastTop.current = top;

    // 24px of slack: coming back to the bottom should not need a pixel-perfect landing.
    if (el.scrollHeight - top - el.clientHeight < 24) setFollow(true);
    else if (wentUp) setFollow(false);
  };

  return { ref, follow, onScroll, pin, resume: () => { setFollow(true); pin(); } };
}
