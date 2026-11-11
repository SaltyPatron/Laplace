/** 2-D canvas renderer of the consensus web, lazily loading react-force-graph-2d. */
import { lazy, Suspense, type ComponentType, type MutableRefObject } from 'react';
import { Muted } from '@ui';
import { rgba, type VisualizationPalette } from '../visualizationPalette';
import { binding, witnessMass, type GraphData, type WebEdge, type WebNode } from './graphData';
import { hopColor, nodeTooltip, strandTooltip, type NodeVisual } from './graphVisuals';

const ForceGraph2D = lazy(() => import('react-force-graph-2d').then((m) => ({ default: m.default }))) as unknown as ComponentType<any>;

export type Fg2dApi = {
  zoomToFit: (ms?: number) => void;
  d3Force?: (name: string, force?: unknown) => unknown;
};

export function Graph2D({
  graphRef,
  size,
  data,
  palette,
  nodeVisuals,
  labelledIds,
  centerId,
  onNodeClick,
}: {
  graphRef: MutableRefObject<Fg2dApi | null>;
  size: { width: number; height: number };
  data: GraphData;
  palette: VisualizationPalette;
  nodeVisuals: Map<string, NodeVisual>;
  labelledIds: Set<string>;
  centerId: string;
  onNodeClick: (n: WebNode) => void;
}) {
  return (
    <Suspense fallback={<Muted>Loading 2-D renderer…</Muted>}>
    <ForceGraph2D
      ref={graphRef as never}
      width={size.width}
      height={size.height}
      graphData={data}
      backgroundColor={palette.background}
      enablePanInteraction
      enableZoomInteraction
      nodeLabel={(n: WebNode) => nodeTooltip(n, nodeVisuals)}
      linkLabel={(l: WebEdge) => strandTooltip(l)}
      linkWidth={(l: WebEdge) => 0.3 + binding(l.weight) * 1.25 + witnessMass(l.witnesses) * 0.65}
      linkColor={(l: WebEdge) => {
        if (l.walk) return palette.signal;
        if (l.refuted || l.weight < 0) return palette.error;
        return rgba(palette.steel, 0.22 + 0.72 * binding(l.weight));
      }}
      onNodeClick={(n: WebNode) => onNodeClick(n)}
      nodeCanvasObject={(node: WebNode, ctx: CanvasRenderingContext2D, globalScale: number) => {
        // World-space radius (not /globalScale) — zoom-in reveals gaps instead of ballooning.
        const visual = nodeVisuals.get(node.id);
        const r = 1.7 + Math.cbrt(Math.max(1, visual?.val ?? 1)) * 1.15;
        const x = (node as WebNode & { x: number; y: number }).x;
        const y = (node as WebNode & { x: number; y: number }).y;
        ctx.beginPath();
        ctx.arc(x, y, r, 0, 2 * Math.PI);
        ctx.fillStyle = visual?.color ?? hopColor(node.hop, node.walk || node.id === centerId, palette);
        ctx.fill();
        if (labelledIds.has(node.id) || globalScale >= 2.5) {
          const label = node.label.length > 22 ? `${node.label.slice(0, 21)}…` : node.label;
          const fontSize = 11 / globalScale;
          ctx.font = `${node.id === centerId ? '600 ' : ''}${fontSize}px sans-serif`;
          ctx.lineWidth = 3 / globalScale;
          ctx.strokeStyle = palette.background;
          ctx.strokeText(label, x + r + 1.2, y + fontSize * 0.35);
          ctx.fillStyle = node.id === centerId ? palette.primary : palette.muted;
          ctx.fillText(label, x + r + 1.2, y + fontSize * 0.35);
        }
      }}
      nodePointerAreaPaint={(node: WebNode, color: string, ctx: CanvasRenderingContext2D) => {
        const r = node.hop === 0 ? 4 : 3;
        const x = (node as WebNode & { x: number; y: number }).x;
        const y = (node as WebNode & { x: number; y: number }).y;
        ctx.beginPath();
        ctx.arc(x, y, r, 0, 2 * Math.PI);
        ctx.fillStyle = color;
        ctx.fill();
      }}
      cooldownTicks={100}
      d3AlphaDecay={0.03}
      d3VelocityDecay={0.3}
    />
    </Suspense>
  );
}
