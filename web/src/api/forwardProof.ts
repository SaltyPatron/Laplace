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

export type FirmwareProfile =
  | 'program'
  | 'coding'
  | 'deductive'
  | 'inductive'
  | 'abductive'
  | 'constructive'
  | 'adversarial';

export const FIRMWARE_PROFILES: {
  id: FirmwareProfile;
  label: string;
  summary: string;
}[] = [
  { id: 'program', label: 'program (default)', summary: 'Balanced general OODA forward pass' },
  { id: 'coding', label: 'coding', summary: 'AST / symbol / toolchain-verified code generation' },
  { id: 'deductive', label: 'deductive', summary: 'High-trust deductive chain & formal closure' },
  { id: 'inductive', label: 'inductive', summary: 'Cross-witness empirical convergence & generalization' },
  { id: 'abductive', label: 'abductive', summary: 'Causal root-cause & best-explanation hypothesis' },
  { id: 'constructive', label: 'constructive', summary: 'Compositional synthesis across multi-tier entities' },
  { id: 'adversarial', label: 'adversarial', summary: 'Counter-example probing & contradiction audit' },
];

export type ComputeEnvelopePreset = 'fast' | 'standard' | 'deep' | 'unlimited';

export const COMPUTE_ENVELOPES: {
  id: ComputeEnvelopePreset;
  label: string;
  bounds: string;
}[] = [
  { id: 'fast', label: 'fast', bounds: '2 hops · 15ms wall · interactive' },
  { id: 'standard', label: 'standard', bounds: '4 hops · 250ms wall · balanced' },
  { id: 'deep', label: 'deep', bounds: '8 hops · 2000ms wall · thorough' },
  { id: 'unlimited', label: 'unlimited', bounds: 'unbounded envelope (policy-capped)' },
];

export interface WitnessShareReceipt {
  witness: string;
  label?: string | null;
  trust?: number | null;
  fisher_info?: number | null;
  games?: number | null;
  share_bp: number;
}

export interface WorkCountersReceipt {
  wall_us?: number | null;
  db_probes?: number | null;
  rows_scanned?: number | null;
  bytes_alloc?: number | null;
  hops?: number | null;
  tokens_out?: number | null;
  truncated?: boolean | null;
  exhausted_reason?: number | string | null;
}

export interface AuthorityScopeReceipt {
  mode?: string | null;
  tenant?: string | null;
  principal?: string | null;
  witness_count?: number | null;
}

export const SHARE_BP_TOTAL = 10000;

export function summarizeWitnessShares(shares: readonly WitnessShareReceipt[] | null | undefined) {
  const list = shares ?? [];
  const sumBp = list.reduce((acc, item) => acc + (Number.isFinite(Number(item.share_bp)) ? Number(item.share_bp) : 0), 0);
  return {
    count: list.length,
    sumBp,
    expectedBp: SHARE_BP_TOTAL,
    conserved: list.length > 0 && sumBp === SHARE_BP_TOTAL,
  };
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
  firmware?: string | null;
  firmware_id?: string | null;
  program_id?: string | null;
  decision_id?: string | null;
  envelope?: string | null;
  authority_scope?: string | AuthorityScopeReceipt | null;
  top_salience?: number | null;
  mean_confidence?: number | null;
  ooda_verified?: number | null;
  ooda_unverified?: number | null;
  ooda_rounds_run?: number | null;
  culprit_id?: string | null;
  culprit_contrast?: number | null;
  wall_us?: number | null;
  db_probes?: number | null;
  work?: WorkCountersReceipt | null;
  witness_shares?: WitnessShareReceipt[] | null;
}
