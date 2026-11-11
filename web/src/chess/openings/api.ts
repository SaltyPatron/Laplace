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
