/**
 * Chess reads served by the modular host under /modular/api/chess. A player is a name
 * exactly as a PGN White/Black tag writes it; a game is its record's 32-hex id.
 */

/**
 * A record over recorded games. `unscored` is games whose source never asserted
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

/** One name of the player search. `games_complete: false` means the count hit a cap. */
export interface ChessPlayerRow {
  name: string;
  games: number;
  games_complete: boolean;
}

export interface ChessPlayersResponse {
  search: string;
  offset: number;
  total: number;
  players: ChessPlayerRow[];
}

/** A source Elo tag (WhiteElo/BlackElo) the player carried, and the games that carried it. */
export interface ChessRatingRow {
  rating: number;
  games: number;
}

/** A head-to-head line. Wins and losses are the page's player's. */
export interface ChessOpponentRow extends ChessRecord {
  name: string;
}

/** A game as a list row. `date` is "YYYY.MM.DD" as the source wrote it. */
export interface ChessGameRow {
  id: string;
  line: string | null;
  white: string | null;
  black: string | null;
  result: string | null;
  date: string | null;
  event: string | null;
  eco: string | null;
  /** The game's movetext, which the opening read takes; null when the game carries none. */
  movetext: string | null;
}

/** A game row of the player read. `outcome` is the page's player's: 2 win, 1 draw, 0 loss, null unscored. */
export interface ChessPlayerGameRow extends ChessGameRow {
  as_white: boolean;
  opponent: string | null;
  outcome: number | null;
}

export interface ChessPlayerResponse {
  name: string;
  overall: ChessRecord;
  as_white: ChessRecord;
  as_black: ChessRecord;
  peak_rating: number | null;
  ratings: ChessRatingRow[];
  opponents_total: number;
  opponents: ChessOpponentRow[];
  /** All the player's games; `games` is one page of them, newest first. */
  total: number;
  offset: number;
  games: ChessPlayerGameRow[];
}

/** The games one witness recorded, newest first. */
export interface ChessGamesResponse {
  witness: string;
  total: number;
  offset: number;
  games: ChessGameRow[];
}

/** One PGN tag as written. */
export interface ChessTag {
  name: string;
  value: string | null;
}

/**
 * One ply of a replayed game. `position` is the id of the board after the move, shared
 * with every other game that reached the same board. `clock` is the mover's remaining
 * clock exactly as the PGN comment wrote it.
 */
export interface ChessPlyRow {
  ply: number;
  san: string;
  uci: string;
  fen: string;
  white_moved: boolean;
  position: string;
  clock: string | null;
}

/** One game: its tags in the source's order and its replayed plies. */
export interface ChessGameResponse extends ChessGameRow {
  tags: ChessTag[];
  start_fen: string | null;
  /** False when the game did not replay; plies may then be partial or empty. */
  complete: boolean;
  plies: ChessPlyRow[];
}
