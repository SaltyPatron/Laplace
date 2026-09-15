export interface ApiErrorBody {
  error?: { message?: string; type?: string; code?: string };
}

export interface HealthResponse {
  status: string;
  substrate?: string;
}

export interface SalientFact {
  type: string;
  fact: string;
  eff_mu: number;
  witnesses: number;
}

export interface SearchEntity {
  id: string;
  label: string;
  type?: string | null;
  score?: number | null;
}

export interface SearchResponse {
  object?: string;
  data?: SearchEntity[];
  results?: SearchEntity[];
}

export interface ExploreConsensusRow {
  direction: 'out' | 'in' | string;
  type: string;
  entity_id_hex: string;
  entity_label: string;
  eff_mu: number;
  witnesses: number;
}

export interface ExploreSenseRow {
  sense_id_hex: string;
  synset_id_hex: string;
  synset_label: string;
  eff_mu: number;
  witnesses: number;
}

export interface ExploreEntityResponse {
  id: string;
  label: string;
  type?: string | null;
  consensus_out: ExploreConsensusRow[];
  consensus_in: ExploreConsensusRow[];
  salient_facts: SalientFact[];
  senses: ExploreSenseRow[];
}

export interface EntityRecord {
  id: string;
  confirmed: number;
  contested: number;
  refuted: number;
  thin: number;
}

export interface TapeRow {
  holder: 'both' | 'x-only' | 'y-only' | string;
  type: string;
  fact: string;
  eff_mu?: number | null;
}

export interface ChessMatchupSide {
  peak_source_elo: number | null;
  games: number;
  wins: number;
  draws: number;
  losses: number;
  unscored: number;
  score: number | null;
}

export interface MatchupSide {
  id: string;
  label: string;
  record: EntityRecord;
  top_facts: { type: string; fact: string; eff_mu: number; witnesses: number }[];
  entity_type?: string | null;
  source_rating_peak?: number | null;
  source_rating_observations?: number;
  chess?: ChessMatchupSide | null;
}

export interface Matchup {
  x: MatchupSide;
  y: MatchupSide;
  tape: TapeRow[];
}

export interface MatchupVerdict {
  relation?: string | null;
  plane?: string | null;
  eff_mu?: number | null;
  usage?: number | null;
  geodesic?: number | null;
  verdict?: string | null;
}

export interface BandLeaderRow {
  subject_id: string;
  subject: string;
  relation: string;
  object_id: string;
  object: string;
  eff_mu: number;
  witnesses: number;
}

export interface BandLeaders {
  band: number;
  name: string;
  rows: BandLeaderRow[];
}

export interface LeadersResponse {
  bands: BandLeaders[];
}
