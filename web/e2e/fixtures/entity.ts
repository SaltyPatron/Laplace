/** A `/v1/explore/entities/{id}` response: the read the entity page's lenses adapt from. */
export interface EntityFixture {
  id_hex: string;
  label: string;
  tier?: number;
  type?: string;
  exists?: boolean;
  consensus_out?: { type: string; entity_id_hex: string; entity_label: string; eff_mu: number; witnesses: number }[];
  consensus_in?: { type: string; entity_id_hex: string; entity_label: string; eff_mu: number; witnesses: number }[];
  constituents?: { ordinal: number; child_id_hex: string; child_label: string; run_length: number; flags: number }[];
}

export function entityResponse(entity: EntityFixture) {
  return {
    id: `entity-${entity.id_hex}`,
    object: 'explore.entity',
    created: 0,
    entity: {
      tier: 1,
      type: '',
      exists: true,
      evidence_count: 0,
      physicalities: [],
      salient_facts: [],
      senses: [],
      packed_vertices: [],
      realized_vertices: [],
      evidence: [],
      ...entity,
      consensus_out: (entity.consensus_out ?? []).map((row) => ({ direction: 'out', ...row })),
      consensus_in: (entity.consensus_in ?? []).map((row) => ({ direction: 'in', ...row })),
      constituents: entity.constituents ?? [],
    },
  };
}
