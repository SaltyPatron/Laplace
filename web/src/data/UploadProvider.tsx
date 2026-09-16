import { createContext, useContext, useEffect, useMemo, useSyncExternalStore, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Banner, Button } from '@ui';
import { useAppStore } from '../store';
import { UploadQueue } from './uploadQueue';

const UploadContext = createContext<{ queue: UploadQueue; scope: string } | null>(null);
/** Retain explicit file selections across routes, but never across principal/tenant changes. */
export function UploadProvider({ children }: { children: ReactNode }) {
  const { tenant, authUser } = useAppStore();
  const scope = JSON.stringify([tenant, authUser?.id]);
  const value = useMemo(() => ({ scope, queue: new UploadQueue() }), [scope]);
  useEffect(() => {
    const beforeUnload = (event: BeforeUnloadEvent) => {
      const state = value.queue.getSnapshot();
      if (state.running || state.items.some((item) => item.status !== 'admitted')) {
        event.preventDefault(); event.returnValue = '';
      }
    };
    window.addEventListener('beforeunload', beforeUnload);
    return () => { window.removeEventListener('beforeunload', beforeUnload); value.queue.stop(); };
  }, [value]);
  return <UploadContext.Provider value={value}>{children}</UploadContext.Provider>;
}
export function useUploadQueue() {
  const value = useContext(UploadContext);
  if (!value) throw new Error('Data workspaces require UploadProvider.');
  return value;
}
export function DataActivity() {
  const { queue } = useUploadQueue();
  const state = useSyncExternalStore(queue.subscribe, queue.getSnapshot, queue.getSnapshot);
  const queued = state.items.filter((item) => item.status === 'queued').length;
  const attention = state.items.filter((item) => item.status === 'unconfirmed' || item.status === 'invalid').length;
  if (!state.running && queued === 0 && attention === 0) return null;
  return <Banner variant={attention ? 'warning' : 'info'}>
    <Link to="/data">Data selection</Link>: {queued} queued{attention > 0 ? ` · ${attention} need attention` : ''}{state.running ? ' · one admission in progress' : ''}.
    {state.running && <Button variant="ghost" disabled={state.stopping} onClick={queue.stop}>{state.stopping ? 'Stopping after current request' : 'Stop after current request'}</Button>}
  </Banner>;
}
