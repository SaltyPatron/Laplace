import { apiPost, type ApiOptions } from '../api/client';

/** One row of `ops.api()` — the installed-operation allow-list. */
export interface OpSignature {
  name: string;
  args: string;
  returns: string;
}

/** A row of `laplace.ingest_run_journal`, as returned by `ops.ingest_runs`. */
export interface IngestRun {
  run_id: string;
  source_name: string;
  source_id: string | null;
  layer: number | null;
  status: string;
  started_at: string | null;
  ended_at: string | null;
  units_attempted: number | null;
  units_applied: number | null;
  units_failed: number | null;
  entities: number | null;
  physicalities: number | null;
  attestations: number | null;
  files_done: number | null;
  files_total: number | null;
  input_units_done: number | null;
  input_units_total: number | null;
  evidence_persisted: boolean | null;
  error: string | null;
}

export interface OpResult<T> {
  object: string;
  name: string;
  rows: T[];
  truncated_at?: number | null;
}

/**
 * Call an installed substrate operation by name.
 *
 * `POST /v1/op` resolves the name against `ops.api()` and refuses anything
 * outside it, so this is a named call and never SQL text. Note the endpoint
 * binds a read-only data source (SubstrateClient.InvokeOpAsync), so operations
 * that write — `ops.ingest_run_close` among them — are in the catalog but not
 * invokable here.
 */
export function callOp<T = Record<string, unknown>>(
  name: string,
  args?: Record<string, unknown>,
  maxRows = 200,
  timeoutSeconds?: number,
  opts: ApiOptions = {},
) {
  return apiPost<OpResult<T>>(
    '/v1/op',
    {
      name,
      ...(args && Object.keys(args).length ? { args } : {}),
      max_rows: maxRows,
      ...(timeoutSeconds ? { timeout_seconds: timeoutSeconds } : {}),
    },
    opts,
  );
}

/** The installed-operation catalog, optionally filtered by substring. */
export function listOps(like?: string, opts: ApiOptions = {}) {
  return callOp<OpSignature>('ops.api', like ? { p_like: like } : undefined, 2000, undefined, opts);
}

/** The ingest journal — the record CI/CD pipelines gate on. */
export function ingestRuns(limit = 25, opts: ApiOptions = {}) {
  return callOp<IngestRun>('ops.ingest_runs', { p_limit: limit }, limit, undefined, opts);
}

/**
 * Force a run closed, unblocking any pipeline gating on it.
 *
 * `ops.ingest_run_close` is on the endpoint's write allow-list
 * (InstalledOpInvoker.WritableOps), so unlike every other catalog mutation it
 * resolves onto a writable connection instead of failing read-only.
 */
export function closeIngestRun(runId: string, status = 'cancelled', opts: ApiOptions = {}) {
  return callOp<Record<string, unknown>>(
    'ops.ingest_run_close',
    { p_run_id: runId, p_status: status },
    1,
    undefined,
    opts,
  );
}
