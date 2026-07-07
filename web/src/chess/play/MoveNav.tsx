import { IconButton, cn } from '@ui';
import { plyLabel } from '../gameHistory';
import styles from './MoveNav.module.css';

export interface MoveNavProps {
  viewPly: number;
  livePly: number;
  reviewing: boolean;
  onFirst: () => void;
  onPrev: () => void;
  onNext: () => void;
  onLast: () => void;
}

export function MoveNav({
  viewPly,
  livePly,
  reviewing,
  onFirst,
  onPrev,
  onNext,
  onLast,
}: MoveNavProps) {
  const atStart = viewPly <= 0;
  const atLive = viewPly >= livePly;

  return (
    <div className={styles.nav}>
      <div className={styles.controls}>
        <IconButton aria-label="First move" disabled={atStart} onClick={onFirst}>|◀</IconButton>
        <IconButton aria-label="Previous move" disabled={atStart} onClick={onPrev}>◀</IconButton>
        <IconButton aria-label="Next move" disabled={atLive} onClick={onNext}>▶</IconButton>
        <IconButton aria-label="Latest move" disabled={atLive} onClick={onLast}>▶|</IconButton>
      </div>
      <div className={cn(styles.label, reviewing && styles.reviewing)}>
        {reviewing ? <>Reviewing · <b>{plyLabel(viewPly, livePly)}</b></> : (
          <>Move <b>{viewPly}</b> / {livePly}</>
        )}
      </div>
    </div>
  );
}
