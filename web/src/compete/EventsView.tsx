import { useRef, useState } from 'react';
import { Link as RouterLink, Route, Routes, useNavigate } from 'react-router-dom';
import { Button, ErrorText, Field, Input, Muted, Panel, ReadStatus, Select, Table, TableScroll, Td, Th, useReadResource } from '@ui';
import { plainMessage } from '../api/client';
import { useAppStore } from '../store';
import { createEvent, createSession, readEvents, readFormats, readSessions } from './api';
import { EventPage } from './EventPage';
import styles from './Events.module.css';

export function EventsView() {
  return (
    <Routes>
      <Route index element={<EventList />} />
      <Route path=":id" element={<EventPage />} />
    </Routes>
  );
}

/** A host's events and the form that makes a new one. A visitor without an account sees what hosting is. */
function EventList() {
  const { tenant, authUser, authProviders, devPrincipal, servicePrincipal } = useAppStore();
  // With no sign-in configured the host runs as one workspace, and that workspace hosts.
  const canHost = !!(authUser || servicePrincipal || devPrincipal) || authProviders.length === 0;
  const scope = JSON.stringify([tenant, authUser?.id]);
  const formats = useReadResource({ key: `event-formats ${scope}`, enabled: canHost, read: readFormats });
  const events = useReadResource({ key: `events ${scope}`, enabled: canHost, read: readEvents });
  const sessions = useReadResource({ key: `event-sessions ${scope}`, enabled: canHost, read: readSessions });
  const navigate = useNavigate();
  const [title, setTitle] = useState('');
  const [format, setFormat] = useState('');
  // '' is no session, 'new' makes one from the name typed, anything else is an existing session.
  const [session, setSession] = useState('');
  const [sessionTitle, setSessionTitle] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const pending = useRef(false);
  const chosen = formats.data?.formats.find((each) => each.id === format) ?? formats.data?.formats[0];

  async function create() {
    if (pending.current || !chosen || !title.trim() || (session === 'new' && !sessionTitle.trim())) return;
    pending.current = true; setBusy(true); setError('');
    try {
      const joined = session === 'new' ? (await createSession(sessionTitle.trim())).id : session || undefined;
      const { id } = await createEvent(title.trim(), chosen.id, joined);
      navigate(`/events/${id}`);
    } catch (failure) {
      setError(plainMessage(failure));
    } finally { pending.current = false; setBusy(false); }
  }

  if (!canHost) {
    return (
      <Panel title="Host an event">
        <p>Run a tournament, league night, sweeper or ladder: share one link, let people register themselves, and enter results as they come in.</p>
        <Muted>Sign in to host. Anyone with an event’s link can view it and register without an account.</Muted>
      </Panel>
    );
  }
  return (
    <div className={styles.page}>
      <Panel title="Host an event">
        <form className={styles.create} onSubmit={(submit) => { submit.preventDefault(); void create(); }}>
          <Field label="Title"><Input value={title} maxLength={120} required placeholder="Friday sweeper" onChange={(change) => setTitle(change.target.value)} /></Field>
          <Field label="Format" help={chosen?.summary}>
            <Select value={chosen?.id ?? ''} onChange={(change) => setFormat(change.target.value)}>
              {formats.data?.formats.map((each) => <option key={each.id} value={each.id}>{each.title}</option>)}
            </Select>
          </Field>
          <Field label="Session">
            <Select value={session} onChange={(change) => setSession(change.target.value)}>
              <option value="">On its own</option>
              {sessions.data?.sessions.map((each) => <option key={each.id} value={each.id}>{each.title}</option>)}
              <option value="new">New session…</option>
            </Select>
          </Field>
          {session === 'new' && (
            <Field label="Session name"><Input value={sessionTitle} maxLength={120} required placeholder="Tuesday league night" onChange={(change) => setSessionTitle(change.target.value)} /></Field>
          )}
          <Button type="submit" loading={busy} disabled={busy || !chosen || !title.trim() || (session === 'new' && !sessionTitle.trim())}>Create event</Button>
        </form>
        {formats.data && (
          <Muted>Events of up to {formats.data.free_entrants} entrants are free to host. Larger ones cost a flat hosting fee, or nothing extra on the League plan. Events in the same session share one fee: once one is paid for, the rest start free for {formats.data.session_hours} hours. Laplace never handles entry fees or prizes.</Muted>
        )}
        {error && <ErrorText role="alert">{error}</ErrorText>}
      </Panel>
      <Panel title="Your events">
        <ReadStatus label="Your events" resource={events} />
        {events.data && events.data.competitions.length === 0 && <Muted>No events yet. Create one above and share its link.</Muted>}
        {events.data && events.data.competitions.length > 0 && (
          <TableScroll>
            <Table>
              <thead><tr><Th scope="col">Event</Th><Th scope="col">Session</Th><Th scope="col">Format</Th><Th scope="col">Status</Th><Th scope="col" className={styles.number}>Entrants</Th></tr></thead>
              <tbody>
                {events.data.competitions.map((event) => (
                  <tr key={event.id}>
                    <Td><RouterLink to={`/events/${event.id}`}>{event.title}</RouterLink></Td>
                    <Td>{sessions.data?.sessions.find((each) => each.id === event.session)?.title ?? ''}</Td>
                    <Td>{formats.data?.formats.find((each) => each.id === event.format)?.title ?? event.format}</Td>
                    <Td>{event.status === 'registration' ? 'Registration open' : 'Under way'}</Td>
                    <Td className={styles.number}>{event.entrants}</Td>
                  </tr>
                ))}
              </tbody>
            </Table>
          </TableScroll>
        )}
      </Panel>
    </div>
  );
}
