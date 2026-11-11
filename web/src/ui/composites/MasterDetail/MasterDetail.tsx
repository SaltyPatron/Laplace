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
  // The layout follows the space the workspace actually has, not the window: beside the rail a
  // wide window can still leave too little room for two panes.
  return <section aria-label={label} className={styles.workspace}>
    <div className={`${styles.panes} ${selected ? styles.selected : ''}`}>
      <div className={styles.master} data-collection-master>{master}</div>
      <article className={styles.detail} aria-label="Selected record" data-collection-detail>
        {selected && <div className={styles.back}><Button variant="ghost" onClick={onBack}>Back to results</Button></div>}
        {children}
      </article>
    </div>
  </section>;
}
