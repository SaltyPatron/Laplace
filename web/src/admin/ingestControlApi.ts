import { apiPost, type ApiOptions } from '../api/client';

export interface IngestStartRequest {
  source: string;
  path?: string;
  arguments?: string[];
}

export interface IngestStartReceipt {
  object: 'ingest.process';
  pid: number;
  source: string;
  path: string | null;
  cli: string;
  arguments: string[];
  status: 'started';
  note: string;
}

export interface IngestStopReceipt {
  object: 'ingest.process.stop';
  pid: number;
  found: boolean;
  was_running: boolean;
  stop_requested: boolean;
  note: string;
}

export function startIngest(request: IngestStartRequest, opts: ApiOptions = {}) {
  return apiPost<IngestStartReceipt>('/v1/admin/ingest/start', request, opts);
}

export function stopIngest(pid: number, opts: ApiOptions = {}) {
  return apiPost<IngestStopReceipt>('/v1/admin/ingest/stop', { pid }, opts);
}
