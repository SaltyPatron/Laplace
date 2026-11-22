import { apiPost, laplaceHeaders, ApiError, PaymentRequiredError, type PaymentRequiredResponse, type ErrorResponse } from '../api/client';
import type { components } from '../api/types.gen';

export type CodeCompletionRequest = components['schemas']['CodeCompletionRequest'];
export type CodeCompletionResponse = components['schemas']['CodeCompletionResponse'];
export type CodeAttemptReceipt = components['schemas']['CodeAttemptReceipt'];
export type CodeSearchRequest = components['schemas']['CodeSearchRequest'];

export interface CodeSearchResult {
  matches?: {
    id_hex?: string;
    path?: string;
    symbol?: string;
    label?: string;
    snippet?: string;
    eff_mu?: number | null;
    witnesses?: number | null;
    salience?: number | null;
  }[];
  results?: CodeSearchResult['matches'];
  total?: number;
}

/**
 * Calls POST /v1/code/completions preserving the structured CodeCompletionResponse
 * (attempt toolchain receipts, exit_code, stdout/stderr, failure_kind) on 422 / 503.
 */
export async function completeCode(
  body: CodeCompletionRequest,
  opts: { tenant: string; quoteId?: string; signal?: AbortSignal },
): Promise<{ status: number; data: CodeCompletionResponse }> {
  const res = await fetch('/v1/code/completions', {
    method: 'POST',
    headers: laplaceHeaders(opts),
    body: JSON.stringify(body),
    signal: opts.signal,
  });
  const requestId = res.headers.get('x-correlation-id') ?? res.headers.get('x-request-id') ?? undefined;
  let parsed: unknown = null;
  try {
    parsed = await res.json();
  } catch {
    throw new ApiError(res.status, `${res.status} ${res.statusText}`, requestId);
  }
  if (res.status === 402) {
    throw new PaymentRequiredError(parsed as PaymentRequiredResponse);
  }
  if ((parsed as CodeCompletionResponse | null)?.object === 'code.completion' || Array.isArray((parsed as CodeCompletionResponse | null)?.attempts)) {
    return { status: res.status, data: parsed as CodeCompletionResponse };
  }
  if (!res.ok) {
    const err = (parsed as ErrorResponse | null)?.error;
    throw new ApiError(res.status, err?.message ?? `${res.status} ${res.statusText}`, requestId);
  }
  return { status: res.status, data: parsed as CodeCompletionResponse };
}

export function searchCode(body: CodeSearchRequest, opts: { tenant: string; quoteId?: string; signal?: AbortSignal }) {
  return apiPost<CodeSearchResult>('/v1/code/search', body, opts);
}
