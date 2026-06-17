import type { components } from './types.gen';

export type Schemas = components['schemas'];
export type ErrorResponse = Schemas['ErrorResponse'];
export type PaymentRequiredResponse = Schemas['PaymentRequiredResponse'];
export type ChatCompletionResponse = Schemas['ChatCompletionResponse'];
export type EvidenceResponse = Schemas['EvidenceResponse'];
export type ModelList = Schemas['ModelList'];
export type PlanView = Schemas['PlanView'];
export type BillingPlansResponse = Schemas['BillingPlansResponse'];
export type CatalogServiceView = Schemas['CatalogServiceView'];
export type BillingCatalogResponse = Schemas['BillingCatalogResponse'];
export type PreflightQuoteResponse = Schemas['PreflightQuoteResponse'];
export type PlanSubscribeResponse = Schemas['PlanSubscribeResponse'];
export type UsageResponse = Schemas['UsageResponse'];
export type ProvenanceLine = Schemas['ProvenanceLine'];

export interface ApiOptions {
  tenant?: string;
  quoteId?: string;
}


export class PaymentRequiredError extends Error {
  constructor(public readonly body: PaymentRequiredResponse) {
    super(body.error.message ?? 'Payment required');
  }
}

export class ApiError extends Error {
  constructor(public readonly status: number, message: string) {
    super(message);
  }
}

export function laplaceHeaders(opts: ApiOptions): Record<string, string> {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (opts.tenant) headers['X-Laplace-Tenant'] = opts.tenant;
  if (opts.quoteId) headers['X-Laplace-Quote-Id'] = opts.quoteId;
  return headers;
}

async function parseError(res: Response): Promise<never> {
  let message = `${res.status} ${res.statusText}`;
  let body: unknown = null;
  try {
    body = await res.json();
    const err = (body as ErrorResponse).error;
    if (err?.message) message = err.message;
  } catch {
    
  }
  if (res.status === 402 && body) throw new PaymentRequiredError(body as PaymentRequiredResponse);
  throw new ApiError(res.status, message);
}

export async function apiGet<T>(path: string, opts: ApiOptions = {}): Promise<T> {
  const res = await fetch(path, { headers: laplaceHeaders(opts) });
  if (!res.ok) await parseError(res);
  return (await res.json()) as T;
}

export async function apiPost<T>(path: string, payload: unknown, opts: ApiOptions = {}): Promise<T> {
  const res = await fetch(path, {
    method: 'POST',
    headers: laplaceHeaders(opts),
    body: JSON.stringify(payload),
  });
  if (!res.ok) await parseError(res);
  return (await res.json()) as T;
}
