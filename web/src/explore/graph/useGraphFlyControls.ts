import { useEffect, useRef, type RefObject } from 'react';
import { Spherical, Vector3, type Camera, type Object3D } from 'three';

type OrbitLike = {
  target: Vector3;
  update?: () => void;
  enabled?: boolean;
};

type Graph3dFlyApi = {
  camera: () => Camera;
  controls: () => OrbitLike;
  cameraPosition: (
    pos: { x: number; y: number; z: number },
    lookAt?: { x: number; y: number; z: number },
    ms?: number,
  ) => void;
};

/** Continuous hold-to-fly keys. Home/End are one-shots. Ins/Del intentionally unbound. */
const MOVE_KEYS = new Set([
  'KeyW', 'KeyA', 'KeyS', 'KeyD',
  'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight',
  'PageUp', 'PageDown',
  'KeyR', 'KeyF',
  'KeyQ', 'KeyE',
  'Space',
  'ShiftLeft', 'ShiftRight',
]);

const ORIGIN = { x: 0, y: 0, z: 0 };

function isTypingTarget(el: EventTarget | null): boolean {
  if (!(el instanceof HTMLElement)) return false;
  const tag = el.tagName;
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || el.isContentEditable;
}

function shellHasFocus(shell: HTMLElement | null): boolean {
  if (!shell) return false;
  const active = document.activeElement;
  return active === shell || shell.contains(active);
}

/**
 * FPS-ish fly layer on top of orbit mouse controls.
 * Home → recenter on seed origin · End → antipodal viewpoint through origin (sphere flip).
 * Insert / Delete are never bound.
 */
export function useGraphFlyControls(
  shellRef: RefObject<HTMLElement | null>,
  graphRef: RefObject<Graph3dFlyApi | null>,
  enabled: boolean,
) {
  const pressed = useRef(new Set<string>());
  const raf = useRef(0);
  const scratch = useRef({
    forward: new Vector3(),
    right: new Vector3(),
    up: new Vector3(0, 1, 0),
    delta: new Vector3(),
    offset: new Vector3(),
    spherical: new Spherical(),
  });

  useEffect(() => {
    if (!enabled) return;

    const baseSpeed = 1.15;
    const sprintMul = 2.6;
    const turnRate = 0.038;

    const step = () => {
      raf.current = requestAnimationFrame(step);
      const keys = pressed.current;
      if (keys.size === 0) return;
      const fg = graphRef.current;
      if (!fg) return;

      let camera: Camera;
      let controls: OrbitLike;
      try {
        camera = fg.camera();
        controls = fg.controls();
      } catch {
        return;
      }
      if (!camera || !controls?.target) return;

      const { forward, right, up, delta, offset, spherical } = scratch.current;
      camera.getWorldDirection(forward);
      forward.normalize();
      right.crossVectors(forward, up).normalize();
      if (right.lengthSq() < 1e-6) {
        const camObj = camera as Camera & Object3D;
        right.set(1, 0, 0).applyQuaternion(camObj.quaternion).normalize();
      }

      delta.set(0, 0, 0);
      if (keys.has('KeyW') || keys.has('ArrowUp')) delta.add(forward);
      if (keys.has('KeyS') || keys.has('ArrowDown')) delta.sub(forward);
      if (keys.has('KeyD') || keys.has('ArrowRight')) delta.add(right);
      if (keys.has('KeyA') || keys.has('ArrowLeft')) delta.sub(right);
      if (keys.has('PageUp') || keys.has('KeyR') || keys.has('Space')) delta.add(up);
      if (keys.has('PageDown') || keys.has('KeyF')) delta.sub(up);

      const sprint = keys.has('ShiftLeft') || keys.has('ShiftRight');
      if (delta.lengthSq() > 0) {
        delta.normalize().multiplyScalar(baseSpeed * (sprint ? sprintMul : 1));
        camera.position.add(delta);
        controls.target.add(delta);
      }

      const yaw =
        (keys.has('KeyQ') ? 1 : 0) +
        (keys.has('KeyE') ? -1 : 0);
      if (yaw !== 0) {
        offset.copy(camera.position).sub(controls.target);
        spherical.setFromVector3(offset);
        spherical.theta += yaw * turnRate * (sprint ? 1.6 : 1);
        offset.setFromSpherical(spherical);
        camera.position.copy(controls.target).add(offset);
        camera.lookAt(controls.target);
      }

      controls.update?.();
    };

    /** Seed sits at world origin — Home restores a canonical look-at. */
    const goHome = () => {
      const fg = graphRef.current;
      if (!fg) return;
      let dist = 140;
      try {
        const cam = fg.camera();
        dist = Math.max(60, cam.position.length() || 140);
      } catch { /* keep default */ }
      fg.cameraPosition(
        { x: 0, y: dist * 0.22, z: dist },
        ORIGIN,
        550,
      );
    };

    /**
     * Antipodal viewpoint through the seed origin (Borsuk–Ulam sphere flip):
     * camera C ↦ −C, look at origin. Same radial distance, opposite point on the view-sphere.
     */
    const goAntipode = () => {
      const fg = graphRef.current;
      if (!fg) return;
      try {
        const cam = fg.camera();
        const x = cam.position.x;
        const y = cam.position.y;
        const z = cam.position.z;
        const r2 = x * x + y * y + z * z;
        if (r2 < 1e-4) {
          goHome();
          return;
        }
        fg.cameraPosition({ x: -x, y: -y, z: -z }, ORIGIN, 550);
      } catch {
        goHome();
      }
    };

    const onKeyDown = (e: KeyboardEvent) => {
      if (isTypingTarget(e.target)) return;
      if (!shellHasFocus(shellRef.current)) return;

      // Explicitly ignore — never bind Insert/Delete.
      if (e.code === 'Insert' || e.code === 'Delete') return;

      if (e.code === 'Home') {
        e.preventDefault();
        goHome();
        return;
      }
      if (e.code === 'End') {
        e.preventDefault();
        goAntipode();
        return;
      }

      if (!MOVE_KEYS.has(e.code)) return;
      pressed.current.add(e.code);
      if (e.code !== 'ShiftLeft' && e.code !== 'ShiftRight') e.preventDefault();
    };

    const onKeyUp = (e: KeyboardEvent) => {
      pressed.current.delete(e.code);
    };

    const onBlur = () => {
      pressed.current.clear();
    };

    window.addEventListener('keydown', onKeyDown);
    window.addEventListener('keyup', onKeyUp);
    window.addEventListener('blur', onBlur);
    raf.current = requestAnimationFrame(step);

    return () => {
      cancelAnimationFrame(raf.current);
      window.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('keyup', onKeyUp);
      window.removeEventListener('blur', onBlur);
      pressed.current.clear();
    };
  }, [enabled, graphRef, shellRef]);
}
