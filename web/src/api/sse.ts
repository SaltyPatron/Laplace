import { laplaceHeaders, PaymentRequiredError, ApiError, type ApiOptions, type PaymentRequiredResponse } from './client';





export interface ChatChunk {
  id: string;
  object: string;
  created: number;
  model: string;
  choices: {
    index: number;
    delta: { role?: string; content?: string };
    finish_reason: string | null;
  }[];
  laplace?: {
    eff_mu?: number;
    witnesses?: number;
    ord_used?: number;
  };
}





export async function* streamChat(
  path: string,
  payload: unknown,
  opts: ApiOptions,
  signal?: AbortSignal,
): AsyncGenerator<ChatChunk> {
  const res = await fetch(path, {
    method: 'POST',
    headers: laplaceHeaders(opts),
    body: JSON.stringify(payload),
    signal,
  });
  if (!res.ok) {
    let body: unknown = null;
    try {
      body = await res.json();
    } catch {
      
    }
    if (res.status === 402 && body) throw new PaymentRequiredError(body as PaymentRequiredResponse);
    throw new ApiError(res.status, `${res.status} ${res.statusText}`);
  }
  if (!res.body) throw new ApiError(res.status, 'Response has no body to stream.');

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
        const event = buffer.slice(0, sep);
        buffer = buffer.slice(sep + 2);
        for (const line of event.split('\n')) {
          if (!line.startsWith('data: ')) continue;
          const data = line.slice(6).trim();
          if (data === '[DONE]') return;
          yield JSON.parse(data) as ChatChunk;
        }
      }
    }
  } finally {
    reader.releaseLock();
  }
}
