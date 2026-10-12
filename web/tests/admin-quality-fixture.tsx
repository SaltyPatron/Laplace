// Production screens with explicit, inspectable responses for browser checks.
import { useState } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { TooltipProvider } from '../src/ui';
import { AdminView } from '../src/admin/AdminView';
import { SettingsView } from '../src/auth/SettingsView';
import { AccountControls } from '../src/auth/AccountControls';
import { useAppStore } from '../src/store';
import '../src/ui/layers.css';
import '../src/ui/theme.css';

const query = new URLSearchParams(location.search);
let platformRole: string | null = query.get('role') ?? 'administrator';
if (platformRole === 'member' || platformRole === 'anonymous') platformRole = null;
const anonymous = query.get('role') === 'anonymous';
const roleDefinitions = [
  { id: 'observer', name: 'Observer', description: 'View ingestion progress and system activity.', permissions: ['platform.read'] },
  { id: 'operator', name: 'Operations manager', description: 'Run ingestion and manage services.', permissions: ['platform.read', 'ingestion.manage', 'services.manage'] },
  { id: 'administrator', name: 'Platform administrator', description: 'Manage platform access, ingestion, services, and maintenance.', permissions: ['platform.read', 'ingestion.manage', 'services.manage', 'platform.maintain', 'platform.access.manage'] },
];
const user = { id: '00000000-0000-4000-8000-000000000001', email: 'owner@example.test', displayName: 'Workspace Owner', tenantId: 'fixture-workspace', provider: 'google' };
const providers = [{ id: 'google', displayName: 'Google', loginUrl: '/fixture-login/google' }, { id: 'microsoft', displayName: 'Microsoft', loginUrl: '/fixture-login/microsoft' }];
useAppStore.getState().setAuth(anonymous ? null : user, providers);
useAppStore.getState().setTenant(user.tenantId);
const people = [
  { userId: user.id, email: user.email, displayName: user.displayName, providers: ['google'], role: 'administrator' },
  { userId: '00000000-0000-4000-8000-000000000002', email: 'manager@example.test', displayName: 'Operations Manager', providers: ['microsoft'], role: 'operator' },
];
const members = people.map((p, i) => ({ ...p, role: i ? 'member' : 'owner' }));
const account = { userId: user.id, tenantId: user.tenantId, displayName: user.displayName, email: user.email, provider: user.provider,
  workspaces: [{ tenantId: user.tenantId, displayName: 'Laplace team', kind: 'organization', role: query.get('workspaceRole') ?? 'owner' }, { tenantId: 'personal', displayName: 'My workspace', kind: 'personal', role: 'owner' }],
  subscriptions: [], workspaceRoles: ['member', 'admin', 'owner'].map((id) => ({ id, name: id, permissions: ['Use workspace tools', ...(id === 'owner' ? ['Assign workspace roles'] : [])] })),
  configuration: { authMode: 'identity', billingEnforced: false, billingStore: 'postgres', stripeConfigured: false, providers: [], privateDataIsolation: false } };
let mutations = 0;
let rejectChanges = query.get('reject') === '1';
let failAccess = query.get('accessError') === '1';
let refreshOutput: () => void = () => {};
const events: unknown[] = [];
const workspaceEvents: unknown[] = [];
const jobs: unknown[] = [];
const denied = () => Response.json({ error: { message: 'Keep at least one platform administrator. Assign another administrator first.' } }, { status: 409 });
window.fetch = async (input, init) => {
  const url = new URL(String(input), location.origin); const path = url.pathname;
  const method = init?.method ?? 'GET'; const payload = init?.body ? JSON.parse(String(init.body)) : {};
  if (path === '/v1/access') return failAccess ? Response.json({ error: { message: 'Access service unavailable' } }, { status: 503 })
    : Response.json({ authenticated: !anonymous, userId: anonymous ? null : user.id, workspace: { tenantId: user.tenantId, role: 'owner' }, platform: { role: platformRole, permissions: roleDefinitions.find((r) => r.id === platformRole)?.permissions ?? [] }, roles: roleDefinitions });
  if (path === '/v1/account') return Response.json(account);
  if (path === '/v1/account/members') return Response.json({ members });
  if (path === '/v1/account/invitations') return Response.json({ invitations: [] });
  if (path === '/v1/account/role-events') return Response.json({ events: workspaceEvents });
  if (path === '/v1/billing/keys') return Response.json({ keys: [{ key_prefix: 'lp_demo', label: 'Analytics integration', created_at: new Date().toISOString(), revoked_at: null }] });
  if (path === '/v1/admin/access/people') return Response.json({ people: people.filter((p) => p.email.includes(url.searchParams.get('search') ?? '')), limit: 100 });
  if (path === '/v1/admin/access/events') return Response.json({ events, limit: 50 });
  if (method === 'PUT' && path.endsWith('/role')) {
    if (rejectChanges) return denied();
    mutations++;
    if (path.startsWith('/v1/account/members/')) {
      const member = members.find((p) => path.includes(p.userId));
      if (member) {
        workspaceEvents.unshift({ eventId: mutations, person: member.email, actor: user.email, previousRole: member.role, role: payload.role, occurredAt: new Date().toISOString() });
        member.role = payload.role;
        if (member.userId === user.id) account.workspaces[0].role = payload.role;
      }
      refreshOutput(); return new Response(null, { status: 204 });
    }
    const person = people.find((p) => path.includes(p.userId));
    if (person) { events.unshift({ eventId: mutations, userId: person.userId, person: person.email, actor: user.id, actorName: user.email, previousRole: person.role, role: payload.role, occurredAt: new Date().toISOString() }); person.role = payload.role; }
    refreshOutput(); return new Response(null, { status: 204 });
  }
  if (path === '/v1/admin/access/workspaces') return Response.json({ workspaces: account.workspaces.map((w) => ({ ...w, members: 2 })), limit: 200 });
  if (path === '/v1/admin/access/clients') return Response.json({ clients: providers.map((p) => ({ provider: p.id, displayName: p.displayName, clientId: `${p.id}-public-client-id`, authority: 'https://identity.example.test', callbackPath: `/signin-${p.id}` })) });
  if (path === '/v1/admin/ingest/sources') return Response.json({ sources: ['document', 'repo', 'wordnet', 'unicode'], cliAvailable: true, host: 'fixture-server' });
  if (path === '/v1/admin/ingest/processes') return Response.json({ object: 'list', data: jobs });
  if (path === '/v1/admin/ingest/start') {
    if (rejectChanges) return Response.json({ error: { message: 'Source folder was not found on this server.' } }, { status: 400 });
    const job = { pid: 314, source: payload.source, path: payload.path, arguments: ['ingest', payload.source, payload.path].filter(Boolean), cli: 'laplace', started_at: new Date().toISOString() };
    jobs.push(job); mutations++; refreshOutput(); return Response.json({ ...job, status: 'started', object: 'ingest.process' });
  }
  if (path === '/v1/admin/ingest/stop') { jobs.length = 0; mutations++; refreshOutput(); return Response.json({ note: 'Stop requested. Already committed data remains available.' }); }
  if (path.startsWith('/v1/admin/services/')) {
    if (method === 'POST') { mutations++; refreshOutput(); }
    return Response.json({ service: path.split('/')[4], activeState: 'active', subState: 'running', unit: 'laplace-fixture.service', result: 'success', operatorStopped: false });
  }
  if (path === '/v1/op') return Response.json({ object: 'op.result', name: payload.name, rows: [], truncated_at: null });
  return Response.json({ error: { message: `Unimplemented fixture request: ${method} ${path}` } }, { status: 500 });
};
function Fixture() {
  const [, update] = useState(0); refreshOutput = () => update((n) => n + 1);
  return <>{query.get('preview') !== '1' && <div style={{ padding: '8px', display: 'flex', flexWrap: 'wrap', gap: '12px' }}>
    <span>Browser verification fixture</span><button onClick={() => { rejectChanges = !rejectChanges; }}>Toggle rejected changes</button>
    <button onClick={() => { platformRole = null; }}>Revoke platform role</button><button onClick={() => { failAccess = false; }}>Restore access read</button>
    <output aria-label="Submitted changes">{mutations}</output></div>}
    <div style={{ maxWidth: '100%', margin: '0 auto' }}>
      <AccountControls user={anonymous ? null : user} providers={providers} returnUrl="/operator" />
      <Routes><Route path="/settings" element={<SettingsView />} /><Route path="*" element={<AdminView />} /></Routes>
    </div></>;
}
createRoot(document.getElementById('root')!).render(<TooltipProvider><MemoryRouter initialEntries={[query.get('page') ?? '/operator']}><Fixture /></MemoryRouter></TooltipProvider>);
