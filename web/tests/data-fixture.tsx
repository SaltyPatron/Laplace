// Presentation-only fixture; application entry never imports it.
import { StrictMode, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter, Route, Routes, useNavigate } from 'react-router-dom';
import { Button, TooltipProvider } from '../src/ui';
import { ResultWorkspace } from '../src/ui/composites/ResultWorkspace/ResultWorkspace';
import { captureRows } from '../src/ui/lib/resultRows';
import { DataView } from '../src/data/DataView';
import { UploadProvider, DataActivity } from '../src/data/UploadProvider';
import { useAppStore } from '../src/store';
import '../src/ui/layers.css';
import '../src/ui/theme.css';
function Responses() {
  const [snapshot, setSnapshot] = useState(() => captureRows(Array.from({ length: 60 }, (_, index) => ({ label: `Record ${index + 1}`, wide: '9223372036854775807', ordinal: index + 1 })), 'Fixture response only'));
  return <><Button onClick={() => setSnapshot(captureRows([{ label: 'Changed record', wide: '2', ordinal: 1 }], 'Changed fixture response'))}>Replace response</Button>
    <ResultWorkspace scopeKey="fixture" snapshot={snapshot} label="Fixture records" rowLabel={(row) => row.label} /></>;
}
function Fixture() {
  const navigate = useNavigate();
  return <UploadProvider><nav><Button onClick={() => navigate('/data')}>Open data workspace</Button><Button onClick={() => navigate('/elsewhere')}>Another workspace</Button><Button onClick={() => navigate('/rows')}>Response workspace</Button>
    <Button onClick={() => useAppStore.getState().setTenant('another-tenant')}>Switch fixture tenant</Button></nav><DataActivity />
    <Routes><Route path="/data" element={<DataView />} /><Route path="/rows" element={<Responses />} /><Route path="*" element={<h2>Another workspace</h2>} /></Routes>
  </UploadProvider>;
}
createRoot(document.getElementById('root')!).render(<StrictMode><TooltipProvider><MemoryRouter initialEntries={['/data']}><Fixture /></MemoryRouter></TooltipProvider></StrictMode>);
