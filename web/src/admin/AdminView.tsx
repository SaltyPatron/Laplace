import { lazy, Suspense, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Button, LoadingText, Muted, Panel, ReadStatus, Select } from '@ui';
import { useSectionParam } from '../layout/useSectionParam';
import { useAppStore } from '../store';
import { AccountControls } from '../auth/AccountControls';
import { roleName, useAccess } from '../auth/access';
import styles from './Admin.module.css';

const IngestControl = lazy(() => import('./IngestControl').then((m) => ({ default: m.IngestControl })));
const IngestJournal = lazy(() => import('./IngestJournal').then((m) => ({ default: m.IngestJournal })));
const Activity = lazy(() => import('./Activity').then((m) => ({ default: m.Activity })));
const OpConsole = lazy(() => import('./OpConsole').then((m) => ({ default: m.OpConsole })));
const Repair = lazy(() => import('./Repair').then((m) => ({ default: m.Repair })));
const Agents = lazy(() => import('./Agents').then((m) => ({ default: m.Agents })));
const Services = lazy(() => import('./Services').then((m) => ({ default: m.Services })));
const AccessManagement = lazy(() => import('./AccessManagement').then((m) => ({ default: m.AccessManagement })));
const PlatformDirectory = lazy(() => import('./AccessManagement').then((m) => ({ default: m.PlatformDirectory })));
const SECTIONS = ['overview', 'ingest', 'activity', 'services', 'access', 'workspaces', 'clients', 'ops', 'repair', 'agents'] as const;
type Section = typeof SECTIONS[number];
const tools: { id: Section; label: string; description: string; permission: string }[] = [
  { id: 'ingest', label: 'Ingestion', description: 'Choose a source, start a run, and follow its progress.', permission: 'platform.read' },
  { id: 'activity', label: 'System activity', description: 'Inspect active database work and long-running requests.', permission: 'platform.read' },
  { id: 'services', label: 'Services', description: 'Check and manage the MCP and Lichess services on this host.', permission: 'platform.read' },
  { id: 'access', label: 'People & roles', description: 'Find people by email and manage platform permissions.', permission: 'platform.access.manage' },
  { id: 'workspaces', label: 'Workspaces', description: 'Review organizations, personal workspaces, and membership counts.', permission: 'platform.access.manage' },
  { id: 'clients', label: 'Sign-in clients', description: 'Inspect the Microsoft and Google clients active on this host.', permission: 'platform.access.manage' },
  { id: 'ops', label: 'Operation console', description: 'Inspect and run installed platform operations.', permission: 'platform.maintain' },
  { id: 'repair', label: 'Database maintenance', description: 'Review storage and perform maintenance.', permission: 'platform.maintain' },
  { id: 'agents', label: 'Agents', description: 'Configure connected agents and inspect their status.', permission: 'platform.maintain' },
];
export function AdminView() {
  const navigate = useNavigate();
  const [section, setSection] = useSectionParam('section', [...SECTIONS], 'overview');
  const { authReady, authUser, authProviders, devPrincipal } = useAppStore();
  const access = useAccess();
  const [revision, setRevision] = useState(0);
  const permissions = access.error ? [] : access.data?.platform.permissions ?? [];
  const available = tools.filter((tool) => permissions.includes(tool.permission));
  const selected = tools.find((tool) => tool.id === section);
  const allowed = section === 'overview' || !!selected && permissions.includes(selected.permission);
  if (!authReady) return <div className={styles.page}><LoadingText>Loading administration…</LoadingText></div>;
  if (!authUser && !devPrincipal) return <div className={styles.page}><Panel title="Administration">
    <p>Sign in to manage your workspace or use the platform tools assigned to your account.</p>
    <AccountControls user={null} providers={authProviders} returnUrl="/operator" />
  </Panel></div>;
  return <div className={styles.page}>
    <header className={styles.header}>
      <div><span className={styles.eyebrow}>Manage Laplace</span><h1 className={styles.title}>Administration</h1><Muted>Workspace access and shared platform operations, in one place.</Muted></div>
      <div className={styles.accessSummary}><span className={styles.badge}>{access.error ? 'Access unavailable' : access.data ? roleName(access.data.platform.role) : 'Checking access…'}</span><Button variant="ghost" onClick={() => void access.refresh()}>Refresh access</Button></div>
    </header>
    <ReadStatus label="Your access" resource={access} />
    <label className={styles.mobileNav}>Administration page<Select value={section} onChange={(event) => {
      const next = event.target.value; if (next.startsWith('/')) navigate(next); else setSection(next as Section);
    }}><option value="overview">Overview</option><optgroup label="Your workspace">
      <option value="/settings?section=workspace">Workspace settings</option><option value="/settings?section=members">Members & invitations</option>
      <option value="/settings?section=clients">API clients</option><option value="/settings?section=permissions">Roles & permissions</option>
    </optgroup>{available.length > 0 && <optgroup label="Platform">{available.map((tool) => <option key={tool.id} value={tool.id}>{tool.label}</option>)}</optgroup>}</Select></label>
    <div className={styles.adminLayout}>
      <nav className={styles.adminNav} aria-label="Administration sections">
        <button aria-current={section === 'overview' ? 'page' : undefined} onClick={() => setSection('overview')}>Overview</button>
        <span className={styles.navGroup}>Your workspace</span>
        <Link to="/settings?section=workspace">Workspace settings</Link><Link to="/settings?section=members">Members & invitations</Link>
        <Link to="/settings?section=clients">API clients</Link><Link to="/settings?section=permissions">Roles & permissions</Link>
        {available.length > 0 && <span className={styles.navGroup}>Platform</span>}
        {available.map((tool) => <button key={tool.id} aria-current={section === tool.id ? 'page' : undefined} onClick={() => setSection(tool.id)}>{tool.label}</button>)}
      </nav>
      <section className={styles.adminContent} aria-label={selected?.label ?? 'Administration overview'}>
        {!access.data || access.error ? <Panel title="Checking your access"><p>{access.error ? 'Your permissions could not be loaded. Retry the access check above to open platform tools.' : 'Loading the tools assigned to your account…'}</p></Panel>
          : !allowed ? <Panel title="Access needed"><p>This tool requires an assigned platform role. A workspace administrator manages their own workspace; shared system operations need platform access.</p><Button onClick={() => setSection('overview')}>Back to overview</Button></Panel>
          : section === 'overview' ? <>
            <Panel title="Your workspace"><div className={styles.taskCards}>
              <Link to="/settings?section=members"><strong>Manage your team</strong><span>Invite people and review their workspace roles.</span></Link>
              <Link to="/settings?section=clients"><strong>Connect an application</strong><span>Create and revoke workspace API keys.</span></Link>
              <Link to="/data"><strong>Add data</strong><span>Upload local files and inspect admitted content.</span></Link>
              <Link to="/settings?section=billing"><strong>Plans & billing</strong><span>Review your subscription and billing access.</span></Link>
            </div></Panel>
            <Panel title="Platform tools">{available.length ? <div className={styles.taskCards}>{available.map((tool) => <button key={tool.id} onClick={() => setSection(tool.id)}><strong>{tool.label}</strong><span>{tool.description}</span></button>)}</div>
              : <p>Your account has no platform role. You can use your workspace tools above. A platform administrator can find your sign-in email in People & roles to assign access.</p>}</Panel>
          </> : <>
            <div><h2 className={styles.sectionTitle}>{selected?.label}</h2><Muted>{selected?.description}</Muted></div>
            <Suspense fallback={<LoadingText>Loading {selected?.label.toLowerCase()}…</LoadingText>}>
              {section === 'ingest' ? <><div className={styles.toolbar}><Link to="/data">Upload local files</Link><Link to="/explore/warehouse">Browse admitted sources</Link><Muted>Server ingestion affects the shared substrate.</Muted></div>
                {permissions.includes('ingestion.manage') && <IngestControl onStarted={() => setRevision((value) => value + 1)} />}
                <IngestJournal refreshSignal={revision} canManage={permissions.includes('ingestion.manage')} /></>
                : section === 'activity' ? <Activity canManage={permissions.includes('platform.maintain')} />
                : section === 'services' ? <Services canManage={permissions.includes('services.manage')} />
                : section === 'access' ? <AccessManagement roles={access.data?.roles ?? []} onChanged={() => void access.refresh()} />
                : section === 'workspaces' || section === 'clients' ? <PlatformDirectory section={section} />
                : section === 'ops' ? <OpConsole /> : section === 'repair' ? <Repair /> : <Agents />}
            </Suspense>
          </>}
      </section>
    </div>
  </div>;
}
