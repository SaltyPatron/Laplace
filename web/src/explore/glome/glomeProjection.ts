/**
 * Glome data model and pure projection math: node shape, readable labels, path-carrier
 * decoding and the Packed / Carrier / Placement display projections. No React, no WebGL.
 */
import type { ExplorePhysicalityRow } from '../types';

export type GlomeProjection = 'packed' | 'carrier' | 'placement';

export interface GlomeNode {
  id: string;
  label: string;
  x: number;
  y: number;
  z: number;
  /** 4D radius_origin for Placement ball depth; ignored for Packed shell. */
  radius: number;
  m?: number;
  ordinal?: number;
  runLength?: number;
  mu?: number;
  /** Count of evidence rows touching this entity; visual mass only, not truth. */
  evidenceRows?: number;
  kind?: 'primary' | 'constituent' | 'neighbor' | 'walk' | 'peer';
  /** Optional paint override (Packed ordinal ramp). */
  color?: string;
  /** Compositional altitude, when the caller knows it. */
  tier?: number;
  /** Id of the node this one is a child of in the Merkle DAG, when the caller knows it. */
  parentId?: string;
}

/** What point color encodes. Color is a metric, never a decoration. */
export type GlomeColorBy = 'kind' | 'tier' | 'depth' | 'ordinal';

const SHELL = 0.85;
/** Initial camera distance; the scene keeps point size constant relative to it. */
export const CAMERA_START = 2.2;

const VISIBLE_CONTROL: Record<string, string> = {
  ' ': '␠', '\n': '↵', '\r': '␍', '\t': '⇥', ' ': '⍽', '\u0000': '␀', '\u007f': '␡',
};
const HEX_ID = /^(?:\\x)?[0-9a-f]{32}$/i;
const UNREADABLE = /[□�]/gu;

/**
 * A label a person can read. Whitespace and control characters are drawn as
 * visible symbols instead of font-fallback boxes, a bare 32-hex entity id is
 * shortened, and a missing label becomes "unnamed · 1a2b…" (or "U+XXXX" when
 * the node names a codepoint).
 */
export function readableGlomeLabel(label: string | null | undefined, id?: string | null): string {
  const raw = label ?? '';
  const shortId = id && HEX_ID.test(id) ? `${id.replace(/^\\x/, '').slice(0, 4).toLowerCase()}…` : '';
  const unnamed = shortId ? `unnamed · ${shortId}` : 'unnamed';
  if (!raw) return unnamed;
  const trimmed = raw.trim();
  if (HEX_ID.test(trimmed)) return `entity · ${trimmed.replace(/^\\x/, '').slice(0, 4).toLowerCase()}…`;
  const codepoint = /^u\+([0-9a-f]{4,6})$/i.exec(trimmed);
  if (codepoint) return `U+${codepoint[1].toUpperCase()}`;
  // Spaces between words stay spaces; a label that is only whitespace (or a
  // lone space) is drawn with visible symbols.
  const allBlank = trimmed === '';
  let out = '';
  for (const ch of raw) {
    const cp = ch.codePointAt(0) ?? 0;
    if ((ch === ' ' || ch === ' ') && !allBlank) out += ch;
    else if (VISIBLE_CONTROL[ch]) out += VISIBLE_CONTROL[ch];
    else if (cp < 0x20) out += String.fromCodePoint(0x2400 + cp); // ␀..␟ control pictures
    else if ((cp >= 0x80 && cp < 0xa0) || (cp >= 0x200b && cp <= 0x200f) || cp === 0xfeff
      || (cp >= 0x2028 && cp <= 0x202e) || (cp >= 0x2060 && cp <= 0x2064)) {
      out += `U+${cp.toString(16).toUpperCase().padStart(4, '0')}`;
    } else out += ch;
  }
  // A replacement box or U+FFFD in the label itself means the text was lost
  // upstream; keep what survived, or fall back to the short id.
  const kept = out.replace(UNREADABLE, '').trim();
  if (!kept) return unnamed;
  const chars = Array.from(kept);
  return chars.length > 48 ? `${chars.slice(0, 47).join('')}…` : kept;
}

/**
 * Unpack the IEEE-754 binary64 mantissa payload of a packed trajectory coordinate
 * into [-1, 1] display space.
 *
 * Supports both canonical carrier formats:
 * - Native `lp_id_to_xyz` / `lp_m_full` (`Laplace-Native/src/geometry.c`):
 *   biased exponent 1021 (`0x3fd`, values in [0.25, 0.5)), where X holds 43 bits
 *   (bits 0..42 of `lp_id`), Y holds 43 bits (bits 43..85), Z holds 42 bits
 *   (bits 86..127). M is an integer-valued double (`lp_m_full`): run in bits
 *   0..29, said (`LP_SAID_*`) 30..32, outcome 33..34, position 35..52.
 * - Engine Core `mantissa_pack` (`Laplace/engine/core/src/mantissa.c`):
 *   biased exponent 1023 (`0x3ff`, values in (-2, -1] U [1, 2)), where sign +
 *   52-bit mantissa holds 53 payload bits per coordinate (X: 53 hash bits, Y: 53
 *   hash bits, Z: 22 hash bits in low bits + 31 flag bits).
 */
export function unpackMantissaCoord(
  value: number,
  axis: 'x' | 'y' | 'z' | 'm',
): number {
  if (!Number.isFinite(value)) return 0;
  const buf = new ArrayBuffer(8);
  const view = new DataView(buf);
  view.setFloat64(0, value, false);
  const raw = view.getBigUint64(0, false);
  const exp = Number((raw >> 52n) & 0x7ffn);
  const frac = raw & 0x000fffffffffffffn;

  // Native M is not a mantissa carrier: it is an integer-valued double whose
  // low 30 bits are the run, then said (3), outcome (2) and position (18)
  // (`lp_m_run` / `lp_m_said` / `lp_m_outcome`, laplace.h). Display the run
  // share of its 30-bit field; use unpackPathM for the metadata itself.
  if (axis === 'm' && Number.isInteger(value) && value >= 1 && value < 2 ** 53) {
    const run30 = Number(BigInt(value) & ((1n << 30n) - 1n)) || 1;
    return (run30 / 1073741824) * 2 - 1; // 2^30
  }

  // Native lp_id_to_xyz carrier: exponent 0x3fd (1021).
  if (exp === 0x3fd && axis !== 'm') {
    if (axis === 'x' || axis === 'y') {
      const part43 = Number(frac & ((1n << 43n) - 1n));
      return (part43 / 8796093022208) * 2 - 1; // 2^43
    }
    const part42 = Number(frac & ((1n << 42n) - 1n));
    return (part42 / 4398046511104) * 2 - 1; // 2^42
  }

  // Engine Core mantissa_pack carrier: exponent 0x3ff (1023), sign bit = bit 52.
  if (exp === 0x3ff) {
    const signBit = (raw >> 63n) & 1n;
    const slot53 = (signBit << 52n) | frac;
    if (axis === 'z') {
      const hash22 = Number(slot53 & ((1n << 22n) - 1n));
      return (hash22 / 4194304) * 2 - 1; // 2^22
    }
    return (Number(slot53) / 9007199254740992) * 2 - 1; // 2^53
  }

  // Already decoded into [-1, 1] display space.
  return Math.max(-1, Math.min(1, value));
}

/**
 * Decode a Native path-vertex M (`lp_m_full`, laplace.h): run30 | said3 |
 * outcome2 | position18, low bits first. Anything that is not an integer in
 * [1, 2^53) decodes like `lp_m_bits`: run 1 and nothing else.
 */
export function unpackPathM(m: number): { run: number; said: number; outcome: number; position: number } {
  const bits = Number.isInteger(m) && m >= 1 && m < 2 ** 53 ? BigInt(m) : 1n;
  return {
    run: Number(bits & ((1n << 30n) - 1n)) || 1,
    said: Number((bits >> 30n) & 0x7n),
    outcome: Number((bits >> 33n) & 0x3n),
    position: Number((bits >> 35n) & ((1n << 18n) - 1n)),
  };
}

/**
 * Packed: BLAKE3 entity ID bit-unpacked from the IEEE-754 mantissas of X, Y, Z.
 * Each coordinate's mantissa slice is mapped linearly into [-SHELL, SHELL].
 * M carries the RLE run length / ordinal metadata, not a spatial coordinate.
 */
export function packedDisplayPos(n: GlomeNode): [number, number, number] {
  const ux = unpackMantissaCoord(n.x, 'x');
  const uy = unpackMantissaCoord(n.y, 'y');
  const uz = unpackMantissaCoord(n.z, 'z');
  return [ux * SHELL, uy * SHELL, uz * SHELL];
}

/**
 * Exact carrier bit-space view used by Storage Proof.
 * X/Y/Z have already been decoded from their 53-bit sign+mantissa payload slots
 * into [-1,1]. Keep those three axes independent instead of normalizing them
 * onto a shell, otherwise a second visualization transform would discard payload
 * magnitude. M remains metadata and is deliberately not a spatial axis here.
 */
export function carrierDisplayPos(n: GlomeNode): [number, number, number] {
  return [
    Math.max(-1, Math.min(1, n.x)) * SHELL,
    Math.max(-1, Math.min(1, n.y)) * SHELL,
    Math.max(-1, Math.min(1, n.z)) * SHELL,
  ];
}

/**
 * Placement is a 3-D view of the PointZM point in the closed 4-ball, not a flat XYZ slice.
 *
 * The 4-D point is rotated through the X-M and Z-M planes so M shows in both screen
 * width and camera depth. When radius_origin differs from the 4-norm of (x, y, z, m),
 * the 4-vector is scaled by radius4 / norm4 before projecting (xmX, y, zmZ), preserving
 * true 4-D orthographic projection and centroid linearity while keeping interior depth
 * in the ball visible.
 */
export function placementBallPos(
  n: GlomeNode,
  xmAngle = 0,
  zmAngle = 0,
): [number, number, number] {
  const x = Number.isFinite(n.x) ? n.x : 0;
  const y = Number.isFinite(n.y) ? n.y : 0;
  const z = Number.isFinite(n.z) ? n.z : 0;
  const m = n.m != null && Number.isFinite(n.m) ? n.m : 0;

  const cx = Math.cos(xmAngle);
  const sx = Math.sin(xmAngle);
  const xmX = x * cx - m * sx;
  const xmM = x * sx + m * cx;

  const cz = Math.cos(zmAngle);
  const sz = Math.sin(zmAngle);
  const zmZ = z * cz - xmM * sz;
  const zmM = z * sz + xmM * cz;

  const norm4 = Math.hypot(xmX, y, zmZ, zmM);
  if (norm4 <= Number.EPSILON) return [0, 0, 0];

  const fallbackRadius = Math.min(1, norm4);
  const radius4 = Number.isFinite(n.radius) && n.radius >= 0
    ? Math.min(1, n.radius)
    : fallbackRadius;
  const displayRadius = SHELL * radius4;
  const scale = displayRadius / norm4;
  return [xmX * scale, y * scale, zmZ * scale];
}

/** Display position of a node under the chosen projection. */
export function project(
  n: GlomeNode,
  mode: GlomeProjection,
  xmAngle: number,
  zmAngle: number,
): [number, number, number] {
  return mode === 'packed'
    ? packedDisplayPos(n)
    : mode === 'carrier'
      ? carrierDisplayPos(n)
      : placementBallPos(n, xmAngle, zmAngle);
}

export function physicalitiesToNodes(
  physicalities: ExplorePhysicalityRow[],
  label: string,
  idHex: string,
): GlomeNode[] {
  return physicalities
    .filter((p) => Number.isFinite(p.x))
    .map((p, i) => ({
      id: i === 0 ? idHex : `${idHex}-${i}`,
      label: i === 0 ? label : `${label} · phys ${i}`,
      x: p.x,
      y: p.y,
      z: p.z,
      m: p.m,
      radius: p.radius,
      kind: 'primary' as const,
    }));
}
