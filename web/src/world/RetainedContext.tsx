import { type InformationWorld } from './context';
import styles from './WorldContext.module.css';

/** Report carried coordinates that the current collection adapter cannot apply. */
export function RetainedContext({ world }: { world: InformationWorld }) {
  const fields: [string, string[]][] = [
    ['World', world.domain], ['Source', world.sources], ['Evidence', world.evidence],
    ['Epoch', world.epoch ? [world.epoch] : []], ['From', world.from ? [world.from] : []],
    ['Until', world.until ? [world.until] : []], ['Ranking', world.ranking ? [world.ranking] : []],
    ['Grouping', world.grouping ? [world.grouping] : []], ['Additional comparisons', world.comparison.slice(1)],
  ];
  const active = fields.filter(([, values]) => values.length);
  if (!active.length) return null;
  return <aside className={styles.retained} aria-label="Retained context"><p>These coordinates remain in your address, but this search does not apply them as constraints.</p>
    <dl className={styles.coordinates}>{active.map(([label, values]) => <div key={label}><dt>{label}</dt><dd>{values.join(' · ')}</dd></div>)}</dl>
  </aside>;
}
