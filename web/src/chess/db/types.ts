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

/**
 * A player as the rated competitor he is. `games` is the fold's own witness count;
 * `eff_mu` (rating − 2·rd) is the conservative estimate everything ranks by. Not a win
 * percentage: Glicko-2 weighs who you beat, and rd says how sure the corpus is.
 */
export interface ChessPlayerRow {
  rank: number;
  id: string;
  name: string;
  games: number;
  rating: number;
  rd: number;
  eff_mu: number;
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

/** A head-to-head line read from the folded pairing cell: `games` is its witness count. */
export interface ChessOpponentRow {
  id: string;
  name: string;
  games: number;
  rating: number;
  rd: number;
  eff_mu: number;
}

export interface ChessIdentityProfile {
  id: string;
  provider: string;
  provider_id: string;
  display_name: string;
  aliases: string[];
  title: string | null;
  federation: string | null;
  biography: string | null;
  avatar_url: string | null;
  links: string[];
  ratings: Record<string, number>;
  facts: Record<string, string>;
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
  profiles: ChessIdentityProfile[];
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

/**
 * One ply of a replayed game. `position_id` is the content address of the board AFTER the
 * move — a real Chess_Position entity shared with every other game that reached the same
 * board — so any ply opens into the rated web around it. `clock_seconds` is what the
 * source recorded, present only when it recorded a reading for every ply.
 */
export interface ChessPlyRow {
  ply: number;
  san: string;
  uci: string;
  fen: string;
  white_moved: boolean;
  clock_seconds: number | null;
  position_id: string;
}

export interface ChessGamePliesResponse {
  object: string;
  game_id: string;
  start_fen: string;
  has_clocks: boolean;
  /** Non-null when a token would not resolve: the walk stops rather than inventing boards. */
  truncated: string | null;
  plies: ChessPlyRow[];
}
