import { laplaceHeaders, PaymentRequiredError, ApiError, type ApiOptions, type ErrorResponse, type PaymentRequiredResponse } from './client';





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
    performance?: {
      substrate_ms: string | number;
      elapsed_ms: string | number;
      first_result_ms?: string | number;
      output_utf8_bytes: string | number;
      output_codepoints: string | number;
      output_words: string | number;
      generated_tokens?: string | number;
      generated_tokens_per_second?: string | number;
    };
  };
}





/**
 * One SSE `data:` frame into a chunk — or a throw.
 *
 * A failing substrate does not close the stream: the endpoint answers 200,
 * writes `data: {"error":{…}}`, then `data: [DONE]`. Parsed blindly as a
 * ChatChunk that frame has no `choices` and no `laplace`, so every render
 * branch skips it and the turn ends with empty content and no error — the
 * reply silently disappears in front of the user. Error frames are raised
 * here so the one catch in the caller reports them like any other failure.
 */
function parseFrame(data: string, status: number): ChatChunk {
  let parsed: unknown;
  try {
    parsed = JSON.parse(data);
  } catch {
    throw new ApiError(status, 'Malformed stream frame from the substrate.');
  }
  const err = (parsed as ErrorResponse | undefined)?.error;
  if (err) {
    if (err.code === 'payment_required' || err.type === 'payment_required') {
      throw new PaymentRequiredError(parsed as PaymentRequiredResponse);
    }
    throw new ApiError(status, err.message ?? err.code ?? 'Substrate stream failed.');
  }
  return parsed as ChatChunk;
}

export async function* streamChat(
  path: string,
  payload: unknown,
  opts: ApiOptions,
  signal?: AbortSignal,
  onSession?: (sessionKey: string) => void,
): AsyncGenerator<ChatChunk> {
  const res = await fetch(path, {
    method: 'POST',
    headers: laplaceHeaders(opts),
    body: JSON.stringify(payload),
    signal,
  });
  // The session key arrives on the response headers before the stream body —
  // capture it so the next turn continues the same substrate session.
  const sessionKey = res.headers.get('X-Laplace-Session');
  if (sessionKey && onSession) onSession(sessionKey);
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
          yield parseFrame(data, res.status);
        }
      }
    }
  } finally {
    reader.releaseLock();
  }
}
