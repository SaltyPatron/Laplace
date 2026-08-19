import { apiGet, type ApiOptions } from '../../api/client';
import type {
  ChessGamePliesResponse,
  ChessGameResponse,
  ChessGamesResponse,
  ChessPlayerResponse,
  ChessPlayersResponse,
} from './types';

/** Exact names use their content address; surnames and close spellings use the indexed name trajectory. */
export function chessPlayers(
  params: {
    limit?: number;
    offset?: number;
    search?: string;
    initial?: string;
    sort?: 'relevance' | 'strength' | 'games' | 'rating' | 'rd';
    direction?: 'asc' | 'desc';
  } = {},
  opts: ApiOptions = {},
) {
  const q = new URLSearchParams();
  if (params.limit != null) q.set('limit', String(params.limit));
  if (params.offset != null) q.set('offset', String(params.offset));
  if (params.search) q.set('search', params.search);
  if (params.initial) q.set('initial', params.initial);
  if (params.sort) q.set('sort', params.sort);
  if (params.direction) q.set('direction', params.direction);
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

export function chessLaplaceGames(
  params: { limit?: number; offset?: number } = {},
  opts: ApiOptions = {},
) {
  const q = new URLSearchParams();
  q.set('limit', String(params.limit ?? 200));
  q.set('offset', String(params.offset ?? 0));
  return apiGet<ChessGamesResponse>(`/v1/chess/laplace/games?${q}`, opts);
}

export function chessGame(idHex: string, opts: ApiOptions = {}) {
  return apiGet<ChessGameResponse>(`/v1/chess/games/${idHex}`, opts);
}

/** The game replayed into its board sequence, by the same engine that plays live chess. */
export function chessGamePlies(idHex: string, opts: ApiOptions = {}) {
  return apiGet<ChessGamePliesResponse>(`/v1/chess/games/${idHex}/plies`, opts);
}
