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

export function startIngest(request: IngestStartRequest, opts: ApiOptions = {}) {
  return apiPost<IngestStartReceipt>('/v1/admin/ingest/start', request, opts);
}
