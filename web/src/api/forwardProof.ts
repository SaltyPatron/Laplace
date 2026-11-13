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

/** One step of a turn: what the forward program emitted and what it added to the response. */
export interface ForwardTurnStep {
  step: number;
  kind: string;
  id: string;
  text: string;
  grounds: number;
  continuity: number;
  confidence: number;
}

/**
 * The receipt of the turn that produced a response: the Engine's own (laplace turn --receipt) with its
 * trace. The session is [participant, session]; a witnessed turn is at its ordinal.
 */
export interface ForwardPassProof {
  session: string;
  participant: string;
  prompt_id: string;
  response_id?: string;
  turn_id?: string;
  ordinal: number;
  turns_before: number;
  witnessed: boolean;
  reason?: string;
  disposition: string;
  steps: ForwardTurnStep[];
  trace: string;
}
