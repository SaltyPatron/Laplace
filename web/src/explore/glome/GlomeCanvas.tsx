import { Suspense, useEffect, useMemo, useState } from 'react';
import { Muted } from '@ui';
import { Canvas, useThree } from '@react-three/fiber';
import { Html, Line, OrbitControls } from '@react-three/drei';
import type { ExplorePhysicalityRow } from '../types';
import { useDeferredWebGlMount } from '../useDeferredWebGlMount';
import styles from './GlomeCanvas.module.css';

export interface GlomeNode {
  id: string;
  label: string;
  x: number;
  y: number;
  z: number;
  radius: number;
  mu?: number;
  kind?: 'primary' | 'constituent' | 'neighbor' | 'walk' | 'peer';
}

const MAX_NODES = 500;

function spherePos(n: GlomeNode): [number, number, number] {
  const len = Math.hypot(n.x, n.y, n.z) || 1;
  const s = 0.85 * Math.max(0.5, n.radius);
  return [(n.x / len) * s, (n.y / len) * s, (n.z / len) * s];
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
  revision,
}: {
  nodes: GlomeNode[];
  trajectory: [number, number, number][];
  highlightIds: Set<string>;
  revision: string;
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
        const pos = spherePos(n);
        const color =
          n.kind === 'walk' ? '#3ecf8e'
            : n.kind === 'neighbor' ? '#e8b339'
              : n.kind === 'constituent' ? '#9b7bff'
                : highlightIds.has(n.id) ? '#3ecf8e' : '#4f8cff';
        return (
          <mesh
            key={n.id}
            position={pos}
            onPointerOver={(e) => { e.stopPropagation(); setHover(n); }}
            onPointerOut={() => setHover(null)}
          >
            <sphereGeometry args={[Math.max(0.025, n.radius * 0.06), 12, 12]} />
            <meshStandardMaterial color={color} emissive={color} emissiveIntensity={0.25} />
          </mesh>
        );
      })}
      {trajectory.length > 1 ? (
        <Line points={trajectory} color="#4f8cff" lineWidth={1} transparent opacity={0.7} />
      ) : null}
      <mesh>
        <sphereGeometry args={[1, 32, 32]} />
        <meshBasicMaterial color="#243049" wireframe transparent opacity={0.15} />
      </mesh>
      {hover ? (
        <Html position={spherePos(hover).map((v) => v + 0.08) as [number, number, number]}>
          <div className={styles.tooltip}>
            <strong>{hover.label}</strong>
            {hover.mu != null ? <span> μ {hover.mu.toFixed(1)}</span> : null}
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
      radius: p.radius,
      kind: 'primary' as const,
    }));
}

export function GlomeCanvas({
  nodes,
  trajectoryPoints,
  highlightIds = [],
  fill = false,
}: {
  nodes: GlomeNode[];
  trajectoryPoints?: [number, number, number][];
  highlightIds?: string[];
  /** Stretch to fill a flex parent instead of a fixed viewport box. */
  fill?: boolean;
}) {
  const webGlReady = useDeferredWebGlMount(nodes.length > 0);
  const trajectory = useMemo(
    () => trajectoryPoints ?? nodes.filter((n) => n.kind === 'constituent').map((n) => spherePos(n)),
    [nodes, trajectoryPoints],
  );
  const highlights = useMemo(() => new Set(highlightIds), [highlightIds]);
  const revision = useMemo(
    () => `${nodes.length}:${nodes.map((n) => n.id).join(',')}`,
    [nodes],
  );

  if (nodes.length === 0) {
    return <div className={styles.empty}>No S³ coordinates to render.</div>;
  }

  return (
    <div className={fill ? `${styles.root} ${styles.rootFill}` : styles.root}>
      {nodes.length > MAX_NODES ? (
        <Muted className={styles.cap}>Showing {MAX_NODES} of {nodes.length} nodes</Muted>
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
              // Avoid leaking a second context if React StrictMode remounts.
              preserveDrawingBuffer: false,
            }}
          >
            <Suspense fallback={null}>
              <GlomeScene
                nodes={nodes}
                trajectory={trajectory}
                highlightIds={highlights}
                revision={revision}
              />
            </Suspense>
          </Canvas>
        ) : null}
      </div>
      <p className={styles.note}>Structural identity on S³ — not semantic embedding.</p>
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
  return <GlomeCanvas nodes={nodes} highlightIds={highlightIds} fill={fill} />;
}
