export interface ForwardTraceStep {
  step: number;
  entity_id_hex: string;
  entity: string;
  stride_used: number;
  root_id_hex: string;
  candidate_count: number;
  ordered_context_count: number;
  proposal_channel_count: number;
  exact_channel_count: number;
  sequence_occurrences: number;
  covered_occurrences: number;
  relation_families: number;
  opposed_occurrences: number;
  support_anchor_id_hex?: string;
  support_anchor?: string;
  support_relation_id_hex?: string;
  support_relation?: string;
  support_outbound?: boolean;
  support_rating?: number;
  support_rd?: number;
  support_witnesses?: number;
  support_sources: number;
  support_contexts: number;
  declared_result: boolean;
  event: string;
  routing_round: number;
}

export interface ForwardPassProof {
  session: string;
  prompt_occurrence_key: string;
  response_witnessed: boolean;
  program_id?: string;
  semantic_act_id?: string;
  output_fingerprint?: string;
  completion: boolean;
  disposition: string;
  required_obligations: number;
  satisfied_obligations: number;
  remaining_required: number;
  output_count: number;
  prior_discourse_ids: string[];
  events: ForwardTraceStep[];
}
