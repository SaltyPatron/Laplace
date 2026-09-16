// Component interaction fixture, never imported by the application entry point.
import { StrictMode, useEffect, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import { Button, Field, Input, Modal, NavTabs, Panel, ReadStatus, Select, TextArea, TooltipProvider, useReadResource } from '../src/ui';
import { apiGet, setApiWorkspace } from '../src/api/client';
import { QueryConsole } from '../src/query/QueryConsole';
import { BillingView } from '../src/billing/BillingView';
import { Activity } from '../src/admin/Activity';
import { OpConsole } from '../src/admin/OpConsole';
import { useAppStore } from '../src/store';
import { useSectionParam } from '../src/layout/useSectionParam';
import '../src/ui/layers.css';
import '../src/ui/theme.css';

function RetainedBody() {
  const [draft, setDraft] = useState('draft');
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLTextAreaElement>(null);
  useEffect(() => {
    const node = ref.current!;
    node.dataset.mounts = String(Number(node.dataset.mounts ?? 0) + 1);
  }, []);
  return <div style={{ overflow: 'auto' }}>
    <textarea ref={ref} aria-label="Retained draft" value={draft} onChange={(event) => setDraft(event.target.value)} />
    <Button onClick={() => setOpen(true)}>Open nested dialog</Button>
    <Modal open={open} onClose={() => setOpen(false)} title="Fixture dialog">
      <Input aria-label="Dialog input" /><Button onClick={() => setOpen(false)}>Close dialog</Button>
    </Modal>
    <pre>{'A bounded log line\n'.repeat(200)}</pre>
  </div>;
}
function Fixture() {
  const [scope, setScope] = useState('A');
  const [activations, setActivations] = useState(0);
  const [section, setSection] = useSectionParam('section', ['first', 'second'] as const, 'first');
  const read = useReadResource({ key: scope, read: (signal) => apiGet<{ value: string }>(`/__fixture/read?scope=${scope}`, { signal }), refreshMs: 100 });
  const independent = useReadResource({ key: 'independent', read: (signal) => apiGet<{ value: string }>('/__fixture/independent', { signal }) });
  return <main>
    <NavTabs label="Fixture navigation" tabs={[{ id: 'first', label: 'First location', href: '/__workspace_test?section=first', active: true, onClick: () => setSection('first') }]} />
    <Button onClick={() => setSection('first')}>First section</Button><Button onClick={() => setSection('second')}>Second section</Button>
    <output data-testid="section">{section}</output>
    <Field label="Exact value" help="Preserve spaces and letter case." error="An explicit validation error.">
      <Input id="exact-value" aria-describedby="extra-description" defaultValue=" King " />
    </Field><span id="extra-description">Existing description.</span>
    <Field label="Automatic input" help="Bound without an authored ID."><Input defaultValue="preserved" /></Field>
    <Field label="Automatic selection" help="Selection help."><Select defaultValue="one"><option value="one">First option</option></Select></Field>
    <Field label="Automatic multiline" help="Multiline help."><TextArea defaultValue="original text" /></Field>
    <Button visuallyDisabled onClick={() => setActivations((n) => n + 1)}>Disabled action</Button>
    <Button visuallyDisabled asChild><a href="#unwanted" onClick={() => setActivations((n) => n + 1)} onAuxClickCapture={() => setActivations((n) => n + 1)} onAuxClick={() => setActivations((n) => n + 1)}>Disabled link</a></Button>
    <output data-testid="activations">{activations}</output>
    <Button onClick={() => setScope(scope === 'A' ? 'B' : 'A')}>Change scope</Button>
    <Button onClick={() => void read.refresh()}>Refresh fixture</Button>
    <section aria-label="Primary read"><ReadStatus label="Fixture read" resource={read} /><output data-testid="read">{read.data?.value ?? ''}</output></section>
    <section aria-label="Independent read"><ReadStatus label="Independent read" resource={independent} /><output data-testid="independent">{independent.data?.value ?? ''}</output></section>
    <div style={{ height: 350, display: 'flex', transform: 'translateZ(0)' }}>
      <Panel title="Retained workspace" fill><RetainedBody /></Panel>
    </div>
    <Panel title="Other workspace" expandable><Input aria-label="Other draft" defaultValue="unchanged" /></Panel>
  </main>;
}
function Surface() {
  const view = new URLSearchParams(location.search).get('view');
  if (view === 'query') return <QueryConsole />;
  if (view === 'billing') return <><Button onClick={() => useAppStore.getState().setTenant('other-scope')}>Change tenant</Button><BillingView /></>;
  if (view === 'activity') return <Activity />;
  if (view === 'operations') return <OpConsole />;
  return <Fixture />;
}
// Fixture-only identity: production still obtains it from the authenticated API.
if (new URLSearchParams(location.search).get('view') === 'billing') {
  const state = useAppStore.getState();
  state.setAuth({ id: 'workspace-fixture-user', tenantId: state.tenant }, []);
  setApiWorkspace(state.tenant);
}
createRoot(document.getElementById('root')!).render(<StrictMode><TooltipProvider><BrowserRouter><Surface /></BrowserRouter></TooltipProvider></StrictMode>);
