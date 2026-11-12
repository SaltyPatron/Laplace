import { apiGet, type ApiOptions } from '../../api/client';
import type {
  ChessGameResponse,
  ChessGamesResponse,
  ChessPlayerResponse,
  ChessPlayersResponse,
  ChessTag,
} from './types';

/** The witness whose games are the ones Laplace itself played. */
export const LAPLACE_WITNESS = 'Laplace';

/** The page of one player, addressed by the name as written. */
export const playerPath = (name: string) => `/chess/players/${encodeURIComponent(name)}`;

/** Names matching a name as written, or a word of one, most games first. */
export function chessPlayers(
  params: { search: string; limit?: number; offset?: number },
  opts: ApiOptions = {},
) {
  const q = new URLSearchParams({ search: params.search });
  if (params.limit != null) q.set('limit', String(params.limit));
  if (params.offset != null) q.set('offset', String(params.offset));
  return apiGet<ChessPlayersResponse>(`/modular/api/chess/players?${q}`, opts);
}

/** One player's record, source ratings, opponents and a page of games, newest first. */
export function chessPlayer(
  name: string,
  params: { limit?: number; offset?: number; opponents?: number } = {},
  opts: ApiOptions = {},
) {
  const q = new URLSearchParams({ name });
  if (params.limit != null) q.set('limit', String(params.limit));
  if (params.offset != null) q.set('offset', String(params.offset));
  if (params.opponents != null) q.set('opponents', String(params.opponents));
  return apiGet<ChessPlayerResponse>(`/modular/api/chess/player?${q}`, opts);
}

/** The games Laplace itself played, newest first. */
export function chessLaplaceGames(
  params: { limit?: number; offset?: number } = {},
  opts: ApiOptions = {},
) {
  const q = new URLSearchParams({ witness: LAPLACE_WITNESS });
  q.set('limit', String(params.limit ?? 200));
  q.set('offset', String(params.offset ?? 0));
  return apiGet<ChessGamesResponse>(`/modular/api/chess/games?${q}`, opts);
}

/** One game: its tags and its replayed plies. */
export function chessGame(id: string, opts: ApiOptions = {}) {
  return apiGet<ChessGameResponse>(`/modular/api/chess/game?id=${encodeURIComponent(id)}`, opts);
}

/** The value of one PGN tag, or null when the source did not write it. */
export function tagValue(tags: ChessTag[], name: string): string | null {
  return tags.find((tag) => tag.name === name)?.value ?? null;
}

/** A PGN clock reading ("H:MM:SS(.f)" or "M:SS") in seconds; null when it is not one. */
export function clockSeconds(clock: string | null | undefined): number | null {
  if (!clock) return null;
  const parts = clock.trim().split(':');
  if (parts.length < 2 || parts.length > 3 || parts.some((part) => !/^\d+(\.\d+)?$/.test(part))) return null;
  return parts.reduce((total, part) => total * 60 + Number(part), 0);
}
