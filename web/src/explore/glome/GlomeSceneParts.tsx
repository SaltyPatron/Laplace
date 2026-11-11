/** Small three.js helpers mounted inside the glome scene. */
import { useEffect, useMemo, useRef, useState } from 'react';
import { useFrame, useThree } from '@react-three/fiber';
import { Html, Line } from '@react-three/drei';
import * as THREE from 'three';
import styles from './GlomeCanvas.module.css';

/** Demand-mode: redraw when node set changes; OrbitControls still invalidates on input. */
export function InvalidateOnData({ revision }: { revision: string }) {
  const invalidate = useThree((s) => s.invalidate);
  useEffect(() => {
    invalidate();
  }, [revision, invalidate]);
  return null;
}

export function ContextLossGuard() {
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

export function ReferenceMarker({
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
