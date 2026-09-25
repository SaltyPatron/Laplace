import { Suspense, useEffect, useMemo, useRef, useState } from 'react';
import { Canvas, useFrame, useThree } from '@react-three/fiber';
import { Html, Line, OrbitControls } from '@react-three/drei';
import * as THREE from 'three';
import type { ExplorePhysicalityRow } from '../types';
import { useDeferredWebGlMount } from '../useDeferredWebGlMount';
import { ensureVisualizationContrast, lerpColor, useVisualizationPalette, visualizationPalette, type VisualizationPalette } from '../visualizationPalette';
import styles from './GlomeCanvas.module.css';

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
}

interface GlomePalette {
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

function glomePalette(shared: VisualizationPalette): GlomePalette {
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

const SHELL = 0.85;

/** Packed: hash-XYZ on a display shell. M is paint, not an axis. */
export function packedDisplayPos(n: GlomeNode): [number, number, number] {
  const len = Math.hypot(n.x, n.y, n.z) || 1;
  return [(n.x / len) * SHELL, (n.y / len) * SHELL, (n.z / len) * SHELL];
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
 * Placement is a 3-D view of the real PointZM ball, not a flat XYZ slice.
 *
 * First rotate the actual 4-D point through X-M and Z-M planes so M remains
 * observable in both screen width and camera depth. Then use the rotated XYZ
 * direction with radius_origin as radial depth. This preserves the useful part
 * of the Aug-9 M-aware projection without discarding the coherence/interior
 * radius that the earlier glome-ball view exposed.
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

  const dirLen = Math.hypot(xmX, y, zmZ);
  if (dirLen <= Number.EPSILON) return [0, 0, 0];

  const fallbackRadius = Math.min(1, Math.hypot(xmX, y, zmZ, zmM) || 1);
  const radius4 = Number.isFinite(n.radius) && n.radius >= 0
    ? Math.min(1, n.radius)
    : fallbackRadius;
  const displayRadius = SHELL * Math.max(0.02, radius4);
  const scale = displayRadius / dirLen;
  return [xmX * scale, y * scale, zmZ * scale];
}

function project(
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

function ReferenceMarker({
  position,
  label,
  color,
}: {
  position: [number, number, number];
  label: string;
  color: string;
}) {
  const [showLabel, setShowLabel] = useState(false);
  const visibleRef = useRef(false);
  const point = useMemo(() => new THREE.Vector3(...position), [position]);
  const projected = useMemo(() => new THREE.Vector3(), []);
  const outer = useMemo(
    () => [position[0] * 1.055, position[1] * 1.055, position[2] * 1.055] as [number, number, number],
    [position],
  );
  const labelPosition = useMemo(
    () => [position[0] * 1.095, position[1] * 1.095, position[2] * 1.095] as [number, number, number],
    [position],
  );

  useFrame(({ camera }) => {
    projected.copy(point).project(camera);
    // Axis labels are orientation references, never data. Only show one when
    // the axis endpoint is actually near the viewport perimeter; ±Z therefore
    // disappears while it points through the middle of the cloud.
    const screenRadius = Math.hypot(projected.x, projected.y);
    const behind = projected.z < -1 || projected.z > 1;
    const visible = !behind && screenRadius > 0.62;
    if (visible === visibleRef.current) return;
    visibleRef.current = visible;
    setShowLabel(visible);
  });

  return (
    <>
      <Line
        points={[position, outer]}
        color={color}
        lineWidth={1.35}
        transparent={false}
      />
      <mesh position={outer}>
        <sphereGeometry args={[0.014, 8, 8]} />
        <meshBasicMaterial color={color} toneMapped={false} />
      </mesh>
      {showLabel ? (
        <Html position={labelPosition} center zIndexRange={[1, 0]}>
          <span className={styles.referenceLabel}>{label}</span>
        </Html>
      ) : null}
    </>
  );
}

function GlomeScene({
  nodes,
  trajectory,
  highlightIds,
  highlightOrdinal,
  projection,
  xmAngle,
  zmAngle,
  palette,
  revision,
  onSelectOrdinal,
}: {
  nodes: GlomeNode[];
  trajectory: [number, number, number][];
  highlightIds: Set<string>;
  highlightOrdinal: number | null;
  projection: GlomeProjection;
  xmAngle: number;
  zmAngle: number;
  palette: GlomePalette;
  revision: string;
  onSelectOrdinal?: (ordinal: number | null) => void;
}) {
  const [hover, setHover] = useState<GlomeNode | null>(null);
  const invalidate = useThree((s) => s.invalidate);
  const instanceMesh = useMemo(() => {
    // Per-sphere color is the instanceColor attribute that setColorAt() creates
    // before the first render; Three enables USE_INSTANCING_COLOR from it. The
    // material must not set vertexColors: that multiplies by a per-vertex `color`
    // attribute SphereGeometry does not have, which WebGL reads as zero (black).
    const geometry = new THREE.SphereGeometry(1, 9, 9);
    const material = new THREE.MeshBasicMaterial({
      color: 0xffffff,
      toneMapped: false,
    });
    const mesh = new THREE.InstancedMesh(geometry, material, Math.max(1, nodes.length));
    const transform = new THREE.Object3D();
    const color = new THREE.Color();
    const densityScale =
      nodes.length <= 8 ? 2.8
        : nodes.length <= 24 ? 2.2
          : nodes.length <= 96 ? 1.6
            : nodes.length >= 1000 ? 0.8
              : 1;

    for (let i = 0; i < nodes.length; i++) {
      const n = nodes[i];
      const [x, y, z] = project(n, projection, xmAngle, zmAngle);
      const ordHit = highlightOrdinal != null && n.ordinal === highlightOrdinal;
      const runScale = Math.min(0.009, Math.log2(Math.max(1, n.runLength ?? 1)) * 0.0015);
      const evidenceScale = n.evidenceRows && n.evidenceRows > 0
        ? 1 + Math.min(0.55, Math.log2(n.evidenceRows + 1) * 0.045)
        : 1;
      const radius = (ordHit ? 0.036 : 0.016 + runScale) * densityScale * evidenceScale;

      transform.position.set(x, y, z);
      transform.scale.setScalar(radius);
      transform.updateMatrix();
      mesh.setMatrixAt(i, transform.matrix);

      color.set(
        n.color
          ?? (n.kind === 'walk' ? palette.walk
            : n.kind === 'neighbor' ? palette.neighbor
              : n.kind === 'constituent' ? palette.constituent
                : ordHit || highlightIds.has(n.id) ? palette.highlight : palette.primary),
      );
      mesh.setColorAt(i, color);
    }

    mesh.count = nodes.length;
    mesh.instanceMatrix.needsUpdate = true;
    if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;
    return mesh;
  }, [nodes, projection, xmAngle, zmAngle, highlightIds, highlightOrdinal, palette]);

  useEffect(() => {
    invalidate();
    return () => {
      instanceMesh.geometry.dispose();
      const materials = Array.isArray(instanceMesh.material)
        ? instanceMesh.material
        : [instanceMesh.material];
      for (const material of materials) material.dispose();
    };
  }, [instanceMesh, invalidate]);

  return (
    <>
      <color attach="background" args={[palette.background]} />
      <ContextLossGuard />
      <InvalidateOnData revision={revision} />
      <primitive
        object={instanceMesh}
        onPointerMove={(e: { stopPropagation: () => void; instanceId?: number }) => {
          e.stopPropagation();
          setHover(e.instanceId == null ? null : nodes[e.instanceId] ?? null);
        }}
        onPointerOut={() => setHover(null)}
        onClick={(e: { stopPropagation: () => void; instanceId?: number }) => {
          e.stopPropagation();
          const n = e.instanceId == null ? null : nodes[e.instanceId];
          onSelectOrdinal?.(n?.ordinal ?? null);
        }}
      />
      {trajectory.length > 1 ? (
        <Line
          points={trajectory}
          color={projection === 'packed' || projection === 'carrier' ? palette.packedLine : palette.placementLine}
          lineWidth={1.25}
          transparent
          opacity={0.82}
        />
      ) : null}
      {([
        [[1, 0, 0], '+X'], [[-1, 0, 0], '−X'],
        [[0, 1, 0], '+Y'], [[0, -1, 0], '−Y'],
        [[0, 0, 1], '+Z'], [[0, 0, -1], '−Z'],
      ] as const).map(([position, label]) => (
        <ReferenceMarker
          key={label}
          position={[...position]}
          label={label}
          color={palette.reference}
        />
      ))}
      <mesh>
        <sphereGeometry args={[1, 28, 28]} />
        <meshBasicMaterial
          color={palette.wireframe}
          wireframe
          transparent
          opacity={0.34}
          toneMapped={false}
        />
      </mesh>
      {hover ? (
        <Html position={project(hover, projection, xmAngle, zmAngle).map((v) => v + 0.08) as [number, number, number]}>
          <div className={styles.tooltip}>
            <strong>{hover.label}</strong>
            {hover.ordinal != null ? <span> · ord {hover.ordinal}</span> : null}
            {hover.runLength != null && hover.runLength > 1 ? <span> · rle {hover.runLength}</span> : null}
            {hover.mu != null ? <span> · μ {hover.mu.toFixed(1)}</span> : null}
            {hover.evidenceRows != null ? <span> · evidence {hover.evidenceRows.toLocaleString()}</span> : null}
            {projection === 'placement' && Number.isFinite(hover.radius)
              ? <span> · r {hover.radius.toFixed(3)}</span>
              : null}
            {projection === 'carrier' && hover.m != null && Number.isFinite(hover.m)
              ? <span> · Mmeta {hover.m.toFixed(3)}</span>
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

/** Ordinal → the shared steel→signal visualization ramp for Packed paint. */
export function ordinalColor(
  ordinal: number,
  maxOrdinal: number,
  palette: VisualizationPalette = visualizationPalette(),
): string {
  const t = maxOrdinal <= 1 ? 0 : (ordinal - 1) / (maxOrdinal - 1);
  return lerpColor(palette.steel, palette.signal, t);
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

  const trajectory = useMemo(() => {
    if (trajectoryPoints) return trajectoryPoints;
    return nodes
      .filter((n) => n.kind === 'constituent')
      .slice()
      .sort((a, b) => (a.ordinal ?? 0) - (b.ordinal ?? 0))
      .map((n) => project(n, projection, xmAngle, zmAngle));
  }, [nodes, trajectoryPoints, projection, xmAngle, zmAngle]);

  const highlights = useMemo(() => new Set(highlightIds), [highlightIds]);
  const revision = useMemo(
    () => `${projection}:${xmDegrees}:${zmDegrees}:${palette.background}:${palette.primary}:${nodes.length}:${highlightOrdinal}:${nodes.map((n) => n.id).join(',')}`,
    [nodes, projection, xmDegrees, zmDegrees, palette, highlightOrdinal],
  );

  if (nodes.length === 0) {
    return <div className={styles.empty}>{emptyLabel}</div>;
  }

  return (
    <div className={fill ? `${styles.root} ${styles.rootFill}` : styles.root}>
      {projection === 'placement' ? (
        <div className={styles.rotationControls}>
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
          <label className={styles.rotationControl}>
            <span>Z–M depth</span>
            <input
              type="range"
              min="-180"
              max="180"
              step="1"
              value={zmDegrees}
              onChange={(event) => setZmDegrees(Number(event.target.value))}
            />
            <output>{zmDegrees}°</output>
          </label>
        </div>
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
                zmAngle={zmAngle}
                palette={palette}
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
