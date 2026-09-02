import { useEffect, useRef, useState, type CSSProperties } from 'react';
import styles from './AmbientFamiliar.module.css';

type FamiliarMode =
  | 'hidden'
  | 'climb'
  | 'wander'
  | 'stalk'
  | 'pounce'
  | 'startled'
  | 'dive'
  | 'swim';

type FamiliarSignal = 'visit' | 'startle' | 'swim' | 'hide';

interface FamiliarPose {
  mode: FamiliarMode;
  x: number;
  y: number;
  direction: 1 | -1;
  step: number;
  duration: number;
}

interface PointerSample {
  x: number;
  y: number;
  at: number;
}

const INITIAL_POSE: FamiliarPose = {
  mode: 'hidden',
  x: 0,
  y: 0,
  direction: 1,
  step: 0,
  duration: 10,
};

const NATURAL_FACING: Partial<Record<FamiliarMode, 1 | -1>> = {
  wander: -1,
  stalk: 1,
  pounce: 1,
  startled: -1,
  swim: 1,
};

const randomBetween = (min: number, max: number) => min + Math.random() * (max - min);

const viewportWidth = () => Math.max(960, window.innerWidth);

const clampToRail = (x: number) => Math.max(20, Math.min(viewportWidth() - 124, x));

/**
 * A deliberately rare ambient familiar, not application chrome. It never
 * receives pointer events and it stays out of compact/coarse-pointer/reduced-
 * motion contexts. Real system events can opt in through `laplace:familiar`;
 * the idle state machine supplies the small unscripted moments between them.
 */
export function AmbientFamiliar() {
  const [enabled, setEnabled] = useState(false);
  const [pose, setPose] = useState<FamiliarPose>(INITIAL_POSE);
  const pointer = useRef<PointerSample>({ x: -10_000, y: -10_000, at: 0 });

  useEffect(() => {
    const media = window.matchMedia(
      '(min-width: 960px) and (pointer: fine) and (prefers-reduced-motion: no-preference)',
    );
    const sync = () => setEnabled(media.matches);
    sync();
    media.addEventListener('change', sync);
    return () => media.removeEventListener('change', sync);
  }, []);

  useEffect(() => {
    if (!enabled) return;
    const rememberPointer = (event: PointerEvent) => {
      pointer.current = { x: event.clientX, y: event.clientY, at: Date.now() };
    };
    window.addEventListener('pointermove', rememberPointer, { passive: true });
    return () => window.removeEventListener('pointermove', rememberPointer);
  }, [enabled]);

  useEffect(() => {
    if (!enabled) {
      setPose(INITIAL_POSE);
      return;
    }

    const onSignal = (event: Event) => {
      const signal = (event as CustomEvent<FamiliarSignal>).detail;
      if (signal === 'hide') {
        setPose(INITIAL_POSE);
      } else if (signal === 'startle') {
        setPose((current) => ({ ...current, mode: 'startled', step: current.step + 1 }));
      } else if (signal === 'swim') {
        setPose({
          mode: 'swim',
          x: 0,
          y: randomBetween(90, Math.max(180, window.innerHeight * 0.66)),
          direction: Math.random() > 0.5 ? 1 : -1,
          step: 0,
          duration: randomBetween(8, 13),
        });
      } else if (signal === 'visit') {
        setPose({
          mode: 'climb',
          x: clampToRail(randomBetween(80, viewportWidth() - 160)),
          y: 0,
          direction: Math.random() > 0.5 ? 1 : -1,
          step: 0,
          duration: 0,
        });
      }
    };

    window.addEventListener('laplace:familiar', onSignal);
    return () => window.removeEventListener('laplace:familiar', onSignal);
  }, [enabled]);

  useEffect(() => {
    if (!enabled) return;

    let timer = 0;
    let tracking = 0;
    const later = (fn: () => void, ms: number) => {
      timer = window.setTimeout(fn, ms);
    };

    switch (pose.mode) {
      case 'hidden':
        later(() => {
          if (Math.random() < 0.12) {
            setPose({
              mode: 'swim',
              x: 0,
              y: randomBetween(90, Math.max(180, window.innerHeight * 0.68)),
              direction: Math.random() > 0.5 ? 1 : -1,
              step: 0,
              duration: randomBetween(8, 13),
            });
            return;
          }
          setPose({
            mode: 'climb',
            x: clampToRail(randomBetween(80, viewportWidth() - 160)),
            y: 0,
            direction: Math.random() > 0.5 ? 1 : -1,
            step: 0,
            duration: 0,
          });
        }, randomBetween(5_000, 12_000));
        break;

      case 'climb':
        later(() => {
          setPose((current) => ({ ...current, mode: 'wander', step: 1 }));
        }, 1_050);
        break;

      case 'wander':
        later(() => {
          const sample = pointer.current;
          const pointerIsFresh = Date.now() - sample.at < 2_500;
          const pointerIsNearRail = sample.y > window.innerHeight - 220;
          const pointerIsNearFamiliar = Math.abs(sample.x - pose.x) < 360;

          if (pointerIsFresh && pointerIsNearRail && pointerIsNearFamiliar && Math.random() < 0.62) {
            const target = clampToRail(sample.x - 48);
            setPose((current) => ({
              ...current,
              mode: 'stalk',
              x: target,
              direction: target >= current.x ? 1 : -1,
              step: current.step + 1,
            }));
            return;
          }

          if (pose.step > 4 && Math.random() < 0.25) {
            setPose((current) => ({ ...current, mode: 'startled', step: current.step + 1 }));
            return;
          }

          const target = clampToRail(pose.x + randomBetween(-190, 190));
          setPose((current) => ({
            ...current,
            x: target,
            direction: target >= current.x ? 1 : -1,
            step: current.step + 1,
          }));
        }, randomBetween(1_500, 3_400));
        break;

      case 'stalk': {
        const stopAt = Date.now() + randomBetween(1_500, 2_400);
        tracking = window.setInterval(() => {
          const sample = pointer.current;
          if (Date.now() > stopAt || Date.now() - sample.at > 1_500) {
            window.clearInterval(tracking);
            setPose((current) => ({ ...current, mode: 'pounce', step: current.step + 1 }));
            return;
          }
          setPose((current) => {
            const target = clampToRail(sample.x - 48);
            const next = current.x + (target - current.x) * 0.42;
            return {
              ...current,
              x: next,
              direction: target >= current.x ? 1 : -1,
            };
          });
        }, 140);
        break;
      }

      case 'pounce':
        later(() => {
          setPose((current) => ({
            ...current,
            mode: Math.random() < 0.3 ? 'startled' : 'wander',
            step: current.step + 1,
          }));
        }, 760);
        break;

      case 'startled':
        later(() => {
          setPose((current) => ({ ...current, mode: 'dive', step: current.step + 1 }));
        }, 720);
        break;

      case 'dive':
        later(() => setPose(INITIAL_POSE), 900);
        break;

      case 'swim':
        later(() => setPose(INITIAL_POSE), pose.duration * 1_000 + 250);
        break;
    }

    return () => {
      window.clearTimeout(timer);
      window.clearInterval(tracking);
    };
  }, [enabled, pose.mode, pose.step]);

  if (!enabled) return null;

  const naturalFacing = NATURAL_FACING[pose.mode] ?? 1;
  const flip = naturalFacing === pose.direction ? 1 : -1;
  const customProperties = {
    '--familiar-x': `${pose.x}px`,
    '--familiar-y': `${pose.y}px`,
    '--familiar-flip': flip,
    '--familiar-swim-duration': `${pose.duration}s`,
  } as CSSProperties;

  return (
    <div
      className={styles.layer}
      aria-hidden="true"
      data-testid="ambient-familiar"
      style={customProperties}
    >
      <div
        className={`${styles.actor} ${styles[pose.mode]}`}
        data-mode={pose.mode}
        data-direction={pose.direction}
      >
        <div className={styles.facing}>
          <div className={`${styles.sprite} ${styles[`pose_${pose.mode}`]}`} />
        </div>
      </div>
    </div>
  );
}
