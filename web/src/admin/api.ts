import { apiGet, apiPost, apiPutText, type ApiOptions } from '../api/client';

/** One row of `ops.api()` — the installed-operation allow-list. */
export interface OpSignature {
  name: string;
  args: string;
  returns: string;
  /**
   * `procedure` is CALLed and returns no rows; `function` is SELECTed. The
   * endpoint switches on this, and so must anything that renders a result.
   */
  kind?: string;
}

/**
 * Which operations may write, and which destroy testimony — from the server's
 * own lists (`InstalledOpInvoker.WritableOps` / `DestructiveOps`), never guessed
 * from the operation's name.
 */
export interface OpPolicy {
  object: string;
  writable: string[];
  destructive: string[];
}

export function opPolicy(opts: ApiOptions = {}) {
  return apiGet<OpPolicy>('/v1/admin/ops/policy', opts);
}

/** A row of `laplace.ingest_run_journal`, as returned by `ops.ingest_runs`. */
export interface IngestRun {
  run_id: string;
  source_name: string;
  source_id: string | null;
  layer: number | null;
  status: string;
  phase: string | null;
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
  fold_drain_ms: number | null;
  writer_maintenance_ms: number | null;
  evidence_persisted: boolean | null;
  error: string | null;
}

/** One independently scheduled file inside an ingest run. */
export interface IngestFile {
  run_id: string;
  file_label: string;
  source_name: string;
  file_id: string | null;
  status: string;
  started_at: string | null;
  ended_at: string | null;
  bytes: number | null;
  records: number | null;
  entities: number | null;
  physicalities: number | null;
  attestations: number | null;
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
 * outside it, so this is a named call and never SQL text. The endpoint binds a
 * read-only data source (SubstrateClient.InvokeOpAsync) for every op EXCEPT
 * those on the write allow-list (InstalledOpInvoker.WritableOps), which get a
 * writable connection — see `closeIngestRun` below for the one op currently on
 * that list. Every other catalog write fails read-only.
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

/** Per-file work for one run, with active and failed files sorted first. */
export function ingestFiles(runId: string, limit = 200, opts: ApiOptions = {}) {
  return callOp<IngestFile>(
    'ops.ingest_files',
    { p_run_id: runId, p_limit: limit },
    limit,
    undefined,
    opts,
  );
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

// ---------------------------------------------------------------------------
// Activity and cancellation
// ---------------------------------------------------------------------------

/** One backend on this database, from `ops.activity()`. */
export interface ActivityRow {
  pid: number;
  is_self: boolean;
  /**
   * Postgres masked state/query because the role lacks pg_read_all_stats. A
   * different fact from an idle session, which still reports state='idle'.
   */
  restricted: boolean;
  usename: string | null;
  application_name: string | null;
  client_addr: string | null;
  backend_type: string | null;
  state: string | null;
  wait_event_type: string | null;
  wait_event: string | null;
  query_seconds: number | null;
  xact_seconds: number | null;
  backend_seconds: number | null;
  query: string | null;
}

export interface SignalResult {
  pid: number;
  signalled: boolean;
  was_state: string | null;
  was_query: string | null;
  was_seconds: number | null;
}

export function activity(minSeconds = 0, includeIdle = true, opts: ApiOptions = {}) {
  return callOp<ActivityRow>(
    'ops.activity',
    { p_min_seconds: minSeconds, p_include_idle: includeIdle },
    500,
    undefined,
    opts,
  );
}

/** End the running statement; the session survives and rolls back cleanly. */
export function cancelBackend(pid: number, opts: ApiOptions = {}) {
  return callOp<SignalResult>('ops.cancel_backend', { p_pid: pid }, 1, undefined, opts);
}

/** Drop the connection — the escalation for a backend that ignored a cancel. */
export function terminateBackend(pid: number, opts: ApiOptions = {}) {
  return callOp<SignalResult>('ops.terminate_backend', { p_pid: pid }, 1, undefined, opts);
}

// ---------------------------------------------------------------------------
// Repair
// ---------------------------------------------------------------------------

export interface IndexHealthRow {
  index_name: string;
  table_name: string;
  schema_name: string;
  valid: boolean;
  is_partitioned_parent: boolean;
  leaf_count: number;
}

/** Empty set = healthy. leaf_count 0 on a partitioned parent is the shell class. */
export function indexHealth(opts: ApiOptions = {}) {
  return callOp<IndexHealthRow>('ops.index_health', undefined, 500, undefined, opts);
}

/**
 * Rebuild every invalid index, one COMMIT each. Long — the caller passes a
 * generous timeout and cancels from the Activity tab if it must stop.
 */
export function reindexInvalid(dryRun: boolean, timeoutSeconds = 3600, opts: ApiOptions = {}) {
  return callOp<Record<string, unknown>>(
    'ops.reindex_invalid', { p_dry_run: dryRun }, 1, timeoutSeconds, opts);
}

export function analyzeSubstrate(timeoutSeconds = 3600, opts: ApiOptions = {}) {
  return callOp<Record<string, unknown>>(
    'ops.analyze_substrate', undefined, 1, timeoutSeconds, opts);
}

export interface VacuumResult {
  object: string;
  statement: string;
  elapsed_ms: number;
}

/**
 * VACUUM is not an installed operation and cannot be: Postgres refuses it inside
 * a transaction block, and a procedure body is always in one. The endpoint issues
 * it on its own autocommit connection.
 */
export function vacuum(
  body: { table?: string; full?: boolean; analyze?: boolean; timeout_seconds?: number },
  opts: ApiOptions = {},
) {
  return apiPost<VacuumResult>('/v1/admin/maintenance/vacuum', body, opts);
}

// ---------------------------------------------------------------------------
// Data retraction
// ---------------------------------------------------------------------------

export interface SourceRosterRow {
  source_name?: string;
  name?: string;
  source_id?: string;
  [k: string]: unknown;
}

export function sourceStatus(opts: ApiOptions = {}) {
  return callOp<SourceRosterRow>('ops.source_status', undefined, 500, undefined, opts);
}

/**
 * Lawful retraction of one source's testimony: delete its evidence, refold every
 * touched cell from what survives, cull cells left with zero witnesses, and drain
 * the mask-repair queue. IRREVERSIBLE — re-deriving it means re-running the lane.
 */
export function evictSource(
  sourceIdHex: string,
  timeoutSeconds = 21600,
  opts: ApiOptions = {},
) {
  return callOp<Record<string, unknown>>(
    'ops.evict_source', { p_source: sourceIdHex }, 1, timeoutSeconds, opts);
}

// ---------------------------------------------------------------------------
// External agent routing
// ---------------------------------------------------------------------------

export interface AgentRoute {
  name: string;
  provider: string;
  model: string | null;
  base_url: string;
  /** The VARIABLE NAME a key is read from — never a key value. */
  key_env: string;
  credential_source: string;
  auth: string;
  credentialed: boolean;
  alias: boolean;
  default: boolean;
}

export interface AgentRoutes {
  object: string;
  config: string | null;
  searched: string[];
  default: string | null;
  secret_file: string;
  rows: AgentRoute[];
}

export function agentRoutes(opts: ApiOptions = {}) {
  return apiGet<AgentRoutes>('/v1/admin/agents', opts);
}

export interface AgentConfigFile {
  object: string;
  path: string | null;
  exists: boolean;
  write_path: string | null;
  content: string | null;
}

export function agentConfig(opts: ApiOptions = {}) {
  return apiGet<AgentConfigFile>('/v1/admin/agents/config', opts);
}

export interface AgentConfigSaved {
  path: string;
  written: boolean;
  routes: number;
}

/**
 * Parsed server-side BEFORE it is written, by the same parser every `ask` uses —
 * an invalid document is refused rather than saved and discovered later, when
 * nobody connects the broken lane to the edit that broke it.
 */
export function saveAgentConfig(content: string, opts: ApiOptions = {}) {
  return apiPutText<AgentConfigSaved>('/v1/admin/agents/config', content, opts);
}

export interface AgentReply {
  object: string;
  agent: string;
  provider: string;
  model: string;
  reply: string;
  finish_reason: string | null;
  input_tokens: number | null;
  output_tokens: number | null;
  attempts: number;
  provider_ms: number;
  note: string | null;
}

/** Prove a route end to end with the same client the MCP `ask` tool uses. */
export function askAgent(
  body: { prompt: string; model?: string; provider?: string; system?: string; timeout_seconds?: number },
  opts: ApiOptions = {},
) {
  return apiPost<AgentReply>('/v1/admin/agents/ask', body, opts);
}
