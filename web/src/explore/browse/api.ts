import { apiGet, type ApiOptions } from '../../api/client';
import type { BrowseResponse } from './types';

export function browseSubstrate({
  query,
  offset = 0,
  limit = 50,
  capacity = 2048,
}: {
  query: string;
  offset?: number;
  limit?: number;
  capacity?: number;
}, options?: ApiOptions) {
  const params = new URLSearchParams({
    q: query,
    offset: String(offset),
    limit: String(limit),
    capacity: String(capacity),
  });
  return apiGet<BrowseResponse>(`/v1/explore/browse?${params.toString()}`, options);
}
