export interface LabEvent {
  at?: string;
  // log
  level?: string;
  message?: string;
  // progress
  done?: number;
  total?: number;
  label?: string;
  // metric
  name?: string;
  value?: number;
  unit?: string;
  // table
  title?: string;
  columns?: string[];
  rows?: string[][];
  // per-game
  index?: number;
  white?: string;
  black?: string;
  result?: string;
  pgnPath?: string;
  // board (one ply of a live game — presence of `fen` marks it)
  game?: number;
  ply?: number;
  uci?: string;
  fen?: string;
  // done
  finalState?: string;
}

export interface LabJob {
  id: string;
  kind: string;
  state: string;
  summary: { done: number; total: number; message?: string };
  artifacts: Record<string, string>;
}

export interface LabJobSpec {
  kind: string;
  label?: string;
  default?: Record<string, string>;
}

export interface LabEngine {
  path: string;
  found: boolean;
  source: string;
}

export interface LabCatalog {
  jobs: LabJobSpec[];
  engines: Record<string, LabEngine>;
}

export async function* streamLabEvents(jobId: string, signal?: AbortSignal): AsyncGenerator<LabEvent> {
  const res = await fetch(`/chess/lab/jobs/${jobId}/events`, { signal });
  if (!res.ok || !res.body) throw new Error(`${res.status} SSE failed`);
  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      let sep: number;
      while ((sep = buffer.indexOf('\n\n')) >= 0) {
        const block = buffer.slice(0, sep);
        buffer = buffer.slice(sep + 2);
        for (const line of block.split('\n')) {
          if (!line.startsWith('data: ')) continue;
          yield JSON.parse(line.slice(6)) as LabEvent;
        }
      }
    }
  } finally {
    reader.releaseLock();
  }
}
