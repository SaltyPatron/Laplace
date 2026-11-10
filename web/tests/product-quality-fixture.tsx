// Presentation regressions only. This fixture never contacts a substrate.
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { TooltipProvider } from '../src/ui';
import { StageBrowse } from '../src/explore/browse/StageBrowse';
import { ResolveBrowseRedirect } from '../src/explore/entity/ResolveBrowseRedirect';
import { GlomeTab } from '../src/explore/entity/tabs/GlomeTab';
import type { ExploreEntityResponse } from '../src/explore/types';
import '../src/ui/layers.css';
import '../src/ui/theme.css';

const params = new URLSearchParams(location.search);
const view = params.get('view') ?? 'resolve';
const failure = (status: number, message: string) => Response.json({ error: { message } }, { status });
window.fetch = async (input) => {
  const path = String(input);
  if (path.startsWith('/v1/explore/resolve?')) return Response.json({ exists: true, id_hex: 'a'.repeat(32), label: params.get('ref') ?? '%' });
  if (path === '/v1/explore/catalog' && view === 'missing-stage') return Response.json({ stages: [] });
  return failure(503, 'Read temporarily unavailable');
};
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
const entry = view.endsWith('stage') ? '/explore/stage/missing' : `/explore/resolve/${encodeURIComponent(reference)}`;
createRoot(document.getElementById('root')!).render(<StrictMode><TooltipProvider>
  <h1>Product quality interaction fixture</h1>
  {view === 'glome' ? <>
    <input aria-label="Unrelated editor" defaultValue="draft" />
    <GlomeTab entity={entity} neighborMode="structural" neighborsUnlocked glomeExtraNodes={[]}
      walkHighlight={[]} onNeighborModeChange={() => {}} onLoadNeighbors={() => {}} />
  </> : <MemoryRouter initialEntries={[entry]}><Routes>
    <Route path="/explore/stage/:stageId" element={<StageBrowse />} />
    <Route path="/explore/resolve/:ref" element={<ResolveBrowseRedirect />} />
    <Route path="/explore" element={<LocationFixture />} />
  </Routes></MemoryRouter>}
</TooltipProvider></StrictMode>);
