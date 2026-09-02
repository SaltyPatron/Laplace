export interface BrowseHit {
  id_hex: string;
  label: string;
  tier: number;
  type: string;
  matched_name_id_hex: string;
  match_kind: 'name' | 'surface' | string;
  rating?: number | null;
  rd?: number | null;
  eff_mu?: number | null;
  witnesses: number;
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
