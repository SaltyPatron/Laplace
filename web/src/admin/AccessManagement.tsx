import { useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { Button, ErrorText, Input, Modal, Muted, Panel, ReadStatus, Select, useReadResource } from '@ui';
import { apiGet, apiPut } from '../api/client';
import { roleName, type PlatformRole } from '../auth/access';
import { useAppStore } from '../store';
import styles from './Admin.module.css';

interface Person { userId: string; displayName: string | null; email: string | null; role: string | null; providers: string[] }
interface RoleEvent { eventId: number; userId: string; actor: string; actorName: string | null; person: string | null; previousRole: string | null; role: string | null; occurredAt: string }
const permissionNames: Record<string, string> = { 'platform.read': 'View ingestion and system activity', 'ingestion.manage': 'Start and stop ingestion',
  'services.manage': 'Manage host services', 'platform.maintain': 'Run maintenance and writable operations', 'platform.access.manage': 'Assign platform access' };

export function AccessManagement({ roles, onChanged }: { roles: PlatformRole[]; onChanged: () => void }) {
  const { authUser, tenant } = useAppStore();
  const scope = JSON.stringify([authUser?.id, tenant]);
  const [draft, setDraft] = useState('');
  const [search, setSearch] = useState('');
  const [editing, setEditing] = useState<Person | null>(null);
  const [role, setRole] = useState('');
  const [busy, setBusy] = useState(false);
  const pending = useRef(false);
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');
  const people = useReadResource({ key: JSON.stringify(['platform-people', scope, search]),
    read: (signal) => apiGet<{ people: Person[]; limit: number }>(`/v1/admin/access/people?search=${encodeURIComponent(search)}`, { signal }) });
  const events = useReadResource({ key: JSON.stringify(['platform-events', scope]),
    read: (signal) => apiGet<{ events: RoleEvent[]; limit: number }>('/v1/admin/access/events', { signal }) });
  const chosen = roles.find((item) => item.id === role);
  async function save() {
    if (!editing || pending.current) return;
    pending.current = true; setBusy(true); setError(''); setMessage('');
    try {
      await apiPut(`/v1/admin/access/people/${editing.userId}/role`, { role: role || null });
      setMessage(`Access updated for ${editing.email || editing.displayName || 'this account'}.`);
      setEditing(null); await Promise.all([people.refresh(), events.refresh()]); onChanged();
    } catch (failure) { setError(failure instanceof Error ? failure.message : String(failure)); }
    finally { pending.current = false; setBusy(false); }
  }
  return <>
    <Panel title="Find a person">
      <form className={styles.toolbar} onSubmit={(event) => { event.preventDefault(); setSearch(draft.trim()); }}>
        <Input aria-label="Search people by email or name" value={draft} onChange={(event) => setDraft(event.target.value)} placeholder="Email address or name" type="search" maxLength={160} />
        <Button type="submit">Search people</Button><Button variant="ghost" onClick={() => void people.refresh()}>Refresh people</Button>
      </form>
      <Muted>People appear after signing in. Microsoft and Google sign-ins may be separate accounts, even when the email matches.</Muted>
      <ReadStatus label="People" resource={people} />
      {message && <p role="status">{message}</p>}
      {people.data?.people.length === 0 && <p>No matching accounts. Check the email or ask the person to sign in first.</p>}
      <div className={styles.directory}>{people.data?.people.map((person) => <div className={styles.directoryRow} key={person.userId}>
        <div><strong>{person.email || person.displayName || 'Account without an email'}</strong><Muted>{person.displayName} · {person.providers.join(', ') || 'Provider not recorded'}{person.userId === authUser?.id ? ' · You' : ''}</Muted></div>
        <span>{roleName(person.role)}</span><Button variant="ghost" onClick={() => { setEditing(person); setRole(person.role ?? ''); setError(''); }}>Change role</Button>
      </div>)}</div>
      {people.data && <Muted>Up to {people.data.limit} accounts per search. Workspace membership is managed separately.</Muted>}
    </Panel>
    <Panel title="Platform roles"><div className={styles.taskCards}>{roles.map((item) => <div key={item.id}><strong>{item.name}</strong><p>{item.description}</p><ul>{item.permissions.map((permission) => <li key={permission}>{permissionNames[permission] ?? permission}</li>)}</ul></div>)}</div></Panel>
    <Panel title="Recent access changes"><ReadStatus label="Access history" resource={events} />
      {events.data?.events.length === 0 && <Muted>No platform role changes have been recorded.</Muted>}
      {events.data?.events.map((event) => <div className={styles.directoryRow} key={event.eventId}>
        <div><strong>{event.person ?? event.userId}</strong><Muted>{roleName(event.previousRole)} → {roleName(event.role)} · By {event.actorName ?? event.actor}</Muted></div>
        <time dateTime={event.occurredAt}>{new Date(event.occurredAt).toLocaleString()}</time>
      </div>)}
    </Panel>
    <Modal open={editing !== null} onClose={() => { if (!busy) setEditing(null); }} title="Change platform access" actions={<>
      <Button variant="ghost" disabled={busy} onClick={() => setEditing(null)}>Cancel</Button><Button loading={busy} disabled={busy || role === (editing?.role ?? '')} onClick={() => void save()}>Save access</Button></>}>
      <p><strong>{editing?.email || editing?.displayName}</strong> · {editing?.providers.join(', ')}</p>
      <label className={styles.field}>Platform role<Select value={role} onChange={(event) => setRole(event.target.value)} disabled={busy}>
        <option value="">No platform access</option>{roles.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}
      </Select></label>
      <p>{chosen?.description ?? 'Removes shared platform tools. Workspace memberships and API keys stay separate.'}</p>
      {chosen && <ul>{chosen.permissions.map((permission) => <li key={permission}>{permissionNames[permission] ?? permission}</li>)}</ul>}
      <Muted>Changes apply to the next request and are recorded in access history.</Muted>
      {error && <ErrorText role="alert">{error}</ErrorText>}
    </Modal>
  </>;
}

interface Workspace { tenantId: string; displayName: string; kind: string; members: number }
interface Client { provider: string; displayName: string; clientId: string; authority: string; callbackPath: string }
export function PlatformDirectory({ section }: { section: 'workspaces' | 'clients' }) {
  const { tenant, authUser } = useAppStore();
  const read = useReadResource({ key: JSON.stringify(['platform-directory', tenant, authUser?.id, section]),
    read: (signal) => apiGet<{ workspaces?: Workspace[]; clients?: Client[]; limit?: number }>(`/v1/admin/access/${section}`, { signal }) });
  return <Panel title={section === 'workspaces' ? 'Workspace directory' : 'Connected sign-in providers'}>
    <Button variant="ghost" onClick={() => void read.refresh()}>Refresh directory</Button><ReadStatus label="Directory" resource={read} />
    {section === 'workspaces' ? <>
      <Muted>These are account and billing workspaces. Membership does not create a private copy of the shared substrate.</Muted>
      <div className={styles.directory}>{read.data?.workspaces?.map((workspace) => <div className={styles.directoryRow} key={workspace.tenantId}>
        <div><strong>{workspace.displayName}</strong><Muted>{workspace.kind === 'organization' ? 'Organization' : 'Personal'} · {workspace.members} members</Muted></div>
        <details><summary>Workspace ID</summary><code>{workspace.tenantId}</code></details>
      </div>)}</div><Muted>Up to {read.data?.limit ?? 200} workspaces. <Link to="/settings?section=workspace">Manage your current workspace</Link>.</Muted>
    </> : <>
      <p>These clients enable browser sign-in. Application integrations use <Link to="/settings?section=clients">workspace API keys</Link>.</p>
      {read.data?.clients?.length === 0 && <Muted>No browser sign-in clients are configured on this host.</Muted>}
      {read.data?.clients?.map((client) => <section className={styles.clientCard} key={client.provider}><h3>{client.displayName}</h3><span className={styles.badge}>Configured</span>
        <dl><dt>Client ID</dt><dd><code>{client.clientId}</code></dd><dt>Authority</dt><dd>{client.authority}</dd><dt>Callback</dt><dd>{client.callbackPath}</dd></dl>
      </section>)}<Muted>Client secrets are managed by the installation and never returned to this page.</Muted>
    </>}
  </Panel>;
}
