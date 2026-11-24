export interface BrowseHit {
  id_hex: string;
  label: string;
  tier: number;
  type: string;
  matched_name_id_hex: string;
  match_kind: 'exact' | 'case_variant' | 'member' | 'name' | 'surface' | 'contains_all' | 'constituent';
  rating?: number | null;
  rd?: number | null;
  eff_mu?: number | null;
  witnesses: number;
  flags?: number | string | null;
  general_category?: string | null;
  script?: string | null;
  salience?: number | null;
}

export interface BrowseReceipt {
  query_root_id_hex: string;
  query_member_ids_hex: string[];
  candidate_names: number;
  candidate_capacity: number;
  candidate_truncated: boolean;
  matched_entities: number;
  returned: number;
  offset: number;
  limit: number;
  elapsed_us: number;
}

export interface BrowseResponse {
  object: string;
  query: string;
  hits: BrowseHit[];
  receipt: BrowseReceipt;
}

export interface LeaderboardHit {
  id_hex: string;
  label: string;
  tier: number;
  confidence: number;
  rating: number;
  deviation: number;
  matches: number;
  parts_json: string;
}

export interface LeaderboardResponse {
  object: string;
  offset: number;
  limit: number;
  sort: string;
  tier?: number | null;
  elapsed_us: number;
  hits: LeaderboardHit[];
}
