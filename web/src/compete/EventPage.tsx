import { useState } from 'react';
import { Link as RouterLink, useParams } from 'react-router-dom';
import { Button, ErrorText, Field, Input, Muted, Panel, ReadStatus, useReadResource } from '@ui';
import { useAppStore } from '../store';
import { readEvent, register, type HostedEvent } from './api';
import { CompetitionView } from './CompetitionView';
import { HostSetup, ShareLink, useAction } from './EventHost';
import { FrozenNotice, Keeping } from './Keeping';
import { ResultEntry } from './ResultEntry';
import type { Match } from './model';
import styles from './Events.module.css';

/** One event, for whoever has its link: register while it is open, follow it once it is under way. The host also runs it. */
export function EventPage() {
  const { id = '' } = useParams();
  const { tenant, authUser } = useAppStore();
  const event = useReadResource({
    key: JSON.stringify(['event', id, tenant, authUser?.id]), refreshMs: 15000,
    read: (signal) => readEvent(id, signal),
  });
  if (!event.data) return <Panel title="Event"><ReadStatus label="Event" resource={event} /></Panel>;
  return (
    <div className={styles.page}>
      {event.data.host && <nav aria-label="Events"><RouterLink to="/events">All your events</RouterLink></nav>}
      <FrozenNotice retention={event.data.retention} />
      {event.data.status === 'registration'
        ? <Registration event={event.data} refresh={event.refresh} />
        : <UnderWay event={event.data} refresh={event.refresh} />}
    </div>
  );
}

interface StageProps { event: HostedEvent; refresh: () => Promise<void> }

function Registration({ event, refresh }: StageProps) {
  const joining = useAction();
  const [name, setName] = useState('');
  const [joined, setJoined] = useState('');
  const count = event.entrants.length;

  return (
    <div className={styles.page}>
      <Panel title={event.title}>
        <p>Registration is open. {count === 0 ? 'Nobody has registered yet.' : `${count} registered so far.`}</p>
        <form className={styles.join} onSubmit={(submit) => {
          submit.preventDefault();
          const entered = name.trim();
          if (entered) void joining.run(async () => { await register(event.id, entered); setJoined(entered); setName(''); await refresh(); });
        }}>
          <Field label="Your name"><Input value={name} maxLength={60} required autoComplete="name" onChange={(change) => setName(change.target.value)} /></Field>
          <Button type="submit" loading={joining.busy} disabled={joining.busy || !name.trim()}>Register</Button>
        </form>
        {joined && !joining.error && <p role="status">{joined} is registered.</p>}
        {joining.error && <ErrorText role="alert">{joining.error}</ErrorText>}
      </Panel>

      <Panel>
        {event.matches.length === 0
          ? <Muted>The draw appears here once two entrants have registered.</Muted>
          : <>
            <CompetitionView competition={{ ...event, title: 'The draw so far' }} />
            <Muted>Entrants are seeded in the order shown below. The draw changes as people register, until the host starts the event.</Muted>
          </>}
      </Panel>

      {event.host ? <HostSetup event={event} refresh={refresh} /> : (
        <Panel title={`Entrants (${count})`}>
          {count === 0 ? <Muted>Nobody has registered yet.</Muted> : (
            <ol className={styles.entrants}>{event.entrants.map((entrant) => <li key={entrant.id}><span>{entrant.name}</span></li>)}</ol>
          )}
        </Panel>
      )}
    </div>
  );
}
function UnderWay({ event, refresh }: StageProps) {
  const [scoring, setScoring] = useState<Match | null>(null);
  const ready = event.matches.filter((match) => match.slots.every((slot) => slot.entrant != null));
  const open = ready.filter((match) => match.state !== 'final');
  const names = new Map(event.entrants.map((entrant) => [entrant.id, entrant.name]));
  const pairing = (match: Match) => match.slots.map((slot) => names.get(slot.entrant ?? '') ?? '').join(' v ');
  return (
    <div className={styles.page}>
      <Panel><CompetitionView competition={event} /></Panel>
      {event.host && (
        <Panel title="Enter results">
          {open.length === 0 && <Muted>{ready.length === event.matches.length ? 'Every match has a result.' : 'No match is waiting for a result right now.'}</Muted>}
          <ul className={styles.results}>
            {ready.map((match) => (
              <li key={match.id} data-final={match.state === 'final' || undefined}>
                <span><strong>{match.name ?? match.id}</strong> · {pairing(match)}</span>
                <Button variant={match.state === 'final' ? 'ghost' : 'primary'} size="sm" onClick={() => setScoring(match)}>
                  {match.state === 'final' ? 'Change result' : 'Enter result'}
                </Button>
              </li>
            ))}
          </ul>
          <ShareLink />
          <Keeping event={event} />
        </Panel>
      )}
      {scoring && <ResultEntry event={event} match={scoring} onClose={() => setScoring(null)} onSaved={async () => { setScoring(null); await refresh(); }} />}
    </div>
  );
}
