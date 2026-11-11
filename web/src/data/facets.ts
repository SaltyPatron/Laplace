/**
 * Facet reads on the entity contract (web/docs/entity-contract.md).
 *
 * readFacet() uses the contract endpoint (/v1/entities/{id}/{facet}) when /v1/capabilities advertises
 * entity_facets as live; otherwise it adapts today's explore endpoints into strands, so every lens is written
 * once, against the contract.
 * A facet with no backend read at all throws FacetUnavailable, and the lens says so instead of inventing data.
 */
import { apiGet, ApiError, type ApiOptions } from '../api/client';
import { exploreContainers, exploreEntity, exploreNeighbors } from '../explore/api';
import type { ExploreEntityResponse, LabeledEvidenceItem } from '../explore/types';
import type { EntityRef, Facet, FacetName, FacetQuery, Identity, Spider, Strand, StrandState } from './contract';

export class FacetUnavailable extends Error {
  constructor(public readonly facet: FacetName) { super(`The ${facet} read is not available on this host yet.`); }
}

const NEUTRAL = 1500;
/**
 * Relations that are source packaging or bank values, not meaning (recording law). Observed on staging for
 * "dog": an empty relation name, upos, WordNet pointer symbols, sense frequency. Fixed at ingest by the backend;
 * filtered here so no lens ever presents them as knowledge.
 */
const PACKAGING_RELATION = /^\s*$|^upos$|^lexfile$|^deprel$|type of pointer that lemma|^HAS[ _]SENSE[ _]FREQUENCY$|^sense[ _]key$/i;
export const isPackagingRelation = (relation: string) => PACKAGING_RELATION.test(relation);
const ref = (id: string, label: string, tier = 0, kind?: string | null): EntityRef => ({ id: id.toLowerCase(), label: label || id.slice(0, 8), tier, kind: kind ?? undefined });

/** Folded standing → state. Conservative standing above neutral confirms; no witnesses means nothing folded. */
function stateOf(effMu: number | null | undefined, witnesses: number | null | undefined): StrandState {
  if (!witnesses) return 'prior';
  if (effMu == null) return 'thin';
  return effMu > NEUTRAL ? 'confirmed' : 'thin';
}

function query(q: FacetQuery = {}) {
  const params = new URLSearchParams();
  if (q.cursor) params.set('cursor', q.cursor);
  if (q.limit) params.set('limit', String(q.limit));
  for (const kind of q.kind ?? []) params.append('kind', kind);
  for (const witness of q.witness ?? []) params.append('witness', witness);
  if (q.minStrength != null) params.set('min_strength', String(q.minStrength));
  for (const state of q.state ?? []) params.append('state', state);
  const text = params.toString();
  return text ? `?${text}` : '';
}

/**
 * Whether this host serves the contract endpoints, read once from /v1/capabilities (entity_facets).
 * Never probed with requests that 404: a deployed page must not log failures for an absent endpoint.
 */
let contractServed: Promise<boolean> | null = null;
function servesContract(opts: ApiOptions): Promise<boolean> {
  contractServed ??= apiGet<{ endpoints?: { entity_facets?: { status?: string } } }>('/v1/capabilities', { tenant: opts.tenant })
    .then((caps) => caps.endpoints?.entity_facets?.status === 'live')
    .catch(() => { contractServed = null; return false; });
  return contractServed;
}
const notServed = (error: unknown) => error instanceof ApiError && (error.status === 404 || error.status === 405);

// One entity read per id is shared by every adapter that needs it.
const entityReads = new Map<string, Promise<ExploreEntityResponse>>();
function entity(id: string, opts: ApiOptions) {
  const key = id.toLowerCase();
  let read = entityReads.get(key);
  if (!read) {
    read = exploreEntity(key, opts).then((r) => r.entity);
    read.catch(() => entityReads.delete(key));
    entityReads.set(key, read);
    if (entityReads.size > 64) entityReads.delete(entityReads.keys().next().value!);
  }
  return read;
}

export async function readIdentity(id: string, opts: ApiOptions = {}): Promise<Identity> {
  if (await servesContract(opts)) {
    try { return await apiGet<Identity>(`/v1/entities/${id}`, opts); }
    catch (error) { if (!notServed(error)) throw error; }
  }
  const e = await entity(id, opts);
  const p = e.physicalities[0];
  return {
    ref: ref(e.id_hex, e.label, e.tier ?? 0, e.type),
    coord: p ? [p.x, p.y, p.z, p.m] : null,
    hilbert: '',
    banks: {},
    size: e.constituents.length,
    exists: e.exists,
  };
}

export async function readFacet(id: string, facet: FacetName, q: FacetQuery = {}, opts: ApiOptions = {}): Promise<Facet> {
  let result: Facet | null = null;
  if (await servesContract(opts)) {
    try { result = await apiGet<Facet>(`/v1/entities/${id}/${facet}${query(q)}`, opts); }
    catch (error) { if (!notServed(error)) throw error; }
  }
  result ??= await adapt(id, facet, q, opts);
  const items = result.items.filter((s) => !(s.kind.startsWith('attested:') && isPackagingRelation(s.kind.slice('attested:'.length))));
  return items.length === result.items.length ? result : { ...result, items, total: result.total == null ? null : result.total - (result.items.length - items.length) };
}

function spidersFor(evidence: LabeledEvidenceItem[], typeLabel: string, objectId: string): Spider[] {
  const bySource = new Map<string, Spider>();
  for (const item of evidence) {
    if (item.object_id?.toLowerCase() !== objectId.toLowerCase() || item.type_label !== typeLabel || !item.source_id) continue;
    const key = item.source_id;
    const prior = bySource.get(key);
    const count = (item.games ?? item.observation_count ?? 1) + (prior?.count ?? 0);
    bySource.set(key, {
      witness: ref(item.source_id, item.source_label),
      trust: item.trust != null ? item.trust.toFixed(2) : '—',
      count,
      lineage: item.dependence_root ?? undefined,
      score: item.score ?? undefined,
      position: item.position ?? undefined,
    });
  }
  return [...bySource.values()];
}

function envelope(e: EntityRef, facet: FacetName, items: Strand[], truncated = false, total: number | null = items.length): Facet {
  const sources = new Map<string, Spider>();
  for (const strand of items) for (const spider of strand.spiders) if (!sources.has(spider.witness.id)) sources.set(spider.witness.id, spider);
  return { entity: e, facet, items, total: truncated ? null : total, cursor: null, truncated, sources: [...sources.values()] };
}

async function adapt(id: string, facet: FacetName, q: FacetQuery, opts: ApiOptions): Promise<Facet> {
  switch (facet) {
    case 'made-of': {
      const e = await entity(id, opts);
      const from = ref(e.id_hex, e.label, e.tier ?? 0, e.type);
      const items = e.constituents.map<Strand>((c) => ({
        from, to: ref(c.child_id_hex, c.child_label), kind: 'contains', strength: c.run_length, uncertainty: null,
        state: 'structural', spiders: [], position: c.ordinal, run: c.run_length,
      }));
      return envelope(from, facet, items);
    }
    case 'appears-in': {
      const limit = q.limit ?? 100;
      const [e, read] = await Promise.all([entity(id, opts), exploreContainers(id, 1, limit, opts)]);
      const from = ref(e.id_hex, e.label, e.tier ?? 0, e.type);
      const items = read.containers.containers.map<Strand>((c) => ({
        from, to: ref(c.entity_id_hex, c.entity_label, c.tier, c.type), kind: 'contained-in', strength: null,
        uncertainty: null, state: 'structural', spiders: [],
      }));
      return envelope(from, facet, items, items.length >= limit);
    }
    case 'means': {
      const e = await entity(id, opts);
      const from = ref(e.id_hex, e.label, e.tier ?? 0, e.type);
      const rows = [...e.consensus_out.map((r) => ({ r, out: true })), ...e.consensus_in.map((r) => ({ r, out: false }))];
      const items = rows.map<Strand>(({ r, out }) => {
        const other = ref(r.entity_id_hex, r.entity_label);
        return {
          from: out ? from : other, to: out ? other : from, kind: `attested:${r.type}`,
          strength: r.eff_mu, uncertainty: null, state: stateOf(r.eff_mu, r.witnesses),
          spiders: out ? spidersFor(e.evidence, r.type, r.entity_id_hex) : [],
        };
      }).sort((a, b) => (b.strength ?? -Infinity) - (a.strength ?? -Infinity));
      return envelope(from, facet, items);
    }
    case 'shape': {
      const [e, read] = await Promise.all([entity(id, opts), exploreNeighbors(id, q.limit ?? 12, opts)]);
      const from = ref(e.id_hex, e.label, e.tier ?? 0, e.type);
      const items = read.neighbors.structural.filter((n) => n.neighbor_id_hex).map<Strand>((n) => ({
        from, to: ref(n.neighbor_id_hex!, n.neighbor), kind: 'shape-similar',
        strength: n.frechet ? 1 / n.frechet : n.geodesic ? 1 / n.geodesic : null,
        uncertainty: null, state: 'structural', spiders: [],
      }));
      return envelope(from, facet, items);
    }
    case 'next-to':
    case 'occurs-with':
      throw new FacetUnavailable(facet);
  }
}
