/** Consensus-graph presentation helpers: node colour/mass, 3-D label sprites and hover text. */
import { CanvasTexture, LinearFilter, Sprite, SpriteMaterial } from 'three';
import { ensureVisualizationContrast, lerpColor, rgba, type VisualizationPalette } from '../visualizationPalette';
import { relationWords } from '../../domain/Strand/StrandList';
import type { GraphData, WebEdge, WebNode } from './graphData';

export function hopColor(hop: number, walk: boolean | undefined, palette: VisualizationPalette): string {
  const colors = [palette.signal, palette.steel, palette.primary, palette.muted, palette.error];
  const candidate = walk ? palette.signal : colors[Math.min(Math.max(hop, 0), colors.length - 1)];
  return ensureVisualizationContrast(candidate, palette.background, palette.primary);
}

/**
 * 3-D node names are camera-facing sprites. Past MAX_VISIBLE_LABELS nodes, the center,
 * then walk nodes, then the heaviest by visual mass carry the labels.
 */
const LABEL_FONT_PX = 44;
export const MAX_VISIBLE_LABELS = 48;

export interface NodeVisual {
  color: string;
  val: number;
  support: number;
  opposition: number;
  witnesses: number;
}

function endpointId(endpoint: string | WebNode): string {
  return typeof endpoint === 'string' ? endpoint : endpoint.id;
}

/**
 * Nodes are not anonymous topology dots. Summarize the canonical signed Glicko
 * testimony touching each retained entity:
 *   hue  = positive support vs negative/refuted opposition
 *   mass = witness volume (log-scaled) plus standing magnitude
 *
 * This is presentation only. The belief coordinates still come from the native
 * normalized-Laplacian projection; witness counts are not multiplied back into
 * the Glicko edge weight.
 */
export function buildNodeVisuals(
  data: GraphData,
  centerId: string,
  palette: VisualizationPalette,
): Map<string, NodeVisual> {
  const raw = new Map<string, { support: number; opposition: number; witnesses: number }>();
  for (const node of data.nodes)
    raw.set(node.id, { support: 0, opposition: 0, witnesses: 0 });

  for (const edge of data.links) {
    const source = endpointId(edge.source as unknown as string | WebNode);
    const target = endpointId(edge.target as unknown as string | WebNode);
    const signed = Number.isFinite(edge.weight) ? edge.weight : 0;
    const opposition = edge.refuted || signed < 0 ? Math.abs(signed) : 0;
    const support = edge.refuted ? 0 : Math.max(0, signed);
    const witnesses = Number.isFinite(edge.witnesses) ? Math.max(0, edge.witnesses) : 0;
    for (const id of [source, target]) {
      const value = raw.get(id);
      if (!value) continue;
      value.support += support;
      value.opposition += opposition;
      value.witnesses += witnesses;
    }
  }

  const visuals = new Map<string, NodeVisual>();
  for (const node of data.nodes) {
    const value = raw.get(node.id) ?? { support: 0, opposition: 0, witnesses: 0 };
    const standing = value.support + value.opposition;
    const balance = standing > 0 ? (value.support - value.opposition) / standing : 0;
    const intensity = Math.min(1, standing / 3);
    let color = palette.muted;
    if (node.id === centerId || node.walk) {
      color = palette.signal;
    } else if (standing > 0 && balance < -0.08) {
      color = lerpColor(palette.steel, palette.error, Math.min(1, Math.abs(balance) * 0.55 + intensity * 0.45));
    } else if (standing > 0 && balance > 0.08) {
      color = lerpColor(palette.steel, palette.signal, Math.min(1, balance * 0.55 + intensity * 0.45));
    } else if (standing > 0) {
      color = palette.steel;
    }
    color = ensureVisualizationContrast(color, palette.background, palette.primary);

    const evidenceMass = Math.log2(value.witnesses + 1);
    const val = node.id === centerId
      ? 8
      : Math.max(1, Math.min(10, 1 + evidenceMass * 0.72 + Math.min(3, standing)));

    visuals.set(node.id, {
      color,
      val,
      support: value.support,
      opposition: value.opposition,
      witnesses: value.witnesses,
    });
  }
  return visuals;
}

/** Label height as a fraction of the viewport height. */
const LABEL_SCREEN_HEIGHT = 0.034;

export function labelSprite(
  text: string,
  color: string,
  background: string,
  cache: Map<string, Sprite>,
  materials: Set<SpriteMaterial>,
): Sprite | null {
  const key = `${text}\0${color}\0${background}`;
  const hit = cache.get(key);
  if (hit) return hit.clone() as Sprite;

  const canvas = document.createElement('canvas');
  const ctx = canvas.getContext('2d');
  if (!ctx) return null;
  const font = `${LABEL_FONT_PX}px system-ui, sans-serif`;
  ctx.font = font;
  const width = Math.ceil(ctx.measureText(text).width) + 16;
  canvas.width = width;
  canvas.height = Math.ceil(LABEL_FONT_PX * 1.4);
  ctx.font = font;
  ctx.textBaseline = 'middle';
  ctx.fillStyle = rgba(background, 0.78);
  ctx.fillRect(0, 0, canvas.width, canvas.height);
  ctx.fillStyle = color;
  ctx.fillText(text, 8, canvas.height / 2);

  const texture = new CanvasTexture(canvas);
  texture.minFilter = LinearFilter;
  // Labels keep one on-screen size at every zoom (sizeAttenuation off, scale in
  // viewport units), so zooming into a dense cluster separates names instead of enlarging them.
  const material = new SpriteMaterial({
    map: texture, transparent: true, depthWrite: false, sizeAttenuation: false,
  });
  materials.add(material);
  const sprite = new Sprite(material);
  sprite.center.set(0.5, 0);
  sprite.scale.set(LABEL_SCREEN_HEIGHT * (canvas.width / canvas.height), LABEL_SCREEN_HEIGHT, 1);
  cache.set(key, sprite);
  return sprite.clone() as Sprite;
}

export const hopWords = (hop: number) => (hop === 0 ? 'centre' : `${hop} ${hop === 1 ? 'hop' : 'hops'} out`);

export function strandTooltip(l: WebEdge): string {
  if (l.walk) return 'your walk';
  const parts = [relationWords(l.type)];
  if (l.witnesses > 0) parts.push(`${l.witnesses.toLocaleString()} ${l.witnesses === 1 ? 'witness' : 'witnesses'}`);
  if (l.rating) parts.push(`standing ${l.rating.toFixed(0)} ± ${(2 * l.rd).toFixed(0)}`);
  if (l.refuted) parts.push('refuted');
  else if (l.weight < 0) parts.push('opposed');
  return parts.join(' · ');
}

/** Hover text for a node: label, hop distance and witness volume. */
export function nodeTooltip(n: WebNode, visuals: Map<string, NodeVisual>): string {
  const visual = visuals.get(n.id);
  return visual
    ? `${n.label} · ${hopWords(n.hop)} · ${visual.witnesses.toLocaleString()} ${visual.witnesses === 1 ? 'witness' : 'witnesses'}`
    : `${n.label} · ${hopWords(n.hop)}`;
}
