/** 3-D (belief / force) renderer of the consensus web, lazily loading react-force-graph-3d. */
import { lazy, Suspense, type ComponentType, type MutableRefObject } from 'react';
import { Muted } from '@ui';
import { Object3D, type Camera, type Sprite, type SpriteMaterial, type Vector3 } from 'three';
import { rgba, type VisualizationPalette } from '../visualizationPalette';
import { binding, NODE_REL_SIZE, witnessMass, type GraphData, type WebEdge, type WebNode } from './graphData';
import { labelSprite, nodeTooltip, strandTooltip, type NodeVisual } from './graphVisuals';

const ForceGraph3D = lazy(() => import('react-force-graph-3d').then((m) => ({ default: m.default }))) as unknown as ComponentType<any>;

export type Fg3dApi = {
  zoomToFit: (ms?: number, padding?: number) => void;
  cameraPosition: (pos: { x: number; y: number; z: number }, lookAt?: { x: number; y: number; z: number }, ms?: number) => void;
  camera: () => Camera;
  d3Force: (name: string, force?: unknown) => unknown;
  pauseAnimation?: () => void;
  resumeAnimation?: () => void;
  _destructor?: () => void;
  controls: () => {
    target: Vector3;
    update?: () => void;
    enablePan?: boolean;
    screenSpacePanning?: boolean;
    panSpeed?: number;
    mouseButtons?: { LEFT?: number; MIDDLE?: number; RIGHT?: number };
    listenToKeyEvents?: (dom: HTMLElement | Window) => void;
    noPan?: boolean;
  };
};

export function Graph3D({
  graphRef,
  size,
  data,
  palette,
  nodeVisuals,
  labelledIds,
  centerId,
  labelCache,
  labelMaterials,
  beliefMode,
  onNodeClick,
}: {
  graphRef: MutableRefObject<Fg3dApi | null>;
  size: { width: number; height: number };
  data: GraphData;
  palette: VisualizationPalette;
  nodeVisuals: Map<string, NodeVisual>;
  labelledIds: Set<string>;
  centerId: string;
  labelCache: MutableRefObject<Map<string, Sprite>>;
  labelMaterials: MutableRefObject<Set<SpriteMaterial>>;
  beliefMode: boolean;
  onNodeClick: (n: WebNode) => void;
}) {
  return (
    <Suspense fallback={<Muted>Loading 3-D renderer…</Muted>}>
    <ForceGraph3D
      ref={graphRef as never}
      width={size.width}
      height={size.height}
      graphData={data}
      backgroundColor={palette.background}
      showNavInfo
      controlType="orbit"
      enableNavigationControls
      linkDirectionalParticles={0}
      linkOpacity={0.45}
      rendererConfig={{
        antialias: data.nodes.length < 512,
        powerPreference: 'high-performance',
        failIfMajorPerformanceCaveat: false,
      }}
      linkWidth={(l: WebEdge) => 0.05 + binding(l.weight) * 0.48 + witnessMass(l.witnesses) * 0.22}
      linkColor={(l: WebEdge) => {
        if (l.walk) return palette.signal;
        if (l.refuted || l.weight < 0) return rgba(palette.error, 0.42 + 0.42 * Math.abs(l.weight));
        return rgba(palette.steel, 0.14 + 0.8 * binding(l.weight));
      }}
      nodeLabel={(n: WebNode) => nodeTooltip(n, nodeVisuals)}
      linkLabel={(l: WebEdge) => strandTooltip(l)}
      nodeRelSize={1.55}
      nodeVal={(n: WebNode) => nodeVisuals.get(n.id)?.val ?? 1}
      nodeColor={(n: WebNode) => nodeVisuals.get(n.id)?.color ?? palette.muted}
      nodeThreeObjectExtend
      nodeThreeObject={(n: WebNode) => {
        const root = new Object3D();
        if (labelledIds.has(n.id)) {
          const label = n.label.length > 22 ? `${n.label.slice(0, 21)}…` : n.label;
          const sprite = labelSprite(
            label,
            n.id === centerId ? palette.primary : palette.muted,
            palette.background,
            labelCache.current,
            labelMaterials.current,
          );
          if (sprite) {
            const visual = nodeVisuals.get(n.id);
            const radius = 1.55 * Math.cbrt(Math.max(1, visual?.val ?? 1));
            sprite.position.set(0, radius + NODE_REL_SIZE * 2.4, 0);
            root.add(sprite);
          }
        }
        return root;
      }}
      onNodeClick={(n: WebNode) => onNodeClick(n)}
      onNodeDragEnd={(n: WebNode) => {
        // Pin after drag so layout doesn't yank the new origin back.
        (n as WebNode & { fx?: number; fy?: number; fz?: number }).fx = n.x;
        (n as WebNode & { fx?: number; fy?: number; fz?: number }).fy = n.y;
        (n as WebNode & { fx?: number; fy?: number; fz?: number }).fz = n.z;
      }}
      cooldownTicks={beliefMode ? 0 : 120}
      d3AlphaDecay={0.028}
      d3VelocityDecay={0.32}
    />
    </Suspense>
  );
}
