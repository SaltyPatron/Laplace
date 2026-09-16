/** A received response, not an assertion that the entire remote collection was read. */
export interface RowSnapshot<T extends object> {
  id: string;
  rows: readonly T[];
  receivedAt: string;
  boundary: string;
  /** Caller-declared, non-secret request context. Never includes transport credentials. */
  context?: Readonly<Record<string, unknown>>;
}
export interface SelectedRow<T extends object> {
  key: string;
  index: number;
  snapshot: RowSnapshot<T>;
  row: T;
}
let receiptSequence = 0;
export function captureRows<T extends object>(
  rows: readonly T[], boundary: string, context?: Readonly<Record<string, unknown>>,
): RowSnapshot<T> {
  return { id: `browser-${Date.now()}-${++receiptSequence}`, rows, boundary,
    receivedAt: new Date().toISOString(), context };
}
export function receivedRow<T extends object>(snapshot: RowSnapshot<T>, index: number): SelectedRow<T> {
  if (!Number.isSafeInteger(index) || index < 0 || index >= snapshot.rows.length)
    throw new RangeError('The row is outside this received response.');
  return { key: JSON.stringify([snapshot.id, index]), index, snapshot, row: snapshot.rows[index]! };
}
export function rowFields(rows: readonly object[]): string[] {
  return [...new Set(rows.flatMap((row) => Object.keys(row)))];
}
export function rowValue(row: object, field: string): unknown {
  return Object.prototype.hasOwnProperty.call(row, field)
    ? (row as Record<string, unknown>)[field] : undefined;
}
export function valueText(value: unknown): string {
  if (value === undefined) return 'Not returned';
  if (value === null) return 'NULL';
  if (value === '') return 'Empty text';
  if (typeof value === 'object') return JSON.stringify(value, null, 2);
  return String(value);
}
/** Selection is response+ordinal, never inferred canonical identity or all-matching. */
export function selectReceivedRows<T extends object>(
  existing: readonly SelectedRow<T>[], snapshot: RowSnapshot<T>, indices: readonly number[], selected: boolean,
): SelectedRow<T>[] {
  const next = new Map(existing.map((entry) => [entry.key, entry]));
  for (const index of indices) {
    const entry = receivedRow(snapshot, index);
    if (selected) next.set(entry.key, entry); else next.delete(entry.key);
  }
  return [...next.values()];
}
export function receivedRowsExport<T extends object>(entries: readonly SelectedRow<T>[]): string {
  const snapshots = new Map(entries.map((entry) => [entry.snapshot.id, entry.snapshot]));
  return JSON.stringify({
    format: 'laplace.received-rows/v1',
    scope: 'explicit received rows',
    representation: 'JSON values received by this browser; not a full collection or original-byte export.',
    snapshots: [...snapshots.values()].map(({ id, receivedAt, boundary, context, rows }) => ({
      id, received_at: receivedAt, boundary, context, received_row_count: rows.length,
    })),
    rows: entries.map(({ snapshot, index, row }) => ({ snapshot_id: snapshot.id, response_ordinal: index + 1, value: row })),
  }, null, 2);
}
