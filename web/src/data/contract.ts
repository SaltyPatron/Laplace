/**
 * Entity contract: facets and strands. Spec: web/docs/entity-contract.md.
 * Every facet returns strands in one shape, so every view (lens, web, glome, comparison, citation) agrees.
 */

/** 32 lowercase hex characters: the first 16 bytes of BLAKE3-256. */
export type EntityId = string;

export interface EntityRef {
  id: EntityId;
  label: string;
  /** Merkle height (1 + max child height). Never a role such as "word" or "sentence". */
  tier: number;
  kind?: string;
}

export interface Identity {
  ref: EntityRef;
  /** Point on S³ / inside the 4-ball; null when the entity has no recorded placement. */
  coord: [number, number, number, number] | null;
  hilbert: string;
  /** Bank name → enum value. Banks filter; they are never strand targets. */
  banks: Record<string, string>;
  size: number;
  exists: boolean;
}

export type StructuralKind = 'contains' | 'contained-in' | 'precedes' | 'follows' | 'co-occurs';
export type StrandKind = StructuralKind | `attested:${string}` | 'shape-similar';

/**
 * confirmed / contested / refuted / thin: folded consensus. thin = folded, but not enough independent evidence
 * to confirm (one witness, one game is always thin). prior: no matches folded yet. structural: physicality counts.
 */
export type StrandState = 'confirmed' | 'contested' | 'refuted' | 'thin' | 'prior' | 'structural';

/** A witness (source trunk) that spun a strand. */
export interface Spider {
  witness: EntityRef;
  /** Declared trust class from the registry. */
  trust: string;
  /** Games (attested) or occurrences (structural). */
  count: number;
  lineage?: string;
  score?: number;
  position?: number;
}

export interface Strand {
  from: EntityRef;
  to: EntityRef;
  kind: StrandKind;
  /** Consensus rating (attested), occurrence count (structural) or 1/Fréchet on realized curves (shape). */
  strength: number | null;
  /** Consensus deviation for attested strands; null otherwise. */
  uncertainty: number | null;
  state: StrandState;
  spiders: Spider[];
  /** Container for precedes / follows / co-occurs. */
  via?: EntityRef;
  /** Ordinal and run length for made-of strands. */
  position?: number;
  run?: number;
  /** Surfaces behind the strand, e.g. the sentences behind a co-occurrence. */
  evidence?: { total: number; sample: EntityRef[] };
}

export type FacetName = 'made-of' | 'appears-in' | 'next-to' | 'occurs-with' | 'means' | 'shape';

export interface Facet<T = Strand> {
  entity: EntityRef;
  facet: FacetName;
  items: T[];
  /** null only when the read cannot count cheaply; then `truncated` is true. */
  total: number | null;
  cursor: string | null;
  truncated: boolean;
  /** Distinct spiders contributing to this page, for the witness filter. */
  sources: Spider[];
  /** Diagnostics only; shown in Diagnostics mode. */
  receipt?: Record<string, unknown>;
}

export interface FacetQuery {
  cursor?: string;
  limit?: number;
  kind?: StrandKind[];
  witness?: EntityId[];
  minStrength?: number;
  state?: StrandState[];
}

/** Which facet produces which strand kinds; lenses register against facets. */
export const FACET_KINDS: Record<FacetName, readonly string[]> = {
  'made-of': ['contains'],
  'appears-in': ['contained-in'],
  'next-to': ['precedes', 'follows'],
  'occurs-with': ['co-occurs'],
  means: ['attested:*'],
  shape: ['shape-similar'],
};

export type ForwardStage = 'resolve' | 'couple' | 'orient' | 'route' | 'scan' | 'compose' | 'propose' | 'steer' | 'select' | 'realize' | 'witness';

/** A chat / forward-pass turn. The three states are never conflated. */
export type Turn =
  | { state: 'answered'; text: string; citations: Strand[]; trace_id: string }
  | { state: 'abstained'; reason: string; near_misses: { ref: EntityRef; strength: number | null; uncertainty: number | null }[]; trace_id: string }
  | { state: 'failed'; stage: ForwardStage; message: string; trace_id: string };

export const isAttested = (kind: StrandKind): kind is `attested:${string}` => kind.startsWith('attested:');
export const relationOf = (kind: StrandKind) => (isAttested(kind) ? kind.slice('attested:'.length) : null);
