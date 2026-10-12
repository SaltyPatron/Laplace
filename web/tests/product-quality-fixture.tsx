// Presentation regressions only. This fixture never contacts a substrate.
import { StrictMode, useSyncExternalStore } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { TooltipProvider } from '../src/ui';
import { usePulse } from '../src/home/usePulse';
import { useAppStore } from '../src/store';
import { TopicView } from '../src/topic/TopicView';
import { StageBrowse } from '../src/explore/browse/StageBrowse';
import { ResolveBrowseRedirect } from '../src/explore/entity/ResolveBrowseRedirect';
import { GlomeTab } from '../src/explore/entity/tabs/GlomeTab';
import type { ExploreEntityResponse } from '../src/explore/types';
import '../src/ui/layers.css';
import '../src/ui/theme.css';

const params = new URLSearchParams(location.search);
const view = params.get('view') ?? 'topic';
let pulseCalls = 0;
const listeners = new Set<() => void>();
const subscribe = (fn: () => void) => { listeners.add(fn); return () => { listeners.delete(fn); }; };
const failure = (status: number, message: string) => Response.json({ error: { message } }, { status });
window.fetch = async (input, init) => {
  const path = String(input);
  if (path === '/v1/pulse') {
    pulseCalls++;
    listeners.forEach((notify) => notify());
    if (!useAppStore.getState().authUser) return failure(401, 'Sign in required');
    return Response.json({ at: pulseCalls, entities: 42, attestations: 0, consensus: 0, physicalities: 0, flushes_last_min: 0, folding: false });
  }
  if (path.startsWith('/v1/explore/resolve?')) return Response.json({ exists: true, id_hex: 'a'.repeat(32), label: params.get('ref') ?? '%' });
  if (path === '/v1/query') {
    const shape = JSON.parse(String(init?.body)).shape;
    return shape === 'band_facts' ? Response.json({ rows: [{ reply: 'Independent facts loaded' }] })
      : failure(shape === 'translate' ? 402 : 503, `${shape} fixture failure`);
  }
  if (path === '/v1/explore/catalog' && view === 'missing-stage') return Response.json({ stages: [] });
  return failure(503, 'Read temporarily unavailable');
};
function PulseFixture() {
  const state = usePulse(100);
  const calls = useSyncExternalStore(subscribe, () => pulseCalls);
  return <>
    <button onClick={() => useAppStore.getState().setAuth(null, [])}>Resolve anonymous auth</button>
    <button onClick={() => useAppStore.getState().setAuth({ id: 'fixture', tenantId: 'fixture' }, [])}>Sign in fixture</button>
    <output aria-label="Pulse requests">{calls}</output>
    <output aria-label="Pulse result">{state.failure ?? state.pulse?.entities ?? 'Waiting for auth'}</output>
  </>;
}
function LocationFixture() {
  const route = useLocation();
  return <output aria-label="Route">{route.pathname}{route.search}</output>;
}
const entity = {
  id_hex: 'a'.repeat(32), label: 'Fixture composition', physicalities: [], realized_vertices: [],
  packed_vertices: [{ ordinal: 1, child_id_hex: 'b'.repeat(32), run_length: 5, x: 1, y: 0, z: 0, m: 0, flags: 0 }],
  constituents: [{ ordinal: 1, child_id_hex: 'b'.repeat(32), child_label: 'Repeated child', run_length: 5, flags: 0 }],
} as ExploreEntityResponse;
const reference = params.get('ref') ?? '%';
const entry = view === 'resolve' ? `/explore/resolve/${encodeURIComponent(reference)}`
  : view.endsWith('stage') ? '/explore/stage/missing' : `/topic/${encodeURIComponent(reference)}`;
createRoot(document.getElementById('root')!).render(<StrictMode><TooltipProvider>
  <h1>Product quality interaction fixture</h1>
  {view === 'pulse' ? <PulseFixture /> : view === 'glome' ? <>
    <input aria-label="Unrelated editor" defaultValue="draft" />
    <GlomeTab entity={entity} neighborMode="structural" neighborsUnlocked glomeExtraNodes={[]}
      walkHighlight={[]} onNeighborModeChange={() => {}} onLoadNeighbors={() => {}} />
  </> : <MemoryRouter initialEntries={[entry]}><Routes>
    <Route path="/topic/:ref" element={<TopicView />} />
    <Route path="/explore/stage/:stageId" element={<StageBrowse />} />
    <Route path="/explore/resolve/:ref" element={<ResolveBrowseRedirect />} />
    <Route path="/explore" element={<LocationFixture />} />
  </Routes></MemoryRouter>}
</TooltipProvider></StrictMode>);
