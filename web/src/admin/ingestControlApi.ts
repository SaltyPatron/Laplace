import { apiGet, apiPost, type ApiOptions } from '../api/client';

export interface IngestStartRequest {
  source: string;
  path?: string;
  arguments?: string[];
}

export interface IngestProcessReceipt {
  pid: number;
  process_run_id?: string;
  source: string;
  path: string | null;
  cli: string;
  arguments: string[];
  started_at: string;
  stdout?: string;
  stderr?: string;
}

export interface IngestCompletedReceipt {
  pid: number;
  process_run_id?: string;
  source: string;
  path?: string | null;
  cli?: string;
  arguments?: string[];
  started_at: string;
  ended_at: string;
  status: string;
  exit_code: number | null;
  output_incomplete: boolean;
  stdout?: string;
  stderr?: string;
}

export interface IngestSourceEntry {
  kind: 'recipe_source' | 'direct_lane';
  name: string;
  witness: string | null;
  witness_id: string | null;
  staged_on_disk: boolean;
  stage_status: string;
  matched_path: string | null;
  trust_class: string | null;
  after: string[];
  recipes_count: number;
  admitted_attestations: number | null;
}

export interface IngestSourcesList {
  sources: string[];
  entries?: IngestSourceEntry[];
  data_dir?: string | null;
  recipes_dir?: string | null;
  scope: string;
  host: string;
  cliAvailable: boolean;
}

export interface IngestPlanResult {
  object: string;
  source: string;
  ok: boolean;
  exit_code: number | null;
  timed_out: boolean;
  elapsed_ms: number;
  cli: string;
  arguments: string[];
  stdout: string;
  stderr: string;
  output_incomplete: boolean;
}

export interface IngestStartReceipt extends IngestProcessReceipt {
  object: 'ingest.process';
  status: 'started';
  note: string;
}

export interface IngestProcessList {
  object: 'list';
  data: IngestProcessReceipt[];
  completed?: IngestCompletedReceipt[];
}

export interface IngestStopReceipt {
  object: 'ingest.process.stop';
  pid: number;
  found: boolean;
  was_running: boolean;
  stop_requested: boolean;
  note: string;
}

export function listIngestSources(opts: ApiOptions = {}) {
  return apiGet<IngestSourcesList>('/v1/admin/ingest/sources', opts);
}

export function planIngestSource(source: string, opts: ApiOptions = {}) {
  return apiPost<IngestPlanResult>('/v1/admin/ingest/plan', { source }, opts);
}

export function listIngestProcesses(opts: ApiOptions = {}) {
  return apiGet<IngestProcessList>('/v1/admin/ingest/processes', opts);
}

export function startIngest(request: IngestStartRequest, opts: ApiOptions = {}) {
  return apiPost<IngestStartReceipt>('/v1/admin/ingest/start', request, opts);
}

export function stopIngest(pid: number, opts: ApiOptions = {}) {
  return apiPost<IngestStopReceipt>('/v1/admin/ingest/stop', { pid }, opts);
}
