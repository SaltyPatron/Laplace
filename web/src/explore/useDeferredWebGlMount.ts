import { useEffect, useState } from 'react';

/**
 * Delay WebGL mount by two animation frames so a prior Graph/Glome
 * renderer can release its context before a new one is created.
 * Prevents "WebGL context was lost" when flipping entity tabs.
 */
export function useDeferredWebGlMount(enabled = true): boolean {
  const [ready, setReady] = useState(false);

  useEffect(() => {
    if (!enabled) {
      setReady(false);
      return;
    }

    let cancelled = false;
    let outer = 0;
    let inner = 0;
    outer = requestAnimationFrame(() => {
      inner = requestAnimationFrame(() => {
        if (!cancelled) setReady(true);
      });
    });

    return () => {
      cancelled = true;
      cancelAnimationFrame(outer);
      cancelAnimationFrame(inner);
      setReady(false);
    };
  }, [enabled]);

  return ready;
}
