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
  let dataLines: string[] = [];
  let finished = false;
  let skipLf = false;
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) {
        // An EOF is not the protocol's completion marker. In particular, a
        // disconnect during the server's turn commit must not look successful.
        throw new ApiError(res.status, 'Response interrupted before completion was confirmed.');
      }
      buffer += decoder.decode(value, { stream: true });
      // SSE accepts LF, CRLF and CR line endings, including a CRLF split
      // across network reads. A blank line dispatches all data fields together.
      for (;;) {
        if (skipLf && buffer.length > 0) {
          if (buffer[0] === '\n') buffer = buffer.slice(1);
          skipLf = false;
        }
        const sep = buffer.search(/[\r\n]/);
        if (sep < 0) break;
        const line = buffer.slice(0, sep);
        skipLf = buffer[sep] === '\r';
        buffer = buffer.slice(sep + 1);
        if (line === '') {
          if (dataLines.length === 0) continue;
          const data = dataLines.join('\n');
          dataLines = [];
          if (data === '[DONE]') {
            if (!finished)
              throw new ApiError(res.status, 'Response ended without a completed answer.');
            return;
          }
          const chunk = parseFrame(data, res.status);
          if (chunk.choices?.some((choice) => choice.index === 0 && choice.finish_reason != null))
            finished = true;
          yield chunk;
        } else if (line === 'data') {
          dataLines.push('');
        } else if (line.startsWith('data:')) {
          const data = line.slice(5);
          dataLines.push(data.startsWith(' ') ? data.slice(1) : data);
        }
      }
    }
  } finally {
    // Release the response body when the consumer stops, an error frame arrives,
    // or [DONE] is seen before the server closes its side of the connection.
    try { await reader.cancel(); } catch { /* Preserve the original result/error. */ }
    reader.releaseLock();
  }
}
