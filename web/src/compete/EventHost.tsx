import { useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Button, ErrorText, Field, Input, Modal, Muted, Panel, Select, TextArea, useReadResource } from '@ui';
import { plainMessage } from '../api/client';
import { useAppStore } from '../store';
import { addEntrants, changeDraw, deleteEvent, readFormats, startEvent, withdraw, type HostedEvent } from './api';
import styles from './Events.module.css';

/** Runs one request at a time and keeps its failure in view. */
export function useAction() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const pending = useRef(false);
  async function run(action: () => Promise<void>) {
    if (pending.current) return;
    pending.current = true; setBusy(true); setError('');
    try { await action(); } catch (failure) { setError(plainMessage(failure)); } finally { pending.current = false; setBusy(false); }
  }
  return { busy, error, run };
}

export function ShareLink() {
  const [copied, setCopied] = useState(false);
  const link = window.location.href;
  return (
    <div className={styles.share}>
      <Field label="Share this link"><Input readOnly value={link} onFocus={(focus) => focus.target.select()} /></Field>
      <Button variant="ghost" onClick={() => { void navigator.clipboard?.writeText(link).then(() => setCopied(true)); }}>
        {copied ? 'Copied' : 'Copy link'}
      </Button>
    </div>
  );
}

/** What the host does before the start: fill the field, set the seeding and format, then start. */
export function HostSetup({ event, refresh }: { event: HostedEvent; refresh: () => Promise<void> }) {
  const { tenant, authUser } = useAppStore();
  const formats = useReadResource({ key: `event-formats ${JSON.stringify([tenant, authUser?.id])}`, read: readFormats });
  const navigate = useNavigate();
  const hosting = useAction();
  const [names, setNames] = useState('');
  const [added, setAdded] = useState('');
  const [confirmDelete, setConfirmDelete] = useState(false);
  const free = formats.data?.free_entrants;
  const count = event.entrants.length;
  const change = (action: () => Promise<unknown>) => void hosting.run(async () => { await action(); await refresh(); });

  function move(index: number, by: number) {
    const order = event.entrants.map((entrant) => entrant.id);
    [order[index], order[index + by]] = [order[index + by], order[index]];
    change(() => changeDraw(event.id, { order }));
  }

  return (
    <>
      <Panel title={`Entrants (${count})`}>
        {count === 0 ? <Muted>Share the link, or add entrants yourself below.</Muted> : (
          <ol className={styles.seeds}>
            {event.entrants.map((entrant, index) => (
              <li key={entrant.id}>
                <span>{entrant.name}</span>
                <span className={styles.actions}>
                  <Button variant="ghost" size="sm" disabled={hosting.busy || index === 0} aria-label={`Move ${entrant.name} up`} onClick={() => move(index, -1)}>Up</Button>
                  <Button variant="ghost" size="sm" disabled={hosting.busy || index === count - 1} aria-label={`Move ${entrant.name} down`} onClick={() => move(index, 1)}>Down</Button>
                  <Button variant="ghost" size="sm" disabled={hosting.busy} aria-label={`Remove ${entrant.name}`} onClick={() => change(() => withdraw(event.id, entrant.id))}>Remove</Button>
                </span>
              </li>
            ))}
          </ol>
        )}
        <form className={styles.join} onSubmit={(submit) => {
          submit.preventDefault();
          const list = names.split('\n').map((each) => each.trim()).filter(Boolean);
          if (list.length) change(async () => {
            const result = await addEntrants(event.id, list);
            setNames('');
            setAdded(`${result.added} added${result.skipped ? `, ${result.skipped} skipped as already registered or over the limit` : ''}.`);
          });
        }}>
          <Field label="Add entrants, one name per line"><TextArea rows={4} value={names} onChange={(typed) => setNames(typed.target.value)} /></Field>
          <Button type="submit" variant="ghost" disabled={hosting.busy || !names.trim()}>Add entrants</Button>
        </form>
        {added && <p role="status">{added}</p>}
      </Panel>

      <Panel title="Run this event">
        <div className={styles.join}>
          <Field label="Format">
            <Select value={event.format} disabled={hosting.busy || !formats.data} onChange={(picked) => change(() => changeDraw(event.id, { format: picked.target.value }))}>
              {formats.data?.formats.map((each) => <option key={each.id} value={each.id}>{each.title}</option>)}
              {!formats.data && <option value={event.format}>{event.format}</option>}
            </Select>
          </Field>
          <Button variant="ghost" disabled={hosting.busy || count < 2} onClick={() => change(() => changeDraw(event.id, { shuffle: true }))}>Draw at random</Button>
        </div>
        <ShareLink />
        <p>Starting closes registration and fixes the matches as drawn above.</p>
        {event.session && <p>Part of {event.session.title}.</p>}
        {free != null && (count <= free ? <Muted>Free to host up to {free} entrants.</Muted>
          : event.session?.covered ? <p>Hosting for this session is already paid, so this event starts at no further cost.</p>
          : <p>This event is over the free size of {free}. Starting it takes the flat hosting fee, unless this workspace is on the League plan.{event.session ? ` That one fee then covers the other events in this session for ${formats.data?.session_hours} hours.` : ''}</p>)}
        <div className={styles.actions}>
          <Button loading={hosting.busy} disabled={hosting.busy || count < 2 || !formats.data}
            onClick={() => void hosting.run(async () => {
              if (await startEvent(event.id, formats.data?.host_service ?? '') === 'started') await refresh();
            })}>Start event</Button>
          <Button variant="ghost" disabled={hosting.busy} onClick={() => setConfirmDelete(true)}>Delete event…</Button>
        </div>
        {count < 2 && <Muted>An event needs at least two entrants.</Muted>}
        {hosting.error && <ErrorText role="alert">{hosting.error}</ErrorText>}
        <Modal open={confirmDelete} onClose={() => setConfirmDelete(false)} title={`Delete ${event.title}?`} actions={<>
          <Button variant="ghost" onClick={() => setConfirmDelete(false)}>Keep it</Button>
          <Button loading={hosting.busy} onClick={() => void hosting.run(async () => { await deleteEvent(event.id); navigate('/events'); })}>Delete event</Button>
        </>}>
          <p>The event and its registrations are removed and its link stops working. This cannot be undone.</p>
        </Modal>
      </Panel>
    </>
  );
}
