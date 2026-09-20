import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useSyncExternalStore } from 'react';
import { ReadResource, type ReadLoader } from '../lib/readResource';

interface SharedReadEntry {
  resource: ReadResource<unknown>;
  consumers: number;
  releaseTimer: ReturnType<typeof setTimeout> | null;
  lastUsed: number;
}

const sharedReads = new Map<string, SharedReadEntry>();
const SHARED_READ_GRACE_MS = 30_000;
const MAX_SHARED_READS = 192;

function sharedEntry<T>(key: string): SharedReadEntry & { resource: ReadResource<T> } {
  let entry = sharedReads.get(key);
  if (!entry) {
    entry = {
      resource: new ReadResource<unknown>(),
      consumers: 0,
      releaseTimer: null,
      lastUsed: Date.now(),
    };
    sharedReads.set(key, entry);
    if (sharedReads.size > MAX_SHARED_READS) {
      const inactive = [...sharedReads.entries()]
        .filter(([, value]) => value.consumers === 0 && value.releaseTimer === null)
        .sort((a, b) => a[1].lastUsed - b[1].lastUsed);
      for (const [oldKey, value] of inactive) {
        if (sharedReads.size <= MAX_SHARED_READS) break;
        value.resource.cancel();
        sharedReads.delete(oldKey);
      }
    }
  }
  entry.lastUsed = Date.now();
  return entry as SharedReadEntry & { resource: ReadResource<T> };
}

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
  const entry = useMemo(() => sharedEntry<T>(key), [key]);
  const resource = entry.resource;
  const readRef = useRef(read);
  useLayoutEffect(() => { readRef.current = read; }, [read]);
  const snapshot = useSyncExternalStore(resource.subscribe, resource.getSnapshot, resource.getSnapshot);

  useEffect(() => {
    entry.consumers++;
    entry.lastUsed = Date.now();
    if (entry.releaseTimer) {
      clearTimeout(entry.releaseTimer);
      entry.releaseTimer = null;
    }
    return () => {
      entry.consumers = Math.max(0, entry.consumers - 1);
      entry.lastUsed = Date.now();
      if (entry.consumers !== 0) return;
      entry.releaseTimer = setTimeout(() => {
        entry.releaseTimer = null;
        if (entry.consumers !== 0 || sharedReads.get(key) !== entry) return;
        entry.resource.cancel();
        sharedReads.delete(key);
      }, SHARED_READ_GRACE_MS);
    };
  }, [entry, key]);

  useEffect(() => {
    if (!enabled) return;
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
      // The keyed resource may still have other consumers. Last-consumer
      // cancellation/eviction is owned by the shared registry above.
    };
  }, [resource, enabled, refreshMs]);

  const refresh = useCallback(() => resource.load(readRef.current), [resource]);
  const reload = useCallback(() => resource.reload(readRef.current), [resource]);
  return { ...snapshot, refresh, reload, cancel: resource.cancel,
    busy: snapshot.status === 'loading' || snapshot.status === 'refreshing' };
}
