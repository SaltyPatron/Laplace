import { apiGet, apiPost, type ApiOptions } from '../api/client';

export interface IngestStartRequest {
  source: string;
  path?: string;
  arguments?: string[];
}

export interface IngestProcessReceipt {
  pid: number;
  source: string;
  path: string | null;
  cli: string;
  arguments: string[];
  started_at: string;
}

export interface IngestStartReceipt extends IngestProcessReceipt {
  object: 'ingest.process';
  status: 'started';
  note: string;
}

export interface IngestProcessList {
  object: 'list';
  data: IngestProcessReceipt[];
}

export interface IngestStopReceipt {
  object: 'ingest.process.stop';
  pid: number;
  found: boolean;
  was_running: boolean;
  stop_requested: boolean;
  note: string;
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
