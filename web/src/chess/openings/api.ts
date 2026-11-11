import { apiGet, apiPost } from '../../api/client';

/**
 * The modular substrate's opening reads. An opening is a recorded line [P0, M0, P1, …]
 * sharing its positions with every game that reaches them; its name and ECO code are
 * claims attested on that line. The UI sends a FEN or movetext and renders what comes
 * back: position ids come from the Engine and are never computed here.
 */
export interface OpeningLine {
  line: string;
  name: string;
  eco: string;
  plies: number;
  standing?: { rating: number; deviation: number };
}

/** GET /modular/api/chess/position: openings ordered by plies ascending, the most specific last. */
export interface PositionOpeningsResponse {
  position: string;
  openings: OpeningLine[];
  /** Recorded game lines containing this position. */
  games: number;
}

export interface GameOpeningsPly {
  ply: number;
  position: string;
  openings: OpeningLine[];
}

/** POST /modular/api/chess/game-openings: one read per game, every ply's position at once. */
export interface GameOpeningsResponse {
  line: string;
  plies: GameOpeningsPly[];
  deepest: OpeningLine | null;
  /** The first ply whose position no named opening contains; null when the game never leaves book. */
  out_of_book_at: number | null;
}

export const positionOpenings = (fen: string, signal?: AbortSignal) =>
  apiGet<PositionOpeningsResponse>(`/modular/api/chess/position?fen=${encodeURIComponent(fen)}`, { signal });

export const gameOpenings = (pgn: string, signal?: AbortSignal) =>
  apiPost<GameOpeningsResponse>('/modular/api/chess/game-openings', { pgn }, { signal });

/** The most specific opening: the longest line that still contains the position. */
export const mostSpecific = (openings: OpeningLine[]) => openings.at(-1) ?? null;

/** One batched read for a page of games (at most 512): an array aligned with pgns, each
 * { deepest } (null for a game with an illegal move or no named position). */
export interface GameDeepestOpening { deepest: OpeningLine | null }
export const gameOpeningsBatch = (pgns: string[], signal?: AbortSignal) =>
  apiPost<GameDeepestOpening[]>('/modular/api/chess/game-openings', { pgns }, { signal });

/** Syzygy win/draw/loss as the endpoint names it (win, draw, loss, and the 50-move-rule cases). */
export type Wdl = string;

/**
 * GET /modular/api/chess/tablebase: Syzygy verdicts recorded as claims on the same position
 * ids. `wdl`/`dtz` are from the side to move's view; each move's are from the opponent's view
 * after it, best move first. Positions without a recorded table give null verdicts.
 */
export interface TablebaseResponse {
  position: string;
  wdl: Wdl | null;
  dtz: number | null;
  moves: { san: string; position: string; wdl: Wdl | null; dtz: number | null }[];
}

export const positionTablebase = (fen: string, signal?: AbortSignal) =>
  apiGet<TablebaseResponse>(`/modular/api/chess/tablebase?fen=${encodeURIComponent(fen)}`, { signal });

/** Syzygy tables cover at most five pieces here; more is never asked. */
export const TABLEBASE_PIECES = 5;
export const pieceCount = (fen: string) => (fen.split(' ')[0].match(/[prnbqkPRNBQK]/g) ?? []).length;
