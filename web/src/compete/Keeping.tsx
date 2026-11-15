import { Button, Muted } from '@ui';
import type { HostedEvent, Retention } from './api';
import styles from './Events.module.css';

const day = (when: string) => new Date(when).toLocaleDateString(undefined, { day: 'numeric', month: 'long', year: 'numeric' });

/** Says when a workspace's events are read-only, and when they will be deleted. */
export function FrozenNotice({ retention }: { retention: Retention }) {
  if (!retention.frozen || !retention.delete_on) return null;
  return (
    <p role="status" className={styles.notice}>
      This workspace’s plan has ended, so its events are read-only. They will be deleted on {day(retention.delete_on)} unless the plan is renewed.
    </p>
  );
}

/** For the host: how long this event is kept, and a copy of it to keep. */
export function Keeping({ event }: { event: HostedEvent }) {
  function download() {
    const link = document.createElement('a');
    link.href = URL.createObjectURL(new Blob([JSON.stringify(event, null, 2)], { type: 'application/json' }));
    link.download = `${event.title}.json`;
    link.click();
    URL.revokeObjectURL(link.href);
  }
  return (
    <div className={styles.share}>
      <Muted>
        {event.retention.delete_on
          ? `This event is kept until ${day(event.retention.delete_on)}. A plan that includes hosting keeps events for as long as it is active.`
          : 'This event is kept for as long as this workspace’s plan is active.'}
      </Muted>
      <Button variant="ghost" onClick={download}>Download results</Button>
    </div>
  );
}
