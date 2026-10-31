import { useEffect, useRef, useState } from 'react';
import { Button, ErrorText, Muted, Panel } from '@ui';
import { apiPost } from '../api/client';
import { roleName } from './access';
import styles from './SettingsView.module.css';

interface InvitationPreview {
  workspaceName: string; role: string; expiresAt: string; invitedBy: string; currentRole: string | null;
}

export function WorkspaceInvitation({ token, selectWorkspace, onAccepted, onDismiss }: {
  token: string; selectWorkspace: (tenantId: string) => Promise<void>; onAccepted: () => void; onDismiss: () => void;
}) {
  const [preview, setPreview] = useState<InvitationPreview | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);
  const [revision, refresh] = useState(0);
  const [busy, setBusy] = useState(false);
  const [acceptedTenant, setAcceptedTenant] = useState<string | null>(null);
  const submitting = useRef(false);
  useEffect(() => {
    const controller = new AbortController();
    setLoading(true); setError(''); setPreview(null);
    void apiPost<InvitationPreview>('/v1/account/invitations/preview', { token }, { signal: controller.signal }).then(
      (value) => { if (!controller.signal.aborted) setPreview(value); },
      (failure: unknown) => { if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : 'Unable to read this invitation.'); },
    ).finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [token, revision]);

  async function accept() {
    if (!preview || submitting.current) return;
    submitting.current = true; setBusy(true); setError('');
    let destination = acceptedTenant;
    try {
      if (!destination) {
        const result = await apiPost<{ tenantId: string }>('/v1/account/invitations/accept', { token });
        destination = result.tenantId;
        setAcceptedTenant(destination);
        onAccepted();
      }
      await selectWorkspace(destination);
    } catch (failure) {
      const detail = failure instanceof Error ? failure.message : 'The request failed.';
      setError(destination ? `You joined the workspace, but switching to it failed. ${detail}` : detail);
    } finally { submitting.current = false; setBusy(false); }
  }

  return <Panel title="Workspace invitation">
    {loading && <Muted>Checking the invitation…</Muted>}
    {preview && <>
      <h2>Join {preview.workspaceName}</h2>
      <dl className={styles.configuration}>
        <dt>Invited by</dt><dd>{preview.invitedBy}</dd>
        <dt>Workspace role</dt><dd>{roleName(preview.currentRole ?? preview.role)}</dd>
        <dt>Expires</dt><dd>{new Date(preview.expiresAt).toLocaleString()}</dd>
      </dl>
      <p>{preview.currentRole ? 'You already belong to this workspace. Your existing role will be kept.'
        : 'Accepting adds this signed-in account to the workspace. Shared platform administration is assigned separately.'}</p>
    </>}
    {error && <ErrorText role="alert">{error}</ErrorText>}
    {acceptedTenant && <p role="status">Invitation accepted. You can open this workspace using the button below or the workspace picker.</p>}
    <div className={styles.actions}>
      {preview && <Button disabled={busy} loading={busy} onClick={() => void accept()}>{acceptedTenant ? 'Open workspace' : 'Accept invitation'}</Button>}
      {!preview && !loading && <Button onClick={() => refresh((value) => value + 1)}>Retry invitation</Button>}
      <Button variant="ghost" disabled={busy} onClick={onDismiss}>{acceptedTenant ? 'Dismiss' : 'Not now'}</Button>
    </div>
  </Panel>;
}
