/** Glome colours, derived from the shared visualization palette so both themes stay legible. */
import { ensureVisualizationContrast, lerpColor, visualizationPalette, type VisualizationPalette } from '../visualizationPalette';

export interface GlomePalette {
  background: string;
  primary: string;
  walk: string;
  neighbor: string;
  constituent: string;
  highlight: string;
  packedLine: string;
  placementLine: string;
  wireframe: string;
  reference: string;
}

export function glomePalette(shared: VisualizationPalette): GlomePalette {
  const visible = (color: string) =>
    ensureVisualizationContrast(color, shared.background, shared.primary);
  return {
    background: shared.background,
    primary: visible(shared.steel),
    walk: visible(shared.signal),
    neighbor: visible(shared.error),
    constituent: visible(lerpColor(shared.steel, shared.error, 0.55)),
    highlight: visible(shared.primary),
    packedLine: visible(lerpColor(shared.steel, shared.signal, 0.5)),
    placementLine: visible(shared.steel),
    wireframe: visible(shared.muted),
    reference: visible(shared.primary),
  };
}

/** A metric in [0,1] on the shared steel → signal → error ramp. */
export function metricColor(t: number, palette: GlomePalette): string {
  const x = Math.max(0, Math.min(1, t));
  return x < 0.5
    ? lerpColor(palette.primary, palette.walk, x * 2)
    : lerpColor(palette.walk, palette.neighbor, (x - 0.5) * 2);
}

/** Ordinal → the shared steel→signal visualization ramp for Packed paint. */
export function ordinalColor(
  ordinal: number,
  maxOrdinal: number,
  palette: VisualizationPalette = visualizationPalette(),
): string {
  const t = maxOrdinal <= 1 ? 0 : (ordinal - 1) / (maxOrdinal - 1);
  return lerpColor(palette.steel, palette.signal, t);
}
