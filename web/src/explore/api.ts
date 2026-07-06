import { apiGet, apiPost, type ApiOptions, type PreflightQuoteResponse } from '../api/client';
import type {
  BillingReceipt,
  ExploreCatalogResponse,
  ExploreEntityDetailResponse,
  ExploreEntityPreviewResponse,
  ExploreResolveResponse,
  ExploreTrainingExportDetailResponse,
  SalientFactRow,
} from './types';

export interface DecomposeResponse {
  text: string;
  root_id_hex: string;
  natural_unit_ordinal: number;
  nodes: { ordinal: number; id_hex: string; label: string; tier: number; text_offset: number; text_length: number }[];
}

export function exploreCatalog(opts: ApiOptions = {}) {
  return apiGet<ExploreCatalogResponse>('/v1/explore/catalog', opts);
}

export function exploreResolve(reference: string, opts: ApiOptions = {}) {
  return apiGet<ExploreResolveResponse>(
    `/v1/explore/resolve?reference=${encodeURIComponent(reference)}`,
    opts,
  );
}

export function explorePreview(idHex: string, opts: ApiOptions = {}) {
  return apiGet<ExploreEntityPreviewResponse>(`/v1/explore/entities/${idHex}/preview`, opts);
}

export function exploreEntity(idHex: string, opts: ApiOptions = {}) {
  return apiGet<ExploreEntityDetailResponse>(`/v1/explore/entities/${idHex}`, opts);
}

export function exploreExport(
  idHex: string,
  body: { consensus_limit?: number; evidence_limit?: number; include_members?: boolean; include_peers?: boolean },
  opts: ApiOptions = {},
) {
  return apiPost<ExploreTrainingExportDetailResponse>(
    `/v1/explore/entities/${idHex}/export`,
    body,
    opts,
  );
}

export function exploreDecompose(text: string, opts: ApiOptions = {}) {
  return apiPost<DecomposeResponse>('/v1/explore/decompose', { text }, opts);
}

export function exploreNeighbors(idHex: string, k = 10, opts: ApiOptions = {}) {
  return apiGet<{
    neighbors: {
      id_hex: string;
      structural: { neighbor: string; geodesic: number; frechet?: number | null; axis: string }[];
      semantic: SalientFactRow[];
    };
    billing?: BillingReceipt | null;
  }>(`/v1/explore/entities/${idHex}/neighbors?k=${k}`, opts);
}

export function explorePeers(idHex: string, limit = 100, opts: ApiOptions = {}) {
  return apiGet<{
    peers: {
      id_hex: string;
      peers: { peer: string; kind: string; strength: number }[];
    };
    billing?: BillingReceipt | null;
  }>(`/v1/explore/entities/${idHex}/peers?limit=${limit}`, opts);
}

export function exploreMembers(idHex: string, limit = 100, opts: ApiOptions = {}) {
  return apiGet<{
    members: {
      id_hex: string;
      members: { member_id_hex: string; member_label: string; kind: string; eff_mu: number; witnesses: number }[];
    };
    billing?: BillingReceipt | null;
  }>(`/v1/explore/entities/${idHex}/members?limit=${limit}`, opts);
}

export function exploreContainers(idHex: string, maxHops = 3, limit = 50, opts: ApiOptions = {}) {
  return apiGet<{
    containers: {
      id_hex: string;
      containers: { entity_id_hex: string; entity_label: string; tier: number; type: string; hops: number }[];
    };
    billing?: BillingReceipt | null;
  }>(`/v1/explore/entities/${idHex}/containers?max_hops=${maxHops}&limit=${limit}`, opts);
}

export async function preflight(serviceId: string, tenant: string, units = 1) {
  return apiPost<PreflightQuoteResponse>(
    '/v1/billing/preflight',
    { service_id: serviceId, units, tenant },
    { tenant },
  );
}
