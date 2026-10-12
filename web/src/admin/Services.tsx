import { useRef, useState } from 'react';
import { Button, ErrorText, Modal, Muted, Panel, ReadStatus, useReadResource } from '@ui';
import { ApiError, apiGet, apiPost } from '../api/client';
import { useAppStore } from '../store';
import styles from './Admin.module.css';

interface ServiceStatus { service: string; activeState: string; subState: string; unit: string; result: string; operatorStopped: boolean }
type Action = 'start' | 'stop' | 'restart';
const labels = { start: 'Start', stop: 'Stop', restart: 'Restart' };
export function Services({ canManage }: { canManage: boolean }) {
  return <><Service name="mcp" title="MCP" description="Connects assistant clients to Laplace operations." canManage={canManage} />
    <Service name="lichess" title="Lichess" description="Runs the managed Lichess connection on this host." canManage={canManage} /></>;
}
function Service({ name, title, description, canManage }: { name: string; title: string; description: string; canManage: boolean }) {
  const { tenant, authUser } = useAppStore();
  const status = useReadResource({ key: JSON.stringify(['service', name, tenant, authUser?.id]), refreshMs: 10000,
    read: (signal) => apiGet<ServiceStatus>(`/v1/admin/services/${name}`, { signal }) });
  const [action, setAction] = useState<Action | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');
  const pending = useRef(false);
  async function submit() {
    if (!action || pending.current) return;
    pending.current = true; setBusy(true); setError(''); setMessage('');
    try {
      await apiPost(`/v1/admin/services/${name}/${action}`, {});
      setMessage(`${labels[action]} request accepted. Check the service status below for readiness.`);
      setAction(null); await status.refresh();
    } catch (failure) {
      setError(failure instanceof ApiError && failure.status === 409 ? 'A deployment or another service action is in progress. Refresh the status before trying again.'
        : failure instanceof ApiError && failure.status === 503 ? 'Service control is unavailable on this host. A platform administrator needs to check the managed service installation.'
        : failure instanceof Error ? failure.message : String(failure));
    }
    finally { pending.current = false; setBusy(false); }
  }
  return <Panel title={title}><p>{description}</p><ReadStatus label={`${title} status`} resource={status} />
    {message && <p role="status">{message}</p>}
    {status.data && <><p><strong>{status.data.activeState}</strong> · {status.data.subState}{status.data.operatorStopped ? ' · Stopped by an administrator' : ''}</p>
      <details><summary>Service details</summary><p>{status.data.unit} · Last result: {status.data.result}</p></details></>}
    <div className={styles.toolbar}><Button variant="ghost" onClick={() => void status.refresh()}>Refresh {title}</Button>
      {canManage && (['start', 'stop', 'restart'] as const).map((verb) => <Button key={verb} variant="ghost" disabled={busy || !status.data || !!status.error}
        onClick={() => { setAction(verb); setError(''); }}>{labels[verb]} {title}…</Button>)}</div>
    {!canManage && <Muted>Your role can view service status. An operations manager or platform administrator can change it.</Muted>}
    <Modal open={action !== null} onClose={() => { if (!busy) setAction(null); }} title={`${action ? labels[action] : ''} ${title}?`} actions={<>
      <Button variant="ghost" disabled={busy} onClick={() => setAction(null)}>Cancel</Button><Button loading={busy} disabled={busy} onClick={() => void submit()}>{action ? labels[action] : ''} service</Button></>}>
      <p>{action === 'start' ? `Start ${title} on this host.` : `${action === 'stop' ? 'Stopping' : 'Restarting'} ${title} interrupts its current connections on this host.`}</p>
      <p>The request may be accepted before the service is ready. Its status refreshes automatically.</p>{error && <ErrorText role="alert">{error}</ErrorText>}
    </Modal>
  </Panel>;
}
