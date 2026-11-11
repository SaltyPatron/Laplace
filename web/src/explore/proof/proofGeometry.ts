/** Pure storage-proof math: carrier-lane decoding, Glome node mapping, Super-Fibonacci sampling and label helpers. */
import type { StorageProofNodeRow, StorageProofPackedVertexRow } from '../types';
import { ordinalColor, type GlomeNode } from '../glome/GlomeCanvas';

export const STORAGE_PROOF_SURFACE_VERSION = 'v5' as const;
export const TWO_POW_53_MINUS_1 = (1n << 53n) - 1n;
export const MANTISSA_MASK = (1n << 52n) - 1n;
export const TWO_PI = 6.2831853071795864769252867665590057683943387987502;
export const SUPER_FIB_PHI = 1.4142135623730951454746218587388284504413604736328125;
export const SUPER_FIB_PSI = 1.5337511687552042888118041448362171649932861328125;

export type WalkDirection = 'trunk' | 'leaf';

// A sample of Tier-0 ranks is a placement distribution, not an ordered composition:
// consecutive samples are not constituents of one trajectory, so no curve joins them.
export const NO_TRAJECTORY: [number, number, number][] = [];

export interface CarrierLane {
  slot: bigint;
  bits: string;
  rawHex: string;
}

export function carrierLane(value: number): CarrierLane {
  const buffer = new ArrayBuffer(8);
  const view = new DataView(buffer);
  view.setFloat64(0, value, false);
  const raw = view.getBigUint64(0, false);
  const slot = (((raw >> 63n) & 1n) << 52n) | (raw & MANTISSA_MASK);
  return {
    slot,
    bits: slot.toString(2).padStart(53, '0'),
    rawHex: raw.toString(16).padStart(16, '0'),
  };
}

export function carrierCoordinate(value: number): number {
  const slot = carrierLane(value).slot;
  return Number(slot) / Number(TWO_POW_53_MINUS_1) * 2 - 1;
}

export function packedToCarrierNode(row: StorageProofPackedVertexRow, maxOrd: number): GlomeNode {
  return {
    id: `${row.child_id_hex}-carrier-${row.vertex}`,
    label: row.child_label || row.child_id_hex,
    x: carrierCoordinate(row.x),
    y: carrierCoordinate(row.y),
    z: carrierCoordinate(row.z),
    m: carrierCoordinate(row.m),
    radius: 1,
    ordinal: row.logical_ordinal,
    runLength: row.run_length,
    kind: 'constituent',
    color: ordinalColor(row.logical_ordinal, maxOrd),
  };
}

export function realizedToGlomeNode(
  row: StorageProofNodeRow['realized_vertices'][number],
  parentId?: string,
  maxOrd = 1,
): GlomeNode {
  return {
    id: `${row.child_id_hex}-realized-${row.ordinal}`,
    label: row.child_label || row.child_id_hex,
    x: row.x,
    y: row.y,
    z: row.z,
    m: row.m,
    radius: row.radius,
    ordinal: row.ordinal,
    parentId,
    kind: 'constituent',
    color: ordinalColor(row.ordinal, maxOrd),
  };
}

export function nodeToGlomeNode(node: StorageProofNodeRow, selected: boolean, parentId?: string): GlomeNode {
  return {
    id: node.id_hex,
    label: node.label || node.id_hex,
    x: node.x,
    y: node.y,
    z: node.z,
    m: node.m,
    radius: node.radius,
    ordinal: node.ordinal,
    kind: selected ? 'primary' : node.tier === 0 ? 'constituent' : 'walk',
    tier: node.tier,
    parentId,
  };
}

export function depthOf(node: StorageProofNodeRow, byOrdinal: Map<number, StorageProofNodeRow>): number {
  let depth = 0;
  let parent = node.parent_ordinal;
  const seen = new Set<number>();
  while (parent != null && !seen.has(parent)) {
    seen.add(parent);
    const p = byOrdinal.get(parent);
    if (!p) break;
    depth++;
    parent = p.parent_ordinal;
  }
  return depth;
}

export function compactId(id: string): string {
  return id.length <= 18 ? id : `${id.slice(0, 8)}…${id.slice(-8)}`;
}

export function prettyNumber(value: number, digits = 6): string {
  return Number.isFinite(value) ? value.toFixed(digits) : String(value);
}

export function radicalInverseBase2(value: number): number {
  let n = Math.max(0, Math.floor(value));
  let fraction = 0.5;
  let out = 0;
  while (n > 0) {
    if (n % 2 === 1) out += fraction;
    n = Math.floor(n / 2);
    fraction *= 0.5;
  }
  return out;
}

export function superFibonacciPoint(rank: number, n: number): [number, number, number, number] {
  const s = rank + 0.5;
  const t = s / n;
  const r = Math.sqrt(t);
  const cap = Math.sqrt(1 - t);
  const alpha = s * (TWO_PI / SUPER_FIB_PHI);
  const beta = s * (TWO_PI / SUPER_FIB_PSI);
  return [r * Math.sin(alpha), r * Math.cos(alpha), cap * Math.sin(beta), cap * Math.cos(beta)];
}

export function superFibonacciOpenPoint(rank: number): [number, number, number, number] {
  const s = rank + 0.5;
  const t = radicalInverseBase2(rank);
  const r = Math.sqrt(t);
  const cap = Math.sqrt(1 - t);
  const alpha = s * (TWO_PI / SUPER_FIB_PHI);
  const beta = s * (TWO_PI / SUPER_FIB_PSI);
  return [r * Math.sin(alpha), r * Math.cos(alpha), cap * Math.sin(beta), cap * Math.cos(beta)];
}

export function distributionNodes(
  occupied: number,
  atomWindow: number,
  interleaved: boolean,
): GlomeNode[] {
  const sampleCount = Math.min(1600, Math.max(1, occupied));
  const step = occupied / sampleCount;
  const nodes: GlomeNode[] = [];
  let previous = -1;
  for (let i = 0; i < sampleCount; i++) {
    const rank = Math.min(occupied - 1, Math.floor((i + 0.5) * step));
    if (rank === previous) continue;
    previous = rank;
    const [x, y, z, m] = interleaved
      ? superFibonacciOpenPoint(rank)
      : superFibonacciPoint(rank, atomWindow);
    nodes.push({
      id: `${interleaved ? 'open' : 'canonical'}-${rank}`,
      label: `DUCET rank ${rank.toLocaleString()}`,
      x, y, z, m,
      radius: 1,
      kind: 'constituent',
    });
  }
  return nodes;
}

/** Whitespace is content too: draw it so a space node is not an empty box. */
export function visibleText(text: string | null | undefined): string {
  if (!text) return '';
  return text
    .replace(/ /g, '␠')
    .replace(/\r\n|\n/g, '↵')
    .replace(/\r/g, '␍')
    .replace(/\t/g, '⇥')
    .replace(/ /g, '⍽');
}
