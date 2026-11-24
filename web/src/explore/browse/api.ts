import { apiGet, type ApiOptions } from '../../api/client';
import type { BrowseResponse, LeaderboardResponse } from './types';

export function browseSubstrate({
  query,
  offset = 0,
  limit = 50,
  capacity = 2048,
  mode,
}: {
  query: string;
  offset?: number;
  limit?: number;
  capacity?: number;
  mode?: 'discover' | 'direct';
}, options?: ApiOptions) {
  const params = new URLSearchParams({
    q: query,
    offset: String(offset),
    limit: String(limit),
    capacity: String(capacity),
  });
  if (mode) params.set('mode', mode);
  return apiGet<BrowseResponse>(`/v1/explore/browse?${params.toString()}`, options);
}

export function exploreLeaderboard({
  offset = 0,
  limit = 25,
  sort = 'standing',
  tier,
}: {
  offset?: number;
  limit?: number;
  sort?: 'standing' | 'rating' | 'matches';
  tier?: number | null;
} = {}, options?: ApiOptions) {
  const params = new URLSearchParams({
    offset: String(offset),
    limit: String(limit),
    sort,
  });
  if (tier != null) params.set('tier', String(tier));
  return apiGet<LeaderboardResponse>(`/v1/explore/leaderboard?${params.toString()}`, options);
}
