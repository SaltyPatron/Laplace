export interface SubstrateCount { metric: string; value: number; }
export interface ConsensusHealth {
  evidenceRows: number;
  consensusRows: number;
  dedupRatio?: number | null;
  avgWitnesses?: number | null;
  maxWitnesses?: number | null;
}
export interface VisualizationEdge {
  subjectIdHex: string;
  subject: string;
  typeIdHex: string;
  type: string;
  objectIdHex: string;
  object: string;
  effectiveMu: number;
  witnesses: number;
}
export interface ExploreSourceRow {
  key: string;
  evidence: number;
  content: number;
  stage?: string | null;
  layer?: string | null;
  role?: string | null;
}
export interface ExploreStageSourceRow {
  cli: string;
  layer?: string | null;
  role?: string | null;
  links?: string | null;
}
export interface ExploreStageRow {
  stage: string;
  order: number;
  law?: string | null;
  sources: ExploreStageSourceRow[];
}
export interface ExploreCatalogResponse {
  counts: SubstrateCount[];
  consensus?: ConsensusHealth | null;
  multi_source_entity_count?: number | null;
  top_relations: VisualizationEdge[];
  sources: ExploreSourceRow[];
  stages: ExploreStageRow[];
  featured_refs: string[];
}
export interface SalientFactRow {
  type: string;
  fact: string;
  eff_mu: number;
  witnesses: number;
}
export interface ExploreResolveResponse {
  id_hex: string;
  label: string;
  ref_kind: string;
  exists: boolean;
  preview_facts: SalientFactRow[];
}
export interface ExploreEntityPreviewResponse {
  id_hex: string;
  label: string;
  tier?: number | null;
  type?: string | null;
  exists: boolean;
  evidence_count: number;
  preview_facts: SalientFactRow[];
}
export interface DecomposeNodeRow {
  ordinal: number;
  id_hex: string;
  label: string;
  tier: number;
  text_offset: number;
  text_length: number;
}
export interface ExploreAnchorNeighborRow {
  axis: string;
  id_hex: string;
  label: string;
  tier?: number | null;
  geodesic?: number | null;
  frechet?: number | null;
}
export interface ExploreSuggestion {
  surface: string;
  id_hex: string;
  distance: number;
}
export interface ExploreNotFoundResponse {
  reference: string;
  word_id_hex: string;
  exists: boolean;
  coord: number[];
  decomposition: DecomposeNodeRow[];
  neighbors: ExploreAnchorNeighborRow[];
  suggestions: ExploreSuggestion[];
  did_you_mean?: string | null;
}
export interface ExplorePhysicalityRow {
  type: number;
  x: number;
  y: number;
  z: number;
  m: number;
  radius: number;
  n_constituents: number;
}
export interface ExploreConsensusRow {
  direction: string;
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
export interface ExploreConstituentRow {
  ordinal: number;
  child_id_hex: string;
  child_label: string;
  run_length: number;
  flags: number;
}
export interface LabeledEvidenceItem {
  type_id: string;
  type_label: string;
  object_id: string;
  object_label: string;
  source_id: string;
  source_label: string;
  context_id?: string | null;
  outcome: number;
  observation_count: number;
  eff_mu?: number | null;
}
export interface ExploreEntityResponse {
  id_hex: string;
  label: string;
  tier?: number | null;
  type?: string | null;
  exists: boolean;
  evidence_count: number;
  physicalities: ExplorePhysicalityRow[];
  salient_facts: SalientFactRow[];
  consensus_out: ExploreConsensusRow[];
  consensus_in: ExploreConsensusRow[];
  senses: ExploreSenseRow[];
  constituents: ExploreConstituentRow[];
  evidence: LabeledEvidenceItem[];
}
export interface BillingReceipt {
  quote_id: string;
  amount_cents: number;
  currency: string;
  tenant: string;
  service_id: string;
}
export interface ExploreEntityDetailResponse {
  id: string;
  object: string;
  created: number;
  entity: ExploreEntityResponse;
  billing?: BillingReceipt | null;
}
export interface ExploreTrainingExportDetailResponse {
  id: string;
  export: {
    id_hex: string;
    label: string;
    generated_at: number;
    witness_rows: number;
    consensus_rows: number;
    entity: ExploreEntityResponse;
    members: { member_id_hex: string; member_label: string; kind: string; eff_mu: number; witnesses: number; }[];
    peers: { peer: string; kind: string; strength: number; }[];
  };
  billing?: BillingReceipt | null;
}
