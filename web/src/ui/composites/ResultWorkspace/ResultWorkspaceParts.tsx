/** Leaf pieces of ResultWorkspace: truncating cell values and the selected-row field comparison. */
import { Button } from '../../primitives/Button';
import { rowFields, rowValue, valueText, type SelectedRow } from '../../lib/resultRows';
import styles from './ResultWorkspace.module.css';

export function SelectedComparison<T extends object>({
  selected,
  comparePage,
  setComparePage,
  nameOf,
}: {
  selected: SelectedRow<T>[];
  comparePage: number;
  setComparePage: (page: number) => void;
  nameOf: (entry: SelectedRow<T>) => string;
}) {
  const comparisonPageCount = Math.max(1, Math.ceil(selected.length / 4));
  const comparisonPage = Math.min(comparePage, comparisonPageCount - 1);
  const comparing = selected.slice(comparisonPage * 4, comparisonPage * 4 + 4);
  const comparedFields = rowFields(comparing.map((entry) => entry.row));
  return (
    <section aria-label="Selected field comparison">
      <h4>Field comparison — received values, not a semantic ranking</h4>
      <p className={styles.meta}>
        Records {comparisonPage * 4 + 1}–{Math.min(comparisonPage * 4 + 4, selected.length)} of {selected.length} selected. Each retains its own response boundary.
      </p>
      <div className={styles.scroll} tabIndex={0} role="region" aria-label="Comparison table">
        <table className={styles.table}>
          <thead>
            <tr>
              <th scope="col">Field</th>
              {comparing.map((entry) => (
                <th scope="col" key={entry.key}>
                  {nameOf(entry)}
                  <small>{entry.snapshot.receivedAt} · row {entry.index + 1}</small>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {comparedFields.map((field) => (
              <tr key={field}>
                <th scope="row">{field}</th>
                {comparing.map((entry) => <td key={entry.key}><CellValue value={rowValue(entry.row, field)} /></td>)}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div className={styles.toolbar}>
        <Button variant="ghost" disabled={comparisonPage === 0} onClick={() => setComparePage(comparisonPage - 1)}>
          Previous comparison
        </Button>
        <Button
          variant="ghost"
          disabled={comparisonPage + 1 === comparisonPageCount}
          onClick={() => setComparePage(comparisonPage + 1)}
        >
          Next comparison
        </Button>
      </div>
    </section>
  );
}

export function CellValue({ value }: { value: unknown }) {
  const text = valueText(value);
  if (text.length > 160) {
    return (
      <details>
        <summary>{text.slice(0, 160)}…</summary>
        <pre className={styles.fullValue}>{text}</pre>
      </details>
    );
  }
  return <span className={value == null || value === '' ? styles.meta : styles.cellValue}>{text}</span>;
}
