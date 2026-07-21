import { apiGet, apiPost, type ApiOptions } from '../api/client';
import type { QueryResult, QueryShape, RelationBand } from './types';

export function queryShapes(opts?: ApiOptions) {
  return apiGet<{ shapes: QueryShape[] }>('/v1/query/shapes', opts);
}

export function relationBands(opts?: ApiOptions) {
  return apiGet<{ bands: RelationBand[] }>('/v1/query/bands', opts);
}

export interface QueryBody {
  topic: string;
  topic2?: string;
  shape: string;
  bands?: number[];
  relation_type?: string;
  lang?: string;
  depth?: number;
  breadth?: number;
  limit?: number;
  steps?: number;
  spread?: number;
  max_stride?: number;
  seed?: number;
  directed?: boolean;
  use_geometry?: boolean;
}

export function runQuery(body: QueryBody, opts?: ApiOptions) {
  return apiPost<QueryResult>('/v1/query', body, opts);
}
