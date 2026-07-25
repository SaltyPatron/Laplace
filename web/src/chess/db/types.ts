/**
 * The chess read surface, as the API serves it. Ids are content hashes, so every
 * id here is also a substrate entity id — the same one /explore/entity/:id will
 * explain down to its witnesses.
 */

/**
 * A record over witnessed games. `unscored` is games whose source never asserted
 * a result: abstentions, reported rather than folded into the score. `score` is
 * the chess convention (wins + draws/2) over the games that were scored.
 */
export interface ChessRecord {
  games: number;
  wins: number;
  draws: number;
  losses: number;
  unscored: number;
  score: number | null;
}

export interface ChessPlayerRow {
  rank: number;
  id: string;
  name: string;
  record: ChessRecord;
}

export interface ChessPlayersResponse {
  object: string;
  total: number;
  offset: number;
  players: ChessPlayerRow[];
}

export interface ChessRatingRow {
  rating: number;
  games: number;
}

export interface ChessOpponentRow {
  id: string;
  name: string;
  record: ChessRecord;
}

export interface ChessPlayerResponse {
  object: string;
  id: string;
  name: string;
  overall: ChessRecord;
  as_white: ChessRecord;
  as_black: ChessRecord;
  peak_rating: number | null;
  ratings: ChessRatingRow[];
  opponents: ChessOpponentRow[];
}

/** `outcome` is the substrate's own enum — 2 win, 1 draw, 0 loss, null unscored. */
export interface ChessGameRow {
  id: string;
  played_on: string | null;
  event: string | null;
  eco: string | null;
  as_white: boolean;
  opponent_id: string | null;
  opponent: string;
  result: string | null;
  outcome: number | null;
}

export interface ChessGamesResponse {
  object: string;
  player_id: string;
  offset: number;
  games: ChessGameRow[];
}

export interface ChessGameResponse {
  object: string;
  id: string;
  white_id: string | null;
  white: string;
  black_id: string | null;
  black: string;
  result: string | null;
  played_on: string | null;
  event: string | null;
  eco: string | null;
  termination: string | null;
  time_control: string | null;
  tc_class: string | null;
  movetext: string | null;
}
