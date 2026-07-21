export interface QueryShape {
  shape: string;
  summary: string;
  needs_topic2: boolean;
  needs_type: boolean;
  accepts_lang: boolean;
}

export interface RelationBand {
  band: number;
  name: string;
  rank: number;
  relation_types: number;
  consensus_rows: number;
}

export interface QueryRow {
  reply: string;
  eff_mu?: number | null;
  witnesses?: number | null;
}

export interface QueryResult {
  object: string;
  shape: string;
  topic_id?: string | null;
  topic_label?: string | null;
  topic2_id?: string | null;
  topic2_label?: string | null;
  bands?: number[] | null;
  rows: QueryRow[];
}

/** Every dial the substrate accepts for a read. */
export interface QueryDials {
  depth: number;
  breadth: number;
  limit: number;
  steps: number;
  spread: number;
  max_stride: number;
  seed: string;
  directed: boolean;
  use_geometry: boolean;
}

export const DIAL_DEFAULTS: QueryDials = {
  depth: 4,
  breadth: 5,
  limit: 40,
  steps: 24,
  spread: 0.7,
  max_stride: 5,
  seed: '',
  directed: false,
  use_geometry: false,
};

/** Which dials each shape actually reads. A control that does nothing is worse
 *  than no control, so the panel shows only what the chosen shape consumes. */
export const SHAPE_DIALS: Record<string, (keyof QueryDials)[]> = {
  band_facts: ['limit'],
  beam: ['depth', 'breadth', 'limit'],
  path: ['depth', 'directed', 'use_geometry'],
  neighbors: ['limit'],
  generate: ['steps', 'max_stride', 'spread', 'breadth', 'seed'],
  walk: ['depth'],
  complete: ['depth', 'breadth'],
};
