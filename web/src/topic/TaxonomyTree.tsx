import { Link as RouterLink } from 'react-router-dom';
import { Muted } from '@ui';
import type { TaxonomyResponse } from '../query/api';
import styles from './TaxonomyTree.module.css';

/**
 * The IS_A tree as an actual visual: the parent chain climbs as a connected
 * ladder to the root (organism, entity…), the topic sits highlighted at the
 * fork, and its strongest children branch below. Every rung navigates. This is
 * the witnessed taxonomy drawn as a tree — not a prose list.
 */
export function TaxonomyTree({ tax }: { tax: TaxonomyResponse }) {
  if (tax.up.length === 0 && tax.children.length === 0) {
    return <Muted>No witnessed IS_A edges around this topic.</Muted>;
  }

  // walk_strongest emits topic→parent→…→root; the ladder reads top-down from
  // the root, so reverse it.
  const chain = [...tax.up].reverse();

  return (
    <div className={styles.tree}>
      <ol className={styles.ladder}>
        {chain.map((n) => (
          <li key={n.id} className={styles.rung}>
            <RouterLink className={styles.node} to={`/topic/${encodeURIComponent(n.label)}`}>
              {n.label}
            </RouterLink>
            <span className={styles.drop} aria-hidden="true" />
          </li>
        ))}
        <li className={styles.rung}>
          <span className={`${styles.node} ${styles.self}`}>{tax.root_label}</span>
        </li>
      </ol>

      {tax.children.length > 0 && (
        <div className={styles.branches}>
          <span className={styles.branchStem} aria-hidden="true" />
          <div className={styles.kids}>
            {tax.children.map((c) => (
              <RouterLink key={c.id} className={styles.kid} to={`/topic/${encodeURIComponent(c.label)}`}>
                {c.label}
              </RouterLink>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
