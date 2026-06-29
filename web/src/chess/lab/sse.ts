export interface LabEvent {
  at?: string;
  level?: string;
  message?: string;
  done?: number;
  total?: number;
  label?: string;
  name?: string;
  value?: number;
  unit?: string;
  title?: string;
  rows?: string[][];
  finalState?: string;
}

export interface LabJob {
  id: string;
  kind: string;
  state: string;
  summary: { done: number; total: number; message?: string };
  artifacts: Record<string, string>;
}

export interface LabCatalog {
  jobs: { kind: string; label?: string }[];
  binaries: { cutechessOk: boolean; stockfishOk: boolean; qtOk: boolean };
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
