import { apiGet, apiPost, type ApiOptions } from '../api/client';
import type { FirmwareProfile } from '../api/forwardProof';
import type { EntityRecord, TapeRow } from '../query/types';
import type { MeshLink } from '../explore/mesh/types';

export type ArenaChallengeMode = 'highway_race' | 'graphle' | 'provenance_detective';

export interface ArenaDecomposeNodeView {
  ordinal: number;
  id_hex: string;
  label: string;
  tier: number;
}

export interface ArenaEntitySummary {
  id_hex: string;
  label: string;
  tier?: number | null;
  hub_type?: string | null;
  witnessed: boolean;
  coord: number[];
  record?: EntityRecord | null;
  nodes: ArenaDecomposeNodeView[];
}

export interface ArenaBridgeHopView {
  bridge_id: string;
  bridge_label: string;
  relation_a: string;
  relation_b: string;
  claim_id_a?: string | null;
  claim_id_b?: string | null;
  eff_mu: number;
  witnesses: number;
  source: string;
}

export interface ArenaWitnessShareView {
  witness_id: string;
  label: string;
  trust: number;
  fisher_info: number;
  games: number;
  share_bp: number;
}

export interface ArenaCombineResponse {
  object: string;
  side_a: ArenaEntitySummary;
  side_b: ArenaEntitySummary;
  novel_merkle_id: string;
  midpoint_4d: number[];
  midpoint_radius: number;
  distance_4d: number;
  bridges: ArenaBridgeHopView[];
  shared_tape: TapeRow[];
  synthesis_summary?: string | null;
  synthesis_firmware?: string | null;
  shares: ArenaWitnessShareView[];
  shares_total_bp: number;
}

export interface ArenaEntityRef {
  id: string;
  label: string;
  hub_type?: string | null;
  neighbors: MeshLink[];
}

export interface ArenaGraphleRedactedNeighbor {
  relation: string;
  redacted_label: string;
  eff_mu?: number | null;
  witnesses: number;
}

export interface ArenaGraphleClues {
  tier?: number | null;
  mask_u64: number;
  mask_hex: string;
  highway_banks: string[];
  quadrant: string;
  coord: number[];
  radius: number;
  label_length: number;
  redacted_pattern: string;
  hub_type?: string | null;
  confirmed: number;
  contested: number;
  refuted: number;
  thin: number;
  redacted_neighbors: ArenaGraphleRedactedNeighbor[];
}

export interface ArenaWitnessCandidate {
  witness_id: string;
  label: string;
  kind: string;
}

export interface ArenaProvenanceDetectiveClue {
  claim_entity_id: string;
  claim_entity_label: string;
  claim_summary: string;
  record?: EntityRecord | null;
  candidates: ArenaWitnessCandidate[];
}

export interface ArenaChallengeResponse {
  object: string;
  mode: ArenaChallengeMode;
  seed: string;
  challenge_token: string;
  instructions: string;
  start?: ArenaEntityRef | null;
  target?: ArenaEntityRef | null;
  optimal_hint_hops?: number | null;
  graphle?: ArenaGraphleClues | null;
  detective?: ArenaProvenanceDetectiveClue | null;
}

export interface ArenaHighwayStepRequest {
  current_id: string;
  next_id: string;
  target_id: string;
  step_count: number;
}

export interface ArenaHighwayStepResponse {
  object: string;
  valid: boolean;
  message?: string | null;
  current_id: string;
  next_id: string;
  target_id: string;
  step_count: number;
  reached_target: boolean;
  next_entity?: ArenaEntityRef | null;
  target_record?: EntityRecord | null;
  shares: ArenaWitnessShareView[];
  shares_total_bp: number;
}

export interface ArenaVerifyRequest {
  mode: ArenaChallengeMode;
  seed: string;
  guess: string;
  reveal?: boolean;
}

export interface ArenaVerifyResponse {
  object: string;
  mode: ArenaChallengeMode;
  seed: string;
  correct: boolean;
  target_id?: string | null;
  target_label?: string | null;
  consensus_rating: number;
  guess_id?: string | null;
  distance_4d?: number | null;
  shares: ArenaWitnessShareView[];
  shares_total_bp: number;
}

export interface ArenaBlindSlotView {
  slot: 'left' | 'right';
  response: string;
  prompt_id: string;
  response_id?: string | null;
  top_salience: number;
  mean_confidence: number;
  wall_us: number;
  db_probes: number;
  tokens_out: number;
  ooda_rounds_run: number;
  ooda_verified: boolean;
}

export interface ArenaBlindRevealSide {
  slot: 'left' | 'right';
  firmware: FirmwareProfile;
  firmware_id?: string | null;
  disposition: string;
  shares: ArenaWitnessShareView[];
  shares_total_bp: number;
}

export interface ArenaBlindMatchupRequest {
  prompt: string;
  firmware_a: FirmwareProfile;
  firmware_b: FirmwareProfile;
  scope?: string | null;
  witnesses?: string[] | null;
}

export interface ArenaBlindMatchupResponse {
  object: string;
  prompt: string;
  left: ArenaBlindSlotView;
  right: ArenaBlindSlotView;
  reveal_left: ArenaBlindRevealSide;
  reveal_right: ArenaBlindRevealSide;
  swapped: boolean;
}

export function arenaCombine(a: string, b: string, opts: ApiOptions = {}): Promise<ArenaCombineResponse> {
  return apiPost<ArenaCombineResponse>('/v1/arena/combine', { a, b }, opts);
}

export function arenaChallenge(
  mode: ArenaChallengeMode,
  seed?: string,
  opts: ApiOptions = {},
): Promise<ArenaChallengeResponse> {
  const qs = new URLSearchParams({ mode });
  if (seed && seed.trim()) qs.set('seed', seed.trim());
  return apiGet<ArenaChallengeResponse>(`/v1/arena/challenge?${qs.toString()}`, opts);
}

export function arenaHighwayStep(
  req: ArenaHighwayStepRequest,
  opts: ApiOptions = {},
): Promise<ArenaHighwayStepResponse> {
  return apiPost<ArenaHighwayStepResponse>('/v1/arena/highway-step', req, opts);
}

export function arenaVerify(
  req: ArenaVerifyRequest,
  opts: ApiOptions = {},
): Promise<ArenaVerifyResponse> {
  return apiPost<ArenaVerifyResponse>('/v1/arena/verify', req, opts);
}

export function arenaBlindMatchup(
  req: ArenaBlindMatchupRequest,
  opts: ApiOptions = {},
): Promise<ArenaBlindMatchupResponse> {
  return apiPost<ArenaBlindMatchupResponse>('/v1/arena/blind-matchup', req, opts);
}
