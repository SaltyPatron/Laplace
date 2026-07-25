import { apiGet, type ApiOptions } from '../../api/client';
import type {
  ChessGameResponse,
  ChessGamesResponse,
  ChessPlayerResponse,
  ChessPlayersResponse,
} from './types';

/**
 * Searching is a lookup, not a scan: the server folds the typed name exactly the
 * way the decomposer did and hashes it, so "Tal, Mikhail" and "mikhail tal" land
 * on the same content-addressed player or on nothing at all.
 */
export function chessPlayers(
  params: { limit?: number; offset?: number; search?: string } = {},
  opts: ApiOptions = {},
) {
  const q = new URLSearchParams();
  if (params.limit != null) q.set('limit', String(params.limit));
  if (params.offset != null) q.set('offset', String(params.offset));
  if (params.search) q.set('search', params.search);
  return apiGet<ChessPlayersResponse>(`/v1/chess/players?${q}`, opts);
}

export function chessPlayer(idHex: string, opponents = 25, opts: ApiOptions = {}) {
  return apiGet<ChessPlayerResponse>(`/v1/chess/players/${idHex}?opponents=${opponents}`, opts);
}

export function chessPlayerGames(
  idHex: string,
  params: { limit?: number; offset?: number } = {},
  opts: ApiOptions = {},
) {
  const q = new URLSearchParams();
  q.set('limit', String(params.limit ?? 25));
  q.set('offset', String(params.offset ?? 0));
  return apiGet<ChessGamesResponse>(`/v1/chess/players/${idHex}/games?${q}`, opts);
}

export function chessGame(idHex: string, opts: ApiOptions = {}) {
  return apiGet<ChessGameResponse>(`/v1/chess/games/${idHex}`, opts);
}
