/**
 * A competition of any format is one DAG of matches. A slot is filled either by an entrant directly
 * or by a finishing place in an earlier match, so single and double elimination, stepladders,
 * round robins, sweepers and season schedules are the same structure in different shapes.
 *
 * Everything here is given, never derived in the browser: who advanced (`entrant` on a slot that
 * has a `from`), who finished where (`place`), and every standings row arrive already decided.
 */
export interface Entrant {
  id: string;
  name: string;
  seed?: number;
}

/** A finishing place in an earlier match: place 1 is its winner. */
export interface SlotSource {
  match: string;
  place: number;
}

export interface Slot {
  /** The entrant in this slot, once known. */
  entrant?: string;
  /** Where the entrant comes from when the slot is not a direct entry. */
  from?: SlotSource;
  /** The score as the sport writes it: 247, 6–4, 2:1. */
  score?: string;
  /** Finishing place in this match, once it is final. */
  place?: number;
}

export type MatchState = 'scheduled' | 'live' | 'final';

export interface Match {
  id: string;
  stage: string;
  /** Column of the stage this match is played in; a slot's source is always in an earlier round. */
  round: number;
  name?: string;
  /** ISO 8601 start time. */
  when?: string;
  state: MatchState;
  slots: Slot[];
}

export interface Stage {
  id: string;
  title: string;
  /** Titles of the stage's rounds, in round order. */
  rounds?: string[];
}

export interface Standings {
  stage: string;
  columns: string[];
  /** Rows in standing order. */
  rows: { entrant: string; cells: string[] }[];
}

export interface Competition {
  title: string;
  entrants: Entrant[];
  stages: Stage[];
  matches: Match[];
  standings?: Standings[];
}
