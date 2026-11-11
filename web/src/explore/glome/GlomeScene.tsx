/** The WebGL glome: instanced points, trajectory, parent links, labels, axis markers and hover card. */
import { useEffect, useMemo, useRef, useState } from 'react';
import { useFrame, useThree } from '@react-three/fiber';
import { Html, Line, OrbitControls } from '@react-three/drei';
import * as THREE from 'three';
import { metricColor, type GlomePalette } from './glomePalette';
import {
  CAMERA_START, project, readableGlomeLabel, unpackMantissaCoord, unpackPathM,
  type GlomeColorBy, type GlomeNode, type GlomeProjection,
} from './glomeProjection';
import { ContextLossGuard, InvalidateOnData, ReferenceMarker } from './GlomeSceneParts';
import styles from './GlomeCanvas.module.css';

/** Labels appear once no more than this many points are in view... */
const LABEL_VIEW_LIMIT = 160;
/** ...and then only the ones nearest the center of the view. */
const LABEL_MAX = 24;

export function GlomeScene({
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
  colorBy,
  edges,
  showLabels,
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
  colorBy: GlomeColorBy;
  edges: [number, number, number][];
  showLabels: boolean;
}) {
  const [hover, setHover] = useState<GlomeNode | null>(null);
  const [labelled, setLabelled] = useState<number[]>([]);
  const invalidate = useThree((s) => s.invalidate);
  const positions = useMemo(
    () => nodes.map((n) => project(n, projection, xmAngle, zmAngle)),
    [nodes, projection, xmAngle, zmAngle],
  );
  const baseRadii = useRef<number[]>([]);
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
    const maxTier = Math.max(1, ...nodes.map((n) => n.tier ?? 0));
    const maxOrdinal = Math.max(1, ...nodes.map((n) => n.ordinal ?? 0));
    const radii: number[] = new Array(nodes.length);
    const densityScale =
      nodes.length <= 8 ? 2.8
        : nodes.length <= 24 ? 2.2
          : nodes.length <= 96 ? 1.6
            : nodes.length >= 1000 ? 0.8
              : 1;

    for (let i = 0; i < nodes.length; i++) {
      const n = nodes[i];
      const [x, y, z] = positions[i];
      const ordHit = highlightOrdinal != null && n.ordinal === highlightOrdinal;
      const runScale = Math.min(0.009, Math.log2(Math.max(1, n.runLength ?? 1)) * 0.0015);
      const evidenceScale = n.evidenceRows && n.evidenceRows > 0
        ? 1 + Math.min(0.55, Math.log2(n.evidenceRows + 1) * 0.045)
        : 1;
      const radius = (ordHit ? 0.036 : 0.016 + runScale) * densityScale * evidenceScale;
      radii[i] = radius;

      transform.position.set(x, y, z);
      transform.scale.setScalar(radius);
      transform.updateMatrix();
      mesh.setMatrixAt(i, transform.matrix);

      const metric =
        colorBy === 'tier' && n.tier != null ? n.tier / maxTier
          : colorBy === 'depth' && Number.isFinite(n.radius) ? Math.max(0, Math.min(1, n.radius))
            : colorBy === 'ordinal' && n.ordinal != null ? n.ordinal / maxOrdinal
              : null;
      color.set(
        ordHit || highlightIds.has(n.id) ? palette.highlight
          : n.color
            ?? (metric != null ? metricColor(metric, palette)
              : n.kind === 'walk' ? palette.walk
                : n.kind === 'neighbor' ? palette.neighbor
                  : n.kind === 'constituent' ? palette.constituent
                    : palette.primary),
      );
      mesh.setColorAt(i, color);
    }

    mesh.count = nodes.length;
    mesh.instanceMatrix.needsUpdate = true;
    if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;
    baseRadii.current = radii;
    return mesh;
  }, [nodes, positions, highlightIds, highlightOrdinal, palette, colorBy]);

  // Zooming must separate points, not magnify the tangle: point size stays constant
  // on screen, so diving into a dense region resolves its structure. The same pass
  // chooses which points to label once few enough are in view.
  const lastScale = useRef(1);
  const lastView = useRef('');
  const scratch = useMemo(() => ({ t: new THREE.Object3D(), v: new THREE.Vector3() }), []);
  useFrame(({ camera, controls }) => {
    const target = (controls as unknown as { target?: THREE.Vector3 } | null)?.target;
    const distance = target ? camera.position.distanceTo(target) : camera.position.length();
    const scale = Math.max(0.0005, distance / CAMERA_START);
    if (Math.abs(scale - lastScale.current) / lastScale.current > 0.02) {
      lastScale.current = scale;
      for (let i = 0; i < positions.length; i++) {
        scratch.t.position.set(...positions[i]);
        scratch.t.scale.setScalar((baseRadii.current[i] ?? 0.016) * scale);
        scratch.t.updateMatrix();
        instanceMesh.setMatrixAt(i, scratch.t.matrix);
      }
      instanceMesh.instanceMatrix.needsUpdate = true;
      invalidate();
    }
    if (!showLabels) {
      if (labelled.length) setLabelled([]);
      return;
    }
    const view = camera.matrixWorld.elements.map((e) => e.toFixed(4)).join(',');
    if (view === lastView.current) return;
    lastView.current = view;
    const inView: { i: number; d: number }[] = [];
    for (let i = 0; i < positions.length; i++) {
      scratch.v.set(...positions[i]).project(camera);
      if (scratch.v.z < -1 || scratch.v.z > 1 || Math.abs(scratch.v.x) > 1 || Math.abs(scratch.v.y) > 1) continue;
      inView.push({ i, d: Math.hypot(scratch.v.x, scratch.v.y) });
    }
    const next = inView.length <= LABEL_VIEW_LIMIT
      ? inView.sort((a, b) => a.d - b.d).slice(0, LABEL_MAX).map((x) => x.i).sort((a, b) => a - b)
      : [];
    if (next.join(',') !== labelled.join(',')) setLabelled(next);
  });

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
      {edges.length > 1 ? (
        <Line
          points={edges}
          segments
          color={palette.wireframe}
          lineWidth={0.75}
          transparent
          opacity={0.32}
        />
      ) : null}
      {trajectory.length > 1 ? (
        <Line
          points={trajectory}
          color={projection === 'packed' || projection === 'carrier' ? palette.packedLine : palette.placementLine}
          lineWidth={1.65}
          transparent
          opacity={0.85}
        />
      ) : null}
      {labelled.map((i) => (
        <Html key={nodes[i].id} position={positions[i]} zIndexRange={[2, 0]}>
          <span className={styles.pointLabel}>{readableGlomeLabel(nodes[i].label, nodes[i].id)}</span>
        </Html>
      ))}
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
            <strong>{readableGlomeLabel(hover.label, hover.id)}</strong>
            {hover.ordinal != null ? <span> · ord {hover.ordinal}</span> : null}
            {hover.runLength != null && hover.runLength > 1 ? <span> · rle {hover.runLength}</span> : null}
            {hover.mu != null ? <span> · μ {hover.mu.toFixed(1)}</span> : null}
            {hover.evidenceRows != null ? <span> · evidence {hover.evidenceRows.toLocaleString()}</span> : null}
            {projection === 'placement' && Number.isFinite(hover.radius)
              ? <span> · r {hover.radius.toFixed(3)}</span>
              : null}
            {projection === 'packed'
              ? <span> · mxyz ({unpackMantissaCoord(hover.x, 'x').toFixed(3)}, {unpackMantissaCoord(hover.y, 'y').toFixed(3)}, {unpackMantissaCoord(hover.z, 'z').toFixed(3)})</span>
              : null}
            {projection === 'packed' && hover.m != null && Number.isInteger(hover.m) && hover.m >= 1
              ? (() => { const m = unpackPathM(hover.m ?? 1); return <span> · M run {m.run} said {m.said} outcome {m.outcome} pos {m.position}</span>; })()
              : null}
            {projection === 'carrier' && hover.m != null && Number.isFinite(hover.m)
              ? <span> · Mmeta {hover.m.toFixed(3)}</span>
              : null}
          </div>
        </Html>
      ) : null}
      <OrbitControls enablePan enableZoom zoomToCursor minDistance={0.0005} maxDistance={8} zoomSpeed={1.4} makeDefault />
    </>
  );
}
