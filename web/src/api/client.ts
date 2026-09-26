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
export type EntitlementsResponse = Schemas['EntitlementsResponse'];
export type PreflightQuoteResponse = Schemas['PreflightQuoteResponse'];
export type PlanSubscribeResponse = Schemas['PlanSubscribeResponse'];
export type UsageResponse = Schemas['UsageResponse'];
export type ProvenanceLine = Schemas['ProvenanceLine'];

export interface ApiOptions {
  tenant?: string;
  quoteId?: string;
  session?: string;
  operatorToken?: string;
  /** Stops this transport request, not an independently admitted server job. */
  signal?: AbortSignal;
}

// The shell supplies the server-confirmed workspace, not a browser identity.
// This header asserts request intent so a workspace switch in another tab cannot
// silently apply a stale form to the newly selected company.
let browserWorkspace: string | null = null;
export function setApiWorkspace(tenant: string | null): void { browserWorkspace = tenant; }

export class PaymentRequiredError extends Error {
  constructor(public readonly body: PaymentRequiredResponse) {
    super(body.error?.message ?? 'Payment required');
    this.name = 'PaymentRequiredError';
  }
}

export class ApiError extends Error {
  constructor(public readonly status: number, message: string, public readonly requestId?: string) {
    super(message);
    this.name = 'ApiError';
  }
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

function object(value: unknown): Record<string, unknown> | undefined {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown> : undefined;
}

async function parseError(res: Response): Promise<never> {
  const fallback = `${res.status} ${res.statusText}`.trim();
  let body: unknown;
  try { body = await res.json(); } catch { /* A proxy may return HTML or an empty body. */ }
  const record = object(body);
  const error = object(record?.error);
  const message = [error?.message, record?.detail, record?.message, record?.title]
    .find((value): value is string => typeof value === 'string' && value.length > 0) ?? fallback;
  if (res.status === 402 && error) throw new PaymentRequiredError(body as PaymentRequiredResponse);
  throw new ApiError(res.status, message, res.headers.get('x-request-id') ?? undefined);
}

/** One transport/error/cancellation contract for every product surface. No implicit retries. */
async function request<T>(path: string, init: RequestInit, opts: ApiOptions): Promise<T> {
  const headers = new Headers(laplaceHeaders(opts));
  if (init.headers) new Headers(init.headers).forEach((value, key) => headers.set(key, value));
  const res = await fetch(path, {
    ...init, headers, signal: opts.signal, credentials: 'same-origin',
  });
  if (!res.ok) await parseError(res);
  if (res.status === 204) return undefined as T;
  return await res.json() as T;
}

export async function apiGetArrayBuffer(path: string, opts: ApiOptions = {}): Promise<ArrayBuffer> {
  const res = await fetch(path, { headers: laplaceHeaders(opts), signal: opts.signal, credentials: 'same-origin' });
  if (!res.ok) await parseError(res);
  return await res.arrayBuffer();
}

const inflightGets = new Map<string, Promise<unknown>>();

function getRequestKey(path: string, opts: ApiOptions): string {
  return JSON.stringify([path, browserWorkspace, opts.tenant ?? null, opts.quoteId ?? null,
    opts.session ?? null, opts.operatorToken ?? null]);
}

const completedGets = new Map<string, { value: unknown; expiresAt: number }>();
const MAX_COMPLETED_GETS = 128;

function pruneCompletedGets(now = Date.now()): void {
  for (const [key, entry] of completedGets) if (entry.expiresAt <= now) completedGets.delete(key);
  while (completedGets.size > MAX_COMPLETED_GETS) {
    const oldest = completedGets.keys().next().value as string | undefined;
    if (oldest === undefined) break;
    completedGets.delete(oldest);
  }
}

export async function apiGetCached<T>(
  path: string,
  ttlMs: number,
  opts: ApiOptions = {},
): Promise<T> {
  if (ttlMs <= 0) return apiGet<T>(path, opts);
  const key = getRequestKey(path, opts);
  const now = Date.now();
  const hit = completedGets.get(key);
  if (hit && hit.expiresAt > now) return hit.value as T;
  if (hit) completedGets.delete(key);
  const value = await apiGet<T>(path, opts);
  completedGets.set(key, { value, expiresAt: Date.now() + ttlMs });
  pruneCompletedGets();
  return value;
}

export function invalidateApiGetCache(pathPrefix?: string): void {
  if (!pathPrefix) { completedGets.clear(); return; }
  for (const key of completedGets.keys()) if (key.includes(pathPrefix)) completedGets.delete(key);
}

export function apiGet<T>(path: string, opts: ApiOptions = {}): Promise<T> {
  // A caller-owned AbortSignal has its own cancellation lifetime and therefore
  // cannot safely share transport ownership. Signal-free duplicate reads can.
  if (opts.signal) return request<T>(path, {}, opts);
  const key = getRequestKey(path, opts);
  const existing = inflightGets.get(key);
  if (existing) return existing as Promise<T>;
  const pending = request<T>(path, {}, opts);
  inflightGets.set(key, pending);
  void pending.finally(() => {
    if (inflightGets.get(key) === pending) inflightGets.delete(key);
  });
  return pending;
}

/** Preserve authored text byte-for-byte; the server owns its validation. */
export function apiPutText<T>(path: string, body: string, opts: ApiOptions = {}): Promise<T> {
  return request<T>(path, { method: 'PUT', body }, opts);
}

export function apiPost<T>(path: string, payload: unknown, opts: ApiOptions = {}): Promise<T> {
  return request<T>(path, { method: 'POST', body: JSON.stringify(payload) }, opts);
}

export function apiPostBody<T>(
  path: string,
  body: BodyInit,
  contentType = 'application/octet-stream',
  opts: ApiOptions = {},
): Promise<T> {
  return request<T>(path, { method: 'POST', body, headers: { 'Content-Type': contentType } }, opts);
}

/** Send an already formed JSON request without rounding its numeric literals. */
export function apiPostJson<T>(path: string, json: string, opts: ApiOptions = {}): Promise<T> {
  return request<T>(path, { method: 'POST', body: json }, opts);
}

export function apiPut<T>(path: string, payload: unknown, opts: ApiOptions = {}): Promise<T> {
  return apiPutText<T>(path, JSON.stringify(payload), opts);
}

export function apiDelete<T = void>(path: string, opts: ApiOptions = {}): Promise<T> {
  return request<T>(path, { method: 'DELETE' }, opts);
}

export type FailureKind = 'auth' | 'forbidden' | 'payment' | 'missing' | 'unavailable' | 'server' | 'network' | 'other';

/**
 * What a failed request means to the person reading it. Authority, payment and
 * absence are different answers: a refused read never says "nothing witnessed",
 * and an unreachable host never says "sign in".
 */
export function describeFailure(error: unknown): { kind: FailureKind; message: string } {
  if (error instanceof PaymentRequiredError) return { kind: 'payment', message: error.message };
  if (error instanceof ApiError) {
    if (error.status === 401) return { kind: 'auth', message: 'Sign in, or use an API key, to read this.' };
    if (error.status === 403) return { kind: 'forbidden', message: error.message || 'This operation needs more authority than this session holds.' };
    if (error.status === 404) return { kind: 'missing', message: error.message };
    if (error.status === 503) return { kind: 'unavailable', message: error.message || 'The substrate is unavailable right now.' };
    if (error.status >= 500) return { kind: 'server', message: `The server failed this request: ${error.message}` };
    return { kind: 'other', message: error.message };
  }
  if (error instanceof DOMException && error.name === 'AbortError') return { kind: 'other', message: 'Request cancelled.' };
  if (error instanceof TypeError) return { kind: 'network', message: 'The Laplace API could not be reached.' };
  return { kind: 'other', message: error instanceof Error ? error.message : String(error) };
}
