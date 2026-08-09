import { Suspense, useEffect, useMemo, useState } from 'react';
import { Muted } from '@ui';
import { Canvas, useThree } from '@react-three/fiber';
import { Html, Line, OrbitControls } from '@react-three/drei';
import type { ExplorePhysicalityRow } from '../types';
import { useDeferredWebGlMount } from '../useDeferredWebGlMount';
import styles from './GlomeCanvas.module.css';

export type GlomeProjection = 'packed' | 'placement';

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
  kind?: 'primary' | 'constituent' | 'neighbor' | 'walk' | 'peer';
  /** Optional paint override (Packed ordinal ramp). */
  color?: string;
}

const MAX_NODES = 500;
const SHELL = 0.85;

/** Packed: hash-XYZ on a display shell. M is paint, not an axis. */
export function packedDisplayPos(n: GlomeNode): [number, number, number] {
  const len = Math.hypot(n.x, n.y, n.z) || 1;
  return [(n.x / len) * SHELL, (n.y / len) * SHELL, (n.z / len) * SHELL];
}

/**
 * Placement: an orthographic projection of the actual 4D point after an X-M
 * plane rotation. It retains the projected XYZ magnitude instead of normalizing
 * every point onto a shell, and makes M observable. The former projection discarded M, normalized XYZ, then restored
 * radius_origin; every point on the same XYZ ray therefore collapsed onto one
 * display ray even when its fourth coordinate differed.
 */
export function placementBallPos(n: GlomeNode, xmAngle = 0): [number, number, number] {
  const c = Math.cos(xmAngle);
  const s = Math.sin(xmAngle);
  const x = Number.isFinite(n.x) ? n.x : 0;
  const y = Number.isFinite(n.y) ? n.y : 0;
  const z = Number.isFinite(n.z) ? n.z : 0;
  const m = n.m != null && Number.isFinite(n.m) ? n.m : 0;
  const rotatedX = x * c - m * s;
  return [rotatedX * SHELL, y * SHELL, z * SHELL];
}

function project(n: GlomeNode, mode: GlomeProjection, xmAngle: number): [number, number, number] {
  return mode === 'packed' ? packedDisplayPos(n) : placementBallPos(n, xmAngle);
}

/** Demand-mode: redraw when node set changes; OrbitControls still invalidates on input. */
function InvalidateOnData({ revision }: { revision: string }) {
  const invalidate = useThree((s) => s.invalidate);
  useEffect(() => {
    invalidate();
  }, [revision, invalidate]);
  return null;
}

function ContextLossGuard() {
  const gl = useThree((s) => s.gl);
  useEffect(() => {
    const canvas = gl.domElement;
    const onLost = (e: Event) => {
      e.preventDefault();
    };
    canvas.addEventListener('webglcontextlost', onLost, false);
    return () => {
      canvas.removeEventListener('webglcontextlost', onLost, false);
      try {
        gl.dispose();
      } catch {
        /* already gone */
      }
    };
  }, [gl]);
  return null;
}

function GlomeScene({
  nodes,
  trajectory,
  highlightIds,
  highlightOrdinal,
  projection,
  xmAngle,
  revision,
  onSelectOrdinal,
}: {
  nodes: GlomeNode[];
  trajectory: [number, number, number][];
  highlightIds: Set<string>;
  highlightOrdinal: number | null;
  projection: GlomeProjection;
  xmAngle: number;
  revision: string;
  onSelectOrdinal?: (ordinal: number | null) => void;
}) {
  const [hover, setHover] = useState<GlomeNode | null>(null);
  const limited = nodes.slice(0, MAX_NODES);

  return (
    <>
      <ContextLossGuard />
      <InvalidateOnData revision={revision} />
      <ambientLight intensity={0.55} />
      <pointLight position={[4, 4, 4]} intensity={1.2} />
      {limited.map((n) => {
        const pos = project(n, projection, xmAngle);
        const ordHit = highlightOrdinal != null && n.ordinal === highlightOrdinal;
        const color =
          n.color
            ?? (n.kind === 'walk' ? '#3ecf8e'
              : n.kind === 'neighbor' ? '#e8b339'
                : n.kind === 'constituent' ? '#9b7bff'
                  : ordHit || highlightIds.has(n.id) ? '#3ecf8e' : '#4f8cff');
        return (
          <mesh
            key={n.id}
            position={pos}
            onPointerOver={(e) => { e.stopPropagation(); setHover(n); }}
            onPointerOut={() => setHover(null)}
            onClick={(e) => {
              e.stopPropagation();
              onSelectOrdinal?.(n.ordinal ?? null);
            }}
          >
            <sphereGeometry args={[Math.max(0.02, (ordHit ? 0.045 : 0.028) + (n.runLength ?? 1) * 0.002), 12, 12]} />
            <meshStandardMaterial
              color={color}
              emissive={color}
              emissiveIntensity={ordHit ? 0.55 : 0.25}
            />
          </mesh>
        );
      })}
      {trajectory.length > 1 ? (
        <Line
          points={trajectory}
          color={projection === 'packed' ? '#c084fc' : '#4f8cff'}
          lineWidth={1.25}
          transparent
          opacity={0.75}
        />
      ) : null}
      <mesh>
        <sphereGeometry args={[1, 32, 32]} />
        <meshBasicMaterial color="#243049" wireframe transparent opacity={0.15} />
      </mesh>
      {hover ? (
        <Html position={project(hover, projection, xmAngle).map((v) => v + 0.08) as [number, number, number]}>
          <div className={styles.tooltip}>
            <strong>{hover.label}</strong>
            {hover.ordinal != null ? <span> · ord {hover.ordinal}</span> : null}
            {hover.runLength != null && hover.runLength > 1 ? <span> · rle {hover.runLength}</span> : null}
            {hover.mu != null ? <span> · μ {hover.mu.toFixed(1)}</span> : null}
            {projection === 'placement' && Number.isFinite(hover.radius)
              ? <span> · r {hover.radius.toFixed(3)}</span>
              : null}
          </div>
        </Html>
      ) : null}
      <OrbitControls enablePan enableZoom makeDefault />
    </>
  );
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

/** Ordinal → hue for Packed paint (M/ordinal channel). */
export function ordinalColor(ordinal: number, maxOrdinal: number): string {
  const t = maxOrdinal <= 1 ? 0 : (ordinal - 1) / (maxOrdinal - 1);
  const h = Math.round(260 - t * 140);
  return `hsl(${h} 70% 62%)`;
}

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
  const [xmDegrees, setXmDegrees] = useState(35);
  const xmAngle = xmDegrees * Math.PI / 180;
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

  const trajectory = useMemo(() => {
    if (trajectoryPoints) return trajectoryPoints;
    return nodes
      .filter((n) => n.kind === 'constituent')
      .slice()
      .sort((a, b) => (a.ordinal ?? 0) - (b.ordinal ?? 0))
      .map((n) => project(n, projection, xmAngle));
  }, [nodes, trajectoryPoints, projection, xmAngle]);

  const highlights = useMemo(() => new Set(highlightIds), [highlightIds]);
  const revision = useMemo(
    () => `${projection}:${xmDegrees}:${nodes.length}:${highlightOrdinal}:${nodes.map((n) => n.id).join(',')}`,
    [nodes, projection, xmDegrees, highlightOrdinal],
  );

  if (nodes.length === 0) {
    return <div className={styles.empty}>{emptyLabel}</div>;
  }

  return (
    <div className={fill ? `${styles.root} ${styles.rootFill}` : styles.root}>
      {nodes.length > MAX_NODES ? (
        <Muted className={styles.cap}>Showing {MAX_NODES} of {nodes.length} nodes</Muted>
      ) : null}
      {projection === 'placement' ? (
        <label className={styles.rotationControl}>
          <span>X–M rotation</span>
          <input
            type="range"
            min="-180"
            max="180"
            step="1"
            value={xmDegrees}
            onChange={(event) => setXmDegrees(Number(event.target.value))}
          />
          <output>{xmDegrees}°</output>
        </label>
      ) : null}
      <div className={fill ? `${styles.canvas} ${styles.canvasFill}` : styles.canvas}>
        {webGlReady ? (
          <Canvas
            frameloop="demand"
            dpr={[1, 1.5]}
            camera={{ position: [0, 0, 2.2], fov: 50 }}
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
                revision={revision}
                onSelectOrdinal={onSelectOrdinal}
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
