import { Button } from '../../primitives/Button';
import { ErrorText, Muted } from '../../primitives/Text';
import type { ReadSnapshot } from '../../lib/readResource';
import styles from './ReadStatus.module.css';

export interface ReadStatusProps {
  label: string;
  resource: Pick<ReadSnapshot<unknown>, 'status' | 'error' | 'updatedAt'> & {
    refresh: () => Promise<void>;
    cancel: () => void;
  };
}

/** Keep unavailable, empty, refreshing, failed, and stopped reads distinct. */
export function ReadStatus({ label, resource }: ReadStatusProps) {
  const { status, error, updatedAt, refresh, cancel } = resource;
  if (status === 'ready' || status === 'idle') return null;
  const busy = status === 'loading' || status === 'refreshing';
  return (
    <div className={styles.status}>
      {status === 'failed' ? <ErrorText role="alert">{label}: {error?.message ?? 'Read failed.'}</ErrorText>
        : <Muted role={status === 'loading' ? 'status' : undefined}>
          {busy ? `${label}: ${status === 'refreshing' ? 'refreshing' : 'loading'}…` : `Stopped waiting for ${label}.`}
        </Muted>}
      {updatedAt != null && <Muted>Showing the last successful read, at <time dateTime={new Date(updatedAt).toISOString()}>
        {new Date(updatedAt).toLocaleTimeString()}
      </time>.</Muted>}
      {busy ? <Button variant="ghost" size="sm" onClick={cancel}>Stop waiting</Button>
        : <Button variant="ghost" size="sm" onClick={() => void refresh()}>Retry {label}</Button>}
    </div>
  );
}
