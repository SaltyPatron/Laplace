/** Consensus-graph data model and pure transforms: types, layout seeds, web/star conversion and relation filtering. */
import type { ExploreConsensusRow } from '../types';
import type { WalkPathNode } from '../store';
import { isPackagingRelation } from '../../data/facets';

export interface WebNode {
  id: string;
  label: string;
  hop: number;
  walk?: boolean;
  beliefX?: number;
  beliefY?: number;
  beliefZ?: number;
  x?: number;
  y?: number;
  z?: number;
  fx?: number;
  fy?: number;
  fz?: number;
}

export interface WebEdge {
  source: string;
  target: string;
  type: string;
  mu: number;
  witnesses: number;
  hop: number;
  /** Canonical OP4 complete weight: signed Glicko expectation in [-1, 1]. */
  weight: number;
  /** Canonical optimistic-bound verdict from consensus.refuted(rating, rd). */
  refuted: boolean;
  rating: number;
  rd: number;
  volatility: number;
  walk?: boolean;
}

export interface WebGraph {
  nodes: WebNode[];
  edges: WebEdge[];
}

export type Dim = 'belief' | '2d' | '3d';
export type GraphData = { nodes: WebNode[]; links: WebEdge[] };

/** World units — stay small vs link length so zoom-in is readable. */
export const NODE_REL_SIZE = 0.9;
export const SHELL_RADIUS = 78;
export const LINK_BASE = 52;
export const CHARGE = -420;

export function clamp(n: number, lo: number, hi: number) {
  return Math.min(hi, Math.max(lo, n));
}

function hash32(text: string): number {
  let h = 0x811c9dc5;
  for (let i = 0; i < text.length; i++) {
    h ^= text.charCodeAt(i);
    h = Math.imul(h, 0x01000193);
  }
  return h >>> 0;
}

/**
 * Deterministic point on a 3-D shell, hashed from the entity id with radius scaled by hop.
 * Regenerated whenever the 3-D renderer gets its own copy, so a 2-D simulation's z=0
 * coordinates never seed the 3-D force system into a planar symmetry.
 */
function volumetricSeed(id: string, hop: number): [number, number, number] {
  const u = (hash32(`${id}\0u`) + 0.5) / 0x100000000;
  const v = (hash32(`${id}\0v`) + 0.5) / 0x100000000;
  const z = 1 - 2 * u;
  const phi = 2 * Math.PI * v;
  const xy = Math.sqrt(Math.max(0, 1 - z * z));
  const radius = Math.max(1, hop) * SHELL_RADIUS;
  return [
    Math.cos(phi) * xy * radius,
    Math.sin(phi) * xy * radius,
    z * radius,
  ];
}

/**
 * A fresh node and link copy for one render dimension. react-force-graph mutates node
 * coordinates and link endpoints in place, so the 2-D and 3-D engines never share objects.
 */
export function graphForDimension(base: GraphData, dim: Dim, centerId: string): GraphData {
  const spectral = base.nodes.filter((n) =>
    Number.isFinite(n.beliefX) && Number.isFinite(n.beliefY) && Number.isFinite(n.beliefZ));
  const beliefReady = dim === 'belief' && spectral.length > 0;
  const center = spectral.find((n) => n.id === centerId) ?? spectral[0];
  const cx = center?.beliefX ?? 0, cy = center?.beliefY ?? 0, cz = center?.beliefZ ?? 0;
  let spectralRadius = 0;
  if (beliefReady)
    for (const n of spectral)
      spectralRadius = Math.max(spectralRadius, Math.hypot(
        (n.beliefX ?? 0) - cx, (n.beliefY ?? 0) - cy, (n.beliefZ ?? 0) - cz));
  const targetRadius = Math.max(
    SHELL_RADIUS * 1.5,
    Math.min(SHELL_RADIUS * 5, SHELL_RADIUS * Math.sqrt(Math.max(2, spectral.length)) / 2),
  );
  // Translation and one uniform display scale preserve the native spectral geometry.
  const spectralScale = spectralRadius > 0 ? targetRadius / spectralRadius : 1;

  const nodes = base.nodes.map((node) => {
    const clean: WebNode = {
      id: node.id, label: node.label, hop: node.hop, walk: node.walk,
      beliefX: node.beliefX, beliefY: node.beliefY, beliefZ: node.beliefZ,
    };
    if (beliefReady) {
      if (Number.isFinite(node.beliefX) && Number.isFinite(node.beliefY) && Number.isFinite(node.beliefZ)) {
        const x = ((node.beliefX ?? 0) - cx) * spectralScale;
        const y = ((node.beliefY ?? 0) - cy) * spectralScale;
        const z = ((node.beliefZ ?? 0) - cz) * spectralScale;
        return { ...clean, x, y, z, fx: x, fy: y, fz: z };
      }
      // A vertex with no positive conductance is outside the positive belief
      // manifold. Keep it visible, but scatter it deterministically beyond the
      // coherent spectral basin rather than pinning every dead end at (0,0,0).
      const [sx, sy, sz] = volumetricSeed(node.id, node.hop);
      const sr = Math.max(1e-9, Math.hypot(sx, sy, sz));
      const outerRadius = targetRadius * (1.18 + Math.min(3, Math.max(0, node.hop)) * 0.12);
      const outerScale = outerRadius / sr;
      const x = sx * outerScale;
      const y = sy * outerScale;
      const z = sz * outerScale;
      return { ...clean, x, y, z, fx: x, fy: y, fz: z };
    }
    if (node.id === centerId) {
      return dim !== '2d'
        ? { ...clean, x: 0, y: 0, z: 0, fx: 0, fy: 0, fz: 0 }
        : { ...clean, x: 0, y: 0, fx: 0, fy: 0 };
    }
    if (dim !== '2d') {
      const [x, y, z] = volumetricSeed(node.id, node.hop);
      return { ...clean, x, y, z };
    }
    return clean;
  });
  return { nodes, links: base.links.map((link) => ({ ...link })) };
}

/** Positive signed Glicko standing binds the layout; neutral/refuted strands do not. */
export function binding(weight: number): number {
  if (!Number.isFinite(weight)) return 0;
  return Math.min(1, Math.max(0, weight));
}

/** Witness mass is visual evidence only; Glicko already folded it into standing. */
export function witnessMass(witnesses: number): number {
  if (!(witnesses > 0) || !Number.isFinite(witnesses)) return 0;
  return Math.min(1, Math.log2(witnesses + 1) / 6);
}

export function fromWeb(web: WebGraph, walkPath: WalkPathNode[]): GraphData {
  const unique = new Map<string, WebNode>();
  for (const n of web.nodes) {
    if (unique.has(n.id)) continue;
    unique.set(n.id, {
      id: n.id,
      label: n.label,
      hop: n.hop,
      beliefX: n.beliefX,
      beliefY: n.beliefY,
      beliefZ: n.beliefZ,
      walk: walkIdsHas(walkPath, n.id),
    });
  }
  const nodes = [...unique.values()];
  const seen = new Set(nodes.map((n) => n.id));
  for (const step of walkPath) {
    if (!seen.has(step.idHex)) {
      nodes.push({ id: step.idHex, label: step.label, hop: 0, walk: true });
      seen.add(step.idHex);
    }
  }
  const links: WebEdge[] = web.edges
    .filter((e) => e.source !== e.target)
    .map((e) => ({ ...e, walk: false }));
  for (let i = 1; i < walkPath.length; i++) {
    if (walkPath[i - 1].idHex === walkPath[i].idHex) continue;
    links.push({
      source: walkPath[i - 1].idHex,
      target: walkPath[i].idHex,
      type: 'walk',
      mu: 400,
      witnesses: 0,
      hop: 0,
      weight: 1,
      refuted: false,
      rating: 0,
      rd: 0,
      volatility: 0,
      walk: true,
    });
  }
  return { nodes, links };
}

export function fromStar(
  centerId: string,
  centerLabel: string,
  edges: ExploreConsensusRow[],
  walkPath: WalkPathNode[],
): GraphData {
  const nodes = new Map<string, WebNode>();
  nodes.set(centerId, {
    id: centerId,
    label: centerLabel,
    hop: 0,
    walk: walkIdsHas(walkPath, centerId),
  });
  const links: WebEdge[] = [];
  for (const e of edges) {
    const id = e.entity_id_hex || e.entity_label;
    if (id === centerId) continue;
    if (!nodes.has(id)) {
      nodes.set(id, { id, label: e.entity_label || e.type, hop: 1, walk: walkIdsHas(walkPath, id) });
    }
    const source = e.direction === 'in' ? id : centerId;
    const target = e.direction === 'in' ? centerId : id;
    links.push({
      source,
      target,
      type: e.type,
      mu: e.eff_mu,
      witnesses: e.witnesses,
      hop: 1,
      // The entity detail surface carries display μ only. Do not synthesize a
      // browser-side Glicko weight from it; the expanded native web supplies the
      // canonical signed fold and immediately becomes the standing-field layout.
      weight: 0,
      refuted: false,
      rating: 0,
      rd: 0,
      volatility: 0,
      walk: false,
    });
  }
  for (const step of walkPath) {
    if (!nodes.has(step.idHex)) nodes.set(step.idHex, { id: step.idHex, label: step.label, hop: 0, walk: true });
  }
  for (let i = 1; i < walkPath.length; i++) {
    if (walkPath[i - 1].idHex === walkPath[i].idHex) continue;
    links.push({
      source: walkPath[i - 1].idHex,
      target: walkPath[i].idHex,
      type: 'walk',
      mu: 400,
      witnesses: 0,
      hop: 0,
      weight: 1,
      refuted: false,
      rating: 0,
      rd: 0,
      volatility: 0,
      walk: true,
    });
  }
  return { nodes: [...nodes.values()], links };
}

function walkIdsHas(walkPath: WalkPathNode[], id: string) {
  return walkPath.some((n) => n.idHex === id);
}

const endpoint = (end: unknown) => (typeof end === 'string' ? end : (end as WebNode).id);

/** Packaging pointers and bank fields are filters, never strands: they must not appear as web nodes. */
export function withoutPackaging(data: GraphData, centerId: string): GraphData {
  return withoutRelations(data, null, centerId);
}

/** Drop strands of hidden (or packaging) relations, then subjects no remaining strand reaches. */
export function withoutRelations(data: GraphData, hidden: ReadonlySet<string> | null, centerId: string): GraphData {
  const links = data.links.filter((l) => l.walk || (!isPackagingRelation(l.type) && !hidden?.has(l.type)));
  if (links.length === data.links.length) return data;
  const kept = new Set<string>([centerId]);
  for (const l of links) {
    kept.add(endpoint(l.source));
    kept.add(endpoint(l.target));
  }
  return { nodes: data.nodes.filter((n) => n.walk || kept.has(n.id)), links };
}

export function relationCounts(links: WebEdge[]): [string, number][] {
  const counts = new Map<string, number>();
  for (const l of links) counts.set(l.type, (counts.get(l.type) ?? 0) + 1);
  return [...counts].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));
}
