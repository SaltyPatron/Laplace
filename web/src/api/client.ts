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
  /** Stops this transport request, not an independently admitted server job. */
  signal?: AbortSignal;
}

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
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (opts.tenant) headers['X-Laplace-Tenant'] = opts.tenant;
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
  const res = await fetch(path, { ...init, headers: laplaceHeaders(opts), signal: opts.signal });
  if (!res.ok) await parseError(res);
  if (res.status === 204) return undefined as T;
  return await res.json() as T;
}

export function apiGet<T>(path: string, opts: ApiOptions = {}): Promise<T> {
  return request<T>(path, {}, opts);
}

/** Preserve authored text byte-for-byte; the server owns its validation. */
export function apiPutText<T>(path: string, body: string, opts: ApiOptions = {}): Promise<T> {
  return request<T>(path, { method: 'PUT', body }, opts);
}

export function apiPost<T>(path: string, payload: unknown, opts: ApiOptions = {}): Promise<T> {
  return request<T>(path, { method: 'POST', body: JSON.stringify(payload) }, opts);
}

/** Send an already formed JSON request without rounding its numeric literals. */
export function apiPostJson<T>(path: string, json: string, opts: ApiOptions = {}): Promise<T> {
  return request<T>(path, { method: 'POST', body: json }, opts);
}
