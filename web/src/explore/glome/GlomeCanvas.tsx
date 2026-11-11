import { Suspense, useEffect, useMemo, useState } from 'react';
import { Canvas } from '@react-three/fiber';
import type { ExplorePhysicalityRow } from '../types';
import { useDeferredWebGlMount } from '../useDeferredWebGlMount';
import { useVisualizationPalette } from '../visualizationPalette';
import { GlomeRotationControls, GlomeViewControls } from './GlomeControls';
import { glomePalette } from './glomePalette';
import {
  CAMERA_START, physicalitiesToNodes, project,
  type GlomeColorBy, type GlomeNode, type GlomeProjection,
} from './glomeProjection';
import { GlomeScene } from './GlomeScene';
import styles from './GlomeCanvas.module.css';

// The glome's model, decoders and colours live in sibling modules; this file stays
// the public entry point, so existing imports of GlomeCanvas keep working.
export {
  carrierDisplayPos,
  packedDisplayPos,
  physicalitiesToNodes,
  placementBallPos,
  readableGlomeLabel,
  unpackMantissaCoord,
  unpackPathM,
  type GlomeColorBy,
  type GlomeNode,
  type GlomeProjection,
} from './glomeProjection';
export { ordinalColor } from './glomePalette';

export function GlomeCanvas({
  nodes,
  trajectoryPoints,
  highlightIds = [],
  highlightOrdinal = null,
  projection = 'placement',
  fill = false,
  note,
  emptyLabel = 'No coordinates to render.',
  staggerMs = 0,
  onSelectOrdinal,
}: {
  nodes: GlomeNode[];
  trajectoryPoints?: [number, number, number][];
  highlightIds?: string[];
  highlightOrdinal?: number | null;
  projection?: GlomeProjection;
  fill?: boolean;
  note?: string;
  emptyLabel?: string;
  /** Extra delay before mounting WebGL (second pane). */
  staggerMs?: number;
  onSelectOrdinal?: (ordinal: number | null) => void;
}) {
  const baseReady = useDeferredWebGlMount(nodes.length > 0);
  const [staggerReady, setStaggerReady] = useState(staggerMs <= 0);
  const hasTier = nodes.some((n) => n.tier != null);
  const hasParents = nodes.some((n) => n.parentId != null);
  const hasTrajectory = useMemo(
    () => (trajectoryPoints ? trajectoryPoints.length > 1 : nodes.filter((n) => n.kind === 'constituent' && n.ordinal != null).length > 1),
    [nodes, trajectoryPoints],
  );
  const [colorBy, setColorBy] = useState<GlomeColorBy>(hasTier ? 'tier' : 'kind');
  const [showTrajectory, setShowTrajectory] = useState(true);
  const [showEdges, setShowEdges] = useState(true);
  const [showLabels, setShowLabels] = useState(true);
  const [xmDegrees, setXmDegrees] = useState(35);
  const [zmDegrees, setZmDegrees] = useState(25);
  const xmAngle = xmDegrees * Math.PI / 180;
  const zmAngle = zmDegrees * Math.PI / 180;
  const sharedPalette = useVisualizationPalette();
  const palette = useMemo(() => glomePalette(sharedPalette), [sharedPalette]);

  useEffect(() => {
    if (!baseReady || staggerMs <= 0) {
      setStaggerReady(staggerMs <= 0 ? baseReady : false);
      return;
    }
    setStaggerReady(false);
    const t = window.setTimeout(() => setStaggerReady(true), staggerMs);
    return () => window.clearTimeout(t);
  }, [baseReady, staggerMs]);
  const webGlReady = baseReady && staggerReady;

  // Parent-to-child composition spokes connect each child to its Merkle parent centroid.
  const edges = useMemo(() => {
    if (!hasParents || !showEdges) return [] as [number, number, number][];
    const byId = new Map(nodes.map((n) => [n.id, n]));
    const out: [number, number, number][] = [];
    for (const n of nodes) {
      const parent = n.parentId ? byId.get(n.parentId) : undefined;
      if (!parent) continue;
      out.push(project(parent, projection, xmAngle, zmAngle), project(n, projection, xmAngle, zmAngle));
    }
    return out;
  }, [nodes, hasParents, showEdges, projection, xmAngle, zmAngle]);

  // Sequential trajectory path (v_1 -> v_2 -> ... -> v_k) through the ordered constituents.
  const trajectory = useMemo(() => {
    if (!showTrajectory) return [] as [number, number, number][];
    if (trajectoryPoints) return trajectoryPoints;
    return nodes
      .filter((n) => n.kind === 'constituent' && n.ordinal != null)
      .slice()
      .sort((a, b) => (a.ordinal ?? 0) - (b.ordinal ?? 0))
      .map((n) => project(n, projection, xmAngle, zmAngle));
  }, [nodes, trajectoryPoints, showTrajectory, projection, xmAngle, zmAngle]);

  const highlights = useMemo(() => new Set(highlightIds), [highlightIds]);
  const revision = useMemo(
    () => `${projection}:${xmDegrees}:${zmDegrees}:${palette.background}:${palette.primary}:${nodes.length}:${highlightOrdinal}:${colorBy}:${showTrajectory}:${showEdges}:${showLabels}:${nodes.map((n) => n.id).join(',')}`,
    [nodes, projection, xmDegrees, zmDegrees, palette, highlightOrdinal, colorBy, showTrajectory, showEdges, showLabels],
  );

  if (nodes.length === 0) {
    return <div className={styles.empty}>{emptyLabel}</div>;
  }

  return (
    <div className={fill ? `${styles.root} ${styles.rootFill}` : styles.root}>
      <GlomeViewControls
        colorBy={colorBy}
        onColorBy={setColorBy}
        hasTier={hasTier}
        hasTrajectory={hasTrajectory}
        hasParents={hasParents}
        showTrajectory={showTrajectory}
        onShowTrajectory={setShowTrajectory}
        showEdges={showEdges}
        onShowEdges={setShowEdges}
        showLabels={showLabels}
        onShowLabels={setShowLabels}
      />
      {projection === 'placement' ? (
        <GlomeRotationControls
          xmDegrees={xmDegrees}
          onXmDegrees={setXmDegrees}
          zmDegrees={zmDegrees}
          onZmDegrees={setZmDegrees}
        />
      ) : null}
      <div className={fill ? `${styles.canvas} ${styles.canvasFill}` : styles.canvas}>
        {webGlReady ? (
          <Canvas
            frameloop="demand"
            dpr={[1, 1.5]}
            camera={{ position: [0, 0, CAMERA_START], fov: 50, near: 0.00005, far: 50 }}
            gl={{
              antialias: true,
              powerPreference: 'default',
              failIfMajorPerformanceCaveat: false,
              preserveDrawingBuffer: false,
            }}
          >
            <Suspense fallback={null}>
              <GlomeScene
                nodes={nodes}
                trajectory={trajectory}
                highlightIds={highlights}
                highlightOrdinal={highlightOrdinal}
                projection={projection}
                xmAngle={xmAngle}
                zmAngle={zmAngle}
                palette={palette}
                revision={revision}
                onSelectOrdinal={onSelectOrdinal}
                colorBy={colorBy}
                edges={edges}
                showLabels={showLabels}
              />
            </Suspense>
          </Canvas>
        ) : null}
      </div>
      {note ? <p className={styles.note}>{note}</p> : null}
    </div>
  );
}

export function GlomeCanvasFromPhysicalities({
  physicalities,
  label,
  idHex,
  extraNodes,
  highlightIds,
  fill = false,
}: {
  physicalities: ExplorePhysicalityRow[];
  label: string;
  idHex: string;
  extraNodes?: GlomeNode[];
  highlightIds?: string[];
  fill?: boolean;
}) {
  const nodes = useMemo(() => {
    const base = physicalitiesToNodes(physicalities, label, idHex);
    return [...base, ...(extraNodes ?? [])];
  }, [physicalities, label, idHex, extraNodes]);
  return (
    <GlomeCanvas
      nodes={nodes}
      highlightIds={highlightIds}
      fill={fill}
      projection="placement"
      note="Live placement on the glome ball — not semantic embedding."
    />
  );
}
