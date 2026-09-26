import { apiGet, apiGetCached, apiPost, type ApiOptions, type Schemas } from '../api/client';
import type { HighwayPopulationStatus, QueryResult, QueryShape, RelationBand } from './types';

export function queryShapes(opts?: ApiOptions) {
  return apiGetCached<{ shapes: QueryShape[] }>('/v1/query/shapes', 5 * 60_000, opts);
}

export function relationBands(opts?: ApiOptions) {
  return apiGetCached<{ bands: RelationBand[] }>('/v1/query/bands', 5 * 60_000, opts);
}

/** The governed relation vocabulary, as the manifest registry holds it. */
export function relationTypes(opts?: ApiOptions) {
  return apiGetCached<Schemas['RelationTypesResponse']>('/v1/query/relations', 30 * 60_000, opts);
}

export function highwayPopulation(opts?: ApiOptions) {
  return apiGet<HighwayPopulationStatus>('/v1/query/highway-status', opts);
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

export function queryHomeLeaders(opts?: ApiOptions) {
  return apiGet<{ bands: import('./types').BandLeaders[] }>('/v1/query/leaders/home', opts);
}

export function queryLeaders(bands: number[], limit: number, opts?: ApiOptions) {
  return apiGet<{ bands: import('./types').BandLeaders[] }>(
    `/v1/query/leaders?bands=${bands.join(',')}&limit=${limit}`, opts);
}

export function entityRecord(idHex: string, opts?: ApiOptions) {
  return apiGet<import('./types').EntityRecord>(`/v1/explore/entities/${idHex}/record`, opts);
}

export function exploreMatchup(x: string, y: string, opts?: ApiOptions) {
  return apiGet<import('./types').Matchup>(
    `/v1/explore/matchup?x=${encodeURIComponent(x)}&y=${encodeURIComponent(y)}`, opts);
}

export function exploreMatchupVerdict(x: string, y: string, opts?: ApiOptions) {
  return apiGet<import('./types').MatchupVerdict>(
    `/v1/explore/matchup/verdict?x=${encodeURIComponent(x)}&y=${encodeURIComponent(y)}`, opts);
}

export interface TaxonomyNode { id: string; label: string; eff_mu?: number | null }
export interface TaxonomyResponse {
  root_id: string; root_label: string;
  up: TaxonomyNode[]; children: TaxonomyNode[];
}
export function entityTaxonomy(idHex: string, opts?: ApiOptions) {
  return apiGet<TaxonomyResponse>(`/v1/explore/entities/${idHex}/taxonomy`, opts);
}
