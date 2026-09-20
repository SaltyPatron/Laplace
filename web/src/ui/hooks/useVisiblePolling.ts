import { useEffect, useLayoutEffect, useRef } from 'react';

export interface VisiblePollingOptions {
  intervalMs: number;
  enabled?: boolean;
  immediate?: boolean;
}

/**
 * Completion-relative polling for browser workspaces.
 *
 * - never overlaps one invocation with the next;
 * - pauses while the document is hidden;
 * - resumes immediately when the tab becomes visible;
 * - keeps the callback fresh without restarting the timer on every render.
 */
export function useVisiblePolling(
  task: () => unknown | Promise<unknown>,
  { intervalMs, enabled = true, immediate = true }: VisiblePollingOptions,
): void {
  const taskRef = useRef(task);
  useLayoutEffect(() => { taskRef.current = task; }, [task]);

  useEffect(() => {
    if (!enabled || intervalMs <= 0) return;

    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let running = false;

    const clear = () => {
      if (timer !== undefined) clearTimeout(timer);
      timer = undefined;
    };

    const schedule = () => {
      clear();
      if (!stopped && !document.hidden) timer = setTimeout(run, intervalMs);
    };

    const run = async () => {
      if (stopped || running || document.hidden) return;
      running = true;
      try {
        await taskRef.current();
      } catch {
        // Polling consumers own error presentation. A failed tick must not
        // terminate the cadence or become an unhandled rejection.
      } finally {
        running = false;
        schedule();
      }
    };

    const onVisibility = () => {
      clear();
      if (!document.hidden) void run();
    };

    if (immediate) void run();
    else schedule();
    document.addEventListener('visibilitychange', onVisibility);
    return () => {
      stopped = true;
      clear();
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, [enabled, immediate, intervalMs]);
}
