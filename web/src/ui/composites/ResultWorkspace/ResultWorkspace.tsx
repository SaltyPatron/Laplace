import { useDeferredValue, useEffect, useId, useMemo, useRef, useState, type ReactNode } from 'react';
import { Button } from '../../primitives/Button';
import {
  rowFields,
  rowValue,
  valueText,
  receivedRow,
  selectReceivedRows,
  receivedRowsExport,
  type RowSnapshot,
  type SelectedRow,
} from '../../lib/resultRows';
import styles from './ResultWorkspace.module.css';

export interface ResultColumn<T extends object> {
  key: string;
  label: string;
  /** Display/navigation only. Values and exports always come from the received row. */
  render?: (row: T) => ReactNode;
  /** Optional read-only detail link/realization; table actions are never replayed here. */
  detail?: (row: T) => ReactNode;
}

export interface ResultWorkspaceProps<T extends object> {
  /** Changing principal/tenant/collection scope disposes all retained selections. */
  scopeKey: string;
  label: string;
  snapshot: RowSnapshot<T>;
  columns?: readonly ResultColumn<T>[];
  rowLabel?: (row: T, index: number) => string;
  /** Stable presentation key for an explicitly identified row, not selection identity. */
  rowKey?: (row: T, index: number) => string;
  filterText?: string;
  onFilterTextChange?: (text: string) => void;
  /** Operational receipt surfaces can opt out of generic working-set selection controls. */
  selectable?: boolean;
  /** Compress explanatory chrome without changing the received-row boundary or export semantics. */
  compact?: boolean;
}

export function ResultWorkspace<T extends object>(props: ResultWorkspaceProps<T>) {
  return <ReceivedWorkspace key={props.scopeKey} {...props} />;
}

function ReceivedWorkspace<T extends object>({
  label,
  snapshot,
  columns,
  rowLabel,
  rowKey,
  filterText,
  onFilterTextChange,
  selectable = true,
  compact = false,
}: ResultWorkspaceProps<T>) {
  const id = useId();
  const [pageState, setPageState] = useState(0);
  const [pageSize, setPageSize] = useState(25);
  const [visibility, setVisibility] = useState<Record<string, boolean>>({});
  const [localFilter, setLocalFilter] = useState('');
  const filter = filterText ?? localFilter;
  const deferredFilter = useDeferredValue(filter);
  const [selected, setSelected] = useState<SelectedRow<T>[]>([]);
  const [inspected, setInspected] = useState<SelectedRow<T> | null>(null);
  const [compare, setCompare] = useState(false);
  const [comparePage, setComparePage] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const pageCheckbox = useRef<HTMLInputElement>(null);
  const inspectHeading = useRef<HTMLHeadingElement>(null);
  const opener = useRef<HTMLButtonElement | null>(null);

  const fields = useMemo<ResultColumn<T>[]>(() => {
    const declared = columns ?? [];
    const named = new Set(declared.map((column) => column.key));
    return [
      ...declared,
      ...rowFields(snapshot.rows)
        .filter((key) => !named.has(key))
        .map((key) => ({ key, label: key })),
    ];
  }, [columns, snapshot.rows]);

  const isVisible = (key: string) =>
    visibility[key] ?? (!columns || columns.some((column) => column.key === key));
  const visible = fields.filter((field) => isVisible(field.key));
  const matching = useMemo(
    () => snapshot.rows.flatMap((row, index) =>
      !deferredFilter || Object.keys(row).some((key) => valueText(rowValue(row, key)).includes(deferredFilter))
        ? [index]
        : []),
    [snapshot.rows, deferredFilter],
  );
  const pageCount = Math.max(1, Math.ceil(matching.length / pageSize));
  const page = Math.min(pageState, pageCount - 1);
  const start = page * pageSize;
  const indices = matching.slice(start, start + pageSize);
  const keys = new Set(selected.map((entry) => entry.key));
  const checked = selectable
    ? indices.filter((index) => keys.has(receivedRow(snapshot, index).key)).length
    : 0;
  const previous = selectable
    ? selected.filter((entry) => entry.snapshot.id !== snapshot.id).length
    : 0;

  useEffect(() => {
    if (!selectable || !pageCheckbox.current) return;
    pageCheckbox.current.indeterminate = checked > 0 && checked < indices.length;
  }, [selectable, checked, indices.length]);

  useEffect(() => {
    if (inspected) inspectHeading.current?.focus();
  }, [inspected]);

  const nameOf = (entry: SelectedRow<T>) =>
    rowLabel?.(entry.row, entry.index) || `Returned row ${entry.index + 1}`;

  function toggle(indicesToSelect: number[], checkedValue: boolean) {
    if (!selectable) return;
    setSelected((current) => selectReceivedRows(current, snapshot, indicesToSelect, checkedValue));
  }

  function download(entries: SelectedRow<T>[]) {
    let url: string | undefined;
    try {
      const body = receivedRowsExport(entries);
      url = URL.createObjectURL(new Blob([body], { type: 'application/json;charset=utf-8' }));
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = 'laplace-received-rows.json';
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      setError(null);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : String(failure));
    } finally {
      if (url) {
        const objectUrl = url;
        setTimeout(() => URL.revokeObjectURL(objectUrl), 1000);
      }
    }
  }

  async function copyEntry(entry: SelectedRow<T>) {
    try {
      if (!navigator.clipboard?.writeText) {
        throw new Error('Clipboard is unavailable here. Select the displayed value to copy it, or export JSON.');
      }
      await navigator.clipboard.writeText(receivedRowsExport([entry]));
      setError(null);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : String(failure));
    }
  }

  const comparisonPageCount = Math.max(1, Math.ceil(selected.length / 4));
  const comparisonPage = Math.min(comparePage, comparisonPageCount - 1);
  const comparing = selected.slice(comparisonPage * 4, comparisonPage * 4 + 4);
  const comparedFields = rowFields(comparing.map((entry) => entry.row));
  const tableChromeColumns = visible.length + 1 + (selectable ? 1 : 0);

  return (
    <section aria-label={label} className={styles.workspace}>
      <p className={styles.boundary}>{snapshot.boundary}</p>
      <p className={styles.meta}>
        Received <time dateTime={snapshot.receivedAt}>{new Date(snapshot.receivedAt).toLocaleString()}</time>.
        {compact
          ? ' Server order preserved.'
          : ' Presentation pages preserve the server\'s returned order; they are not remote cursors or an all-matching selection.'}
      </p>

      <div className={styles.toolbar}>
        <label htmlFor={`${id}-find`}>{compact ? 'Find' : 'Find within received rows'}</label>
        <input
          id={`${id}-find`}
          type="search"
          value={filter}
          aria-describedby={compact ? undefined : `${id}-find-help`}
          onChange={(event) => {
            setLocalFilter(event.target.value);
            onFilterTextChange?.(event.target.value);
            setPageState(0);
          }}
        />
        {!compact && (
          <span id={`${id}-find-help`} className={styles.meta}>
            Case-sensitive; searches this response only.
          </span>
        )}
        <label htmlFor={`${id}-page-size`}>{compact ? 'Rows' : 'Rows per presentation page'}</label>
        <select
          id={`${id}-page-size`}
          value={pageSize}
          onChange={(event) => {
            setPageSize(Number(event.target.value));
            setPageState(0);
          }}
        >
          {[25, 50, 100, 200].map((size) => <option key={size} value={size}>{size}</option>)}
        </select>
        <details className={styles.columns}>
          <summary>Columns ({visible.length}/{fields.length})</summary>
          {fields.map((field) => (
            <label key={field.key}>
              <input
                type="checkbox"
                checked={isVisible(field.key)}
                onChange={(event) => {
                  setVisibility((current) => ({ ...current, [field.key]: event.target.checked }));
                }}
              />
              {field.label}
            </label>
          ))}
        </details>
        <Button
          variant="ghost"
          disabled={snapshot.rows.length === 0}
          onClick={() => download(snapshot.rows.map((_, index) => receivedRow(snapshot, index)))}
        >
          {compact ? 'Export JSON' : 'Export received response'}
        </Button>
      </div>

      {selectable && (
        <div className={styles.toolbar} aria-label="Working selection">
          <span role="status">
            {selected.length} selected{previous > 0 ? ` · ${previous} retained from earlier responses` : ''}
          </span>
          <Button variant="ghost" disabled={selected.length === 0} onClick={() => { setSelected([]); setCompare(false); }}>
            Clear selection
          </Button>
          <Button
            variant="ghost"
            disabled={selected.length < 2}
            onClick={() => { setCompare(!compare); setComparePage(0); }}
          >
            {compare ? 'Hide comparison' : 'Compare selected fields'}
          </Button>
          <Button variant="ghost" disabled={selected.length === 0} onClick={() => download(selected)}>
            Export selected rows
          </Button>
        </div>
      )}

      {error && <p className={styles.error} role="alert">{error}</p>}

      {snapshot.rows.length === 0 ? (
        <p>No rows were returned in this response.</p>
      ) : (
        <>
          <div className={styles.scroll} role="region" aria-label={`${label} table`} tabIndex={0}>
            <table className={styles.table}>
              <caption>
                {label} — rows {matching.length === 0 ? 0 : start + 1}–{Math.min(start + pageSize, matching.length)} of {matching.length} matching in {snapshot.rows.length} received
              </caption>
              <thead>
                <tr>
                  {selectable && (
                    <th scope="col">
                      <input
                        ref={pageCheckbox}
                        type="checkbox"
                        aria-label="Select only this presentation page"
                        checked={checked === indices.length && indices.length > 0}
                        onChange={(event) => toggle(indices, event.target.checked)}
                      />
                    </th>
                  )}
                  <th scope="col">Inspect</th>
                  {visible.map((field) => <th key={field.key} scope="col">{field.label}</th>)}
                </tr>
              </thead>
              <tbody>
                {matching.length === 0 && (
                  <tr><td colSpan={tableChromeColumns}>No received rows match this filter. Clear the filter to see the response.</td></tr>
                )}
                {indices.map((index) => {
                  const entry = receivedRow(snapshot, index);
                  const selectedRow = selectable && keys.has(entry.key);
                  return (
                    <tr key={rowKey?.(entry.row, index) ?? entry.key} data-selected={selectedRow || undefined}>
                      {selectable && (
                        <td>
                          <input
                            type="checkbox"
                            aria-label={`Select ${nameOf(entry)}`}
                            checked={keys.has(entry.key)}
                            onChange={(event) => toggle([index], event.target.checked)}
                          />
                        </td>
                      )}
                      <td>
                        <Button
                          variant="ghost"
                          aria-label={`Inspect ${nameOf(entry)}`}
                          aria-expanded={inspected?.key === entry.key}
                          aria-controls={`${id}-inspector`}
                          onClick={(event) => {
                            opener.current = event.currentTarget;
                            setInspected(entry);
                          }}
                        >
                          Open
                        </Button>
                      </td>
                      {visible.map((field) => (
                        <td key={field.key}>
                          {field.render ? field.render(entry.row) : <CellValue value={rowValue(entry.row, field.key)} />}
                        </td>
                      ))}
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          <div className={styles.toolbar} aria-label="Response presentation pages">
            <Button variant="ghost" disabled={page === 0} onClick={() => setPageState(page - 1)}>Previous rows</Button>
            <span>Page {page + 1} of {pageCount}</span>
            <Button variant="ghost" disabled={page + 1 === pageCount} onClick={() => setPageState(page + 1)}>Next rows</Button>
          </div>
        </>
      )}

      <section id={`${id}-inspector`} className={styles.inspector} hidden={!inspected} aria-label="Received row detail">
        {inspected && (
          <>
            <div className={styles.toolbar}>
              <h4 ref={inspectHeading} tabIndex={-1}>{nameOf(inspected)}</h4>
              <Button
                variant="ghost"
                onClick={() => {
                  setInspected(null);
                  if (opener.current?.isConnected) opener.current.focus();
                }}
              >
                Close detail
              </Button>
              <Button variant="ghost" onClick={() => void copyEntry(inspected)}>Copy row JSON</Button>
            </div>
            <p className={styles.meta}>
              {inspected.snapshot.id !== snapshot.id ? 'Retained from an earlier response. ' : ''}
              Response ordinal {inspected.index + 1}. {inspected.snapshot.boundary} Received {new Date(inspected.snapshot.receivedAt).toLocaleString()}.
            </p>
            <dl className={styles.values}>
              {Object.keys(inspected.row).map((key) => (
                <div key={key}>
                  <dt>{fields.find((field) => field.key === key)?.label ?? key}</dt>
                  <dd>
                    {fields.find((field) => field.key === key)?.detail?.(inspected.row)}
                    <pre>{valueText(rowValue(inspected.row, key))}</pre>
                  </dd>
                </div>
              ))}
            </dl>
          </>
        )}
      </section>

      {selectable && compare && selected.length > 1 && (
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
      )}
    </section>
  );
}

function CellValue({ value }: { value: unknown }) {
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
