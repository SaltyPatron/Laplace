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
  session?: string;
  operatorToken?: string;
  signal?: AbortSignal;
}

// The shell supplies the server-confirmed workspace, not a browser identity.
// This header asserts request intent so a workspace switch in another tab cannot
// silently apply a stale form to the newly selected company.
let browserWorkspace: string | null = null;
export function setApiWorkspace(tenant: string | null): void { browserWorkspace = tenant; }

export class PaymentRequiredError extends Error {
  constructor(public readonly body: PaymentRequiredResponse) {
    super(body.error.message ?? 'Payment required');
  }
}
export class ApiError extends Error {
  constructor(public readonly status: number, message: string) { super(message); }
}

export function laplaceHeaders(opts: ApiOptions): Record<string, string> {
  const headers: Record<string, string> = { 'Content-Type': 'application/json', 'X-Laplace-Request': '1' };
  if (opts.tenant) headers['X-Laplace-Tenant'] = opts.tenant;
  if (browserWorkspace) headers['X-Laplace-Workspace'] = opts.tenant ?? browserWorkspace;
  if (opts.quoteId) headers['X-Laplace-Quote-Id'] = opts.quoteId;
  if (opts.session) headers['X-Laplace-Session'] = opts.session;
  if (opts.operatorToken) headers['X-Laplace-Operator-Token'] = opts.operatorToken;
  return headers;
}

async function parseError(res: Response): Promise<never> {
  let message = `${res.status} ${res.statusText}`;
  let body: unknown = null;
  try {
    body = await res.json();
    const err = (body as ErrorResponse).error;
    if (err?.message) message = err.message;
  } catch { /* Preserve the HTTP failure when a proxy returns non-JSON. */ }
  if (res.status === 402 && body) throw new PaymentRequiredError(body as PaymentRequiredResponse);
  throw new ApiError(res.status, message);
}

export async function apiGet<T>(path: string, opts: ApiOptions = {}): Promise<T> {
  const res = await fetch(path, { headers: laplaceHeaders(opts), signal: opts.signal, credentials: 'same-origin' });
  if (!res.ok) await parseError(res);
  return (await res.json()) as T;
}

/** Preserve serialized operator configuration byte-for-byte. */
export async function apiPutText<T>(path: string, body: string, opts: ApiOptions = {}): Promise<T> {
  const res = await fetch(path, {
    method: 'PUT', headers: laplaceHeaders(opts), body, signal: opts.signal, credentials: 'same-origin',
  });
  if (!res.ok) await parseError(res);
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

export async function apiPost<T>(path: string, payload: unknown, opts: ApiOptions = {}): Promise<T> {
  const res = await fetch(path, {
    method: 'POST', headers: laplaceHeaders(opts), body: JSON.stringify(payload), signal: opts.signal, credentials: 'same-origin',
  });
  if (!res.ok) await parseError(res);
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

export function apiPut<T>(path: string, payload: unknown, opts: ApiOptions = {}): Promise<T> {
  return apiPutText<T>(path, JSON.stringify(payload), opts);
}

export async function apiDelete<T = void>(path: string, opts: ApiOptions = {}): Promise<T> {
  const res = await fetch(path, {
    method: 'DELETE', headers: laplaceHeaders(opts), signal: opts.signal, credentials: 'same-origin',
  });
  if (!res.ok) await parseError(res);
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}
