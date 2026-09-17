import { useEffect, useState } from 'react';

export interface VisualizationPalette {
  background: string;
  primary: string;
  muted: string;
  signal: string;
  steel: string;
  error: string;
}

const FALLBACK: VisualizationPalette = {
  background: '#0a2638',
  primary: '#eef6f9',
  muted: '#b9cad4',
  signal: '#69d9d1',
  steel: '#8fc4e2',
  error: '#ff8f9b',
};

function token(name: string, fallback: string): string {
  if (typeof document === 'undefined' || typeof getComputedStyle === 'undefined') return fallback;
  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return value || fallback;
}

/**
 * Three.js and force-graph cannot consume CSS custom properties directly. Resolve
 * the visualization-specific UI tokens once at the renderer boundary instead of
 * carrying a second hard-coded purple/yellow/blue palette in WebGL code.
 */
export function visualizationPalette(): VisualizationPalette {
  return {
    background: token('--viz-bg', FALLBACK.background),
    primary: token('--viz-text', FALLBACK.primary),
    muted: token('--viz-muted', FALLBACK.muted),
    signal: token('--viz-signal', FALLBACK.signal),
    steel: token('--viz-steel', FALLBACK.steel),
    error: token('--viz-error', FALLBACK.error),
  };
}

function hexRgb(hex: string): [number, number, number] | null {
  const value = hex.trim();
  if (!/^#[0-9a-f]{6}$/i.test(value)) return null;
  return [
    Number.parseInt(value.slice(1, 3), 16),
    Number.parseInt(value.slice(3, 5), 16),
    Number.parseInt(value.slice(5, 7), 16),
  ];
}

export function rgba(color: string, alpha: number): string {
  const rgb = hexRgb(color);
  if (!rgb) return color;
  return `rgba(${rgb[0]}, ${rgb[1]}, ${rgb[2]}, ${Math.min(1, Math.max(0, alpha))})`;
}

export function lerpColor(a: string, b: string, t: number): string {
  const left = hexRgb(a);
  const right = hexRgb(b);
  if (!left || !right) return t < 0.5 ? a : b;
  const p = Math.min(1, Math.max(0, t));
  const channel = (i: number) => Math.round(left[i] + (right[i] - left[i]) * p);
  return `#${[channel(0), channel(1), channel(2)]
    .map((v) => v.toString(16).padStart(2, '0'))
    .join('')}`;
}


function paletteEqual(a: VisualizationPalette, b: VisualizationPalette): boolean {
  return a.background === b.background
    && a.primary === b.primary
    && a.muted === b.muted
    && a.signal === b.signal
    && a.steel === b.steel
    && a.error === b.error;
}

/**
 * Keep WebGL renderers bound to live CSS tokens across system-theme changes and
 * explicit application appearance attributes instead of snapshotting once at mount.
 */
export function useVisualizationPalette(): VisualizationPalette {
  const [palette, setPalette] = useState<VisualizationPalette>(() => visualizationPalette());

  useEffect(() => {
    if (typeof window === 'undefined' || typeof document === 'undefined') return undefined;
    let frame = 0;
    const refresh = () => {
      window.cancelAnimationFrame(frame);
      frame = window.requestAnimationFrame(() => {
        const next = visualizationPalette();
        setPalette((current) => (paletteEqual(current, next) ? current : next));
      });
    };

    const media = window.matchMedia('(prefers-color-scheme: dark)');
    media.addEventListener('change', refresh);
    const observer = new MutationObserver(refresh);
    observer.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ['class', 'style', 'data-theme', 'data-appearance'],
    });
    window.addEventListener('pageshow', refresh);
    refresh();

    return () => {
      window.cancelAnimationFrame(frame);
      media.removeEventListener('change', refresh);
      observer.disconnect();
      window.removeEventListener('pageshow', refresh);
    };
  }, []);

  return palette;
}

function relativeLuminance(rgb: [number, number, number]): number {
  const component = (value: number) => {
    const channel = value / 255;
    return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * component(rgb[0]) + 0.7152 * component(rgb[1]) + 0.0722 * component(rgb[2]);
}

export function visualizationContrastRatio(foreground: string, background: string): number {
  const fg = hexRgb(foreground);
  const bg = hexRgb(background);
  if (!fg || !bg) return 1;
  const lighter = Math.max(relativeLuminance(fg), relativeLuminance(bg));
  const darker = Math.min(relativeLuminance(fg), relativeLuminance(bg));
  return (lighter + 0.05) / (darker + 0.05);
}

/** WCAG non-text graphical-object contrast floor. */
export function ensureVisualizationContrast(
  color: string,
  background: string,
  fallback: string,
  minimum = 3,
): string {
  return visualizationContrastRatio(color, background) >= minimum ? color : fallback;
}
