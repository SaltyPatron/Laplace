import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useSyncExternalStore } from 'react';
import { ReadResource, type ReadLoader } from '../lib/readResource';

export interface ReadResourceOptions<T> {
  /** Complete read scope, including tenant and any credential/selection revision. */
  key: string;
  read: ReadLoader<T>;
  enabled?: boolean;
  /** Completion-relative refresh interval; zero means no polling. */
  refreshMs?: number;
}

/** Shared read state for independent panes. No whole-page Promise.all dependency. */
export function useReadResource<T>({ key, read, enabled = true, refreshMs = 0 }: ReadResourceOptions<T>) {
  const resource = useMemo(() => new ReadResource<T>(), [key]);
  const readRef = useRef(read);
  useLayoutEffect(() => { readRef.current = read; }, [read]);
  const snapshot = useSyncExternalStore(resource.subscribe, resource.getSnapshot, resource.getSnapshot);

  useEffect(() => {
    if (!enabled) return () => resource.cancel();
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const schedule = () => {
      clearTimeout(timer);
      if (!stopped && refreshMs > 0 && !document.hidden) timer = setTimeout(poll, refreshMs);
    };
    const poll = () => {
      if (stopped) return;
      void resource.load(readRef.current).then(schedule);
    };
    const visibility = () => {
      clearTimeout(timer);
      if (!document.hidden) poll();
    };
    poll();
    if (refreshMs > 0) document.addEventListener('visibilitychange', visibility);
    return () => {
      stopped = true;
      clearTimeout(timer);
      document.removeEventListener('visibilitychange', visibility);
      resource.cancel();
    };
  }, [resource, enabled, refreshMs]);

  const refresh = useCallback(() => resource.load(readRef.current), [resource]);
  const reload = useCallback(() => resource.reload(readRef.current), [resource]);
  return { ...snapshot, refresh, reload, cancel: resource.cancel,
    busy: snapshot.status === 'loading' || snapshot.status === 'refreshing' };
}
