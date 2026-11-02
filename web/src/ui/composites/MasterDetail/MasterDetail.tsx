import type { ReactNode } from 'react';
import { Button } from '../../primitives/Button';
import styles from './MasterDetail.module.css';

export function MasterDetail({ label, master, selected, onBack, children }: {
  label: string;
  master: ReactNode;
  selected: boolean;
  onBack: () => void;
  children: ReactNode;
}) {
  return <section aria-label={label} className={`${styles.workspace} ${selected ? styles.selected : ''}`}>
    <div className={styles.master} data-collection-master>{master}</div>
    <article className={styles.detail} aria-label="Selected record" data-collection-detail>
      {selected && <div className={styles.back}><Button variant="ghost" onClick={onBack}>Back to results</Button></div>}
      {children}
    </article>
  </section>;
}
