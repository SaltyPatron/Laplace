import { useMemo, useRef, useState } from 'react';
import { Button, ErrorText, Field, Input, Modal, Muted, Panel, ReadStatus, TextArea, useReadResource } from '@ui';
import { OperationFields } from '../ui/composites/OperationFields/OperationFields';
import { initialOperationDraft, operationArguments, type OperationDraft } from '../ui/lib/operationFields';
import { useAppStore } from '../store';
import type { OpResult } from './api';
import { apiPostJson } from '../api/client';
import { readOperationCatalog, type OperationDescription } from './operationCatalog';
import styles from './Admin.module.css';

interface Invocation { name: string; argsJson: string; maxRows: number; writable: boolean; destructive: boolean }
interface CompletedInvocation { invocation: Invocation; result: OpResult<Record<string, unknown>>; at: string }

export function OpConsole() {
  const tenant = useAppStore((state) => state.tenant);
  return <OperationWorkspace key={tenant} tenant={tenant} />;
}

function OperationWorkspace({ tenant }: { tenant: string }) {
  const [search, setSearch] = useState('');
  const [filter, setFilter] = useState('');
  const [page, setPage] = useState(0);
  const [selected, setSelected] = useState<OperationDescription | null>(null);
  const [draft, setDraft] = useState<OperationDraft>({});
  const [advanced, setAdvanced] = useState(false);
  const [advancedInitialized, setAdvancedInitialized] = useState(false);
  const [argsText, setArgsText] = useState('{}');
  const [maxRows, setMaxRows] = useState('50');
  const [error, setError] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<Invocation | null>(null);
  const [pending, setPending] = useState<Invocation | null>(null);
  const [completed, setCompleted] = useState<CompletedInvocation | null>(null);
  const executing = useRef(false);
  const catalog = useReadResource({
    key: JSON.stringify(['operation-catalog', tenant, filter]),
    read: (signal) => readOperationCatalog(filter, { tenant, signal }),
  });
  const operations = catalog.data?.operations ?? [];
  const pageCount = Math.max(1, Math.ceil(operations.length / 50));
  const currentPage = Math.min(page, pageCount - 1);
  const shown = operations.slice(currentPage * 50, (currentPage + 1) * 50);
  const rows = completed?.result.rows;
  const columns = useMemo(() => rows ? [...new Set(rows.flatMap((row) => Object.keys(row)))] : [], [rows]);

  function choose(operation: OperationDescription) {
    setSelected(operation); setDraft(initialOperationDraft(operation.parameters));
    setArgsText('{}'); setAdvanced(false); setAdvancedInitialized(false); setError(null);
  }
  function prepare() {
    if (!selected || executing.current) return;
    try {
      if (!/^\d+$/.test(maxRows) || Number(maxRows) > 2147483647) throw new Error('Response row limit must be an integer from 0 to 2147483647.');
      let argsJson: string;
      if (advanced) {
        const parsed: unknown = JSON.parse(argsText); // Validate shape only; transmit the original JSON text.
        if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) throw new Error('Advanced arguments must be a JSON object.');
        argsJson = argsText;
      } else argsJson = JSON.stringify(operationArguments(selected.parameters, draft));
      const invocation = { name: selected.name, argsJson, maxRows: Number(maxRows), writable: selected.writable, destructive: selected.destructive };
      setError(null);
      if (invocation.writable || invocation.destructive) setConfirm(invocation);
      else void execute(invocation);
    } catch (failure) { setError(failure instanceof Error ? failure.message : String(failure)); }
  }
  async function execute(invocation: Invocation) {
    if (executing.current) return;
    executing.current = true; setPending(invocation); setConfirm(null); setError(null);
    try {
      const payload = `{"name":${JSON.stringify(invocation.name)},"args":${invocation.argsJson},"max_rows":${invocation.maxRows}}`;
      const result = await apiPostJson<OpResult<Record<string, unknown>>>('/v1/op', payload, { tenant });
      setCompleted({ invocation, result, at: new Date().toISOString() });
    } catch (failure) {
      setError(`${invocation.name}: ${failure instanceof Error ? failure.message : String(failure)}${invocation.writable ? ' A transport failure can leave the outcome unknown; inspect the current state before submitting again.' : ''}`);
    } finally { executing.current = false; setPending(null); }
  }

  return <div className={styles.opGrid}>
    <Panel title="Installed operations" expandable>
      <form onSubmit={(event) => { event.preventDefault(); setFilter(search); setPage(0); }}>
        <Field label="Find operations" htmlFor="operation-search" help="Search the installed catalog by name, including operations outside the current response window.">
          <Input id="operation-search" value={search} onChange={(event) => setSearch(event.target.value)} />
        </Field><Button type="submit">Search catalog</Button>{' '}<Button variant="ghost" onClick={() => void catalog.reload()}>Refresh catalog</Button>
      </form>
      <ReadStatus label="Operation catalog" resource={catalog} />
      {catalog.data && <>
        <Muted>{operations.length} signatures received{catalog.data.truncated_at != null ? '; more exist — narrow the search' : ''}. Page {currentPage + 1} of {pageCount} in this response.</Muted>
        {operations.length === 0 ? <Muted>No installed operations matched this search.</Muted> : <ul className={styles.opList}>
          {shown.map((operation, index) => <li key={`${operation.name}-${operation.args}-${index}`}>
            <button type="button" className={`${styles.opItem} ${selected?.name === operation.name && selected.args === operation.args ? styles.opItemOn : ''}`} onClick={() => choose(operation)}>
              <span className={styles.opName}>{operation.name}</span><span className={styles.opArgs}>{operation.args || 'No arguments'}</span>
              {operation.destructive ? <span className={styles.destructiveTag}>destroys data</span> : operation.writable ? <span className={styles.writeTag}>write</span> : null}
              {operation.kind === 'procedure' && <span className={styles.procTag}>CALL</span>}
            </button>
          </li>)}
        </ul>}
        <div className={styles.rowActions}><Button variant="ghost" disabled={currentPage === 0} onClick={() => setPage(currentPage - 1)}>Previous signatures</Button>
          <Button variant="ghost" disabled={currentPage + 1 >= pageCount} onClick={() => setPage(currentPage + 1)}>Next signatures</Button></div>
      </>}
    </Panel>
    <Panel title={selected ? selected.name : 'Operation detail'} expandable>
      {!selected ? <Muted>Select an installed operation to inspect its inputs and run it.</Muted> : <form onSubmit={(event) => { event.preventDefault(); prepare(); }}>
        <details><summary>Installed signature and return type</summary><pre className={styles.sig}>{selected.name}({selected.args}){'\n'}{selected.returns ?? 'No row return type'}</pre></details>
        <Muted>{selected.destructive ? 'This operation destroys stored testimony. Review its exact inputs before confirming.' : selected.writable ? 'This operation may change stored state.' : 'The endpoint uses its read-only connection for this operation.'}</Muted>
        <label className={styles.field}><span>Advanced JSON arguments</span><input type="checkbox" checked={advanced} onChange={(event) => {
          if (event.target.checked && !advancedInitialized) {
            try { setArgsText(JSON.stringify(operationArguments(selected.parameters, draft), null, 2)); } catch { setArgsText('{}'); }
            setAdvancedInitialized(true);
          }
          setAdvanced(event.target.checked);
        }} /></label>
        {advanced ? <Field label="Arguments JSON" htmlFor="operation-json" help="A JSON object keyed by exact parameter names. Original numeric literals are transmitted unchanged. Each editor retains its own draft; only the selected editor supplies the run.">
          <TextArea id="operation-json" rows={6} value={argsText} onChange={(event) => setArgsText(event.target.value)} />
        </Field> : <OperationFields parameters={selected.parameters} value={draft} onChange={setDraft} />}
        <Field label="Response row limit" htmlFor="operation-limit" help="Limits returned rows, not execution work. Zero requests no returned rows; omitted results are reported.">
          <Input id="operation-limit" inputMode="numeric" value={maxRows} onChange={(event) => setMaxRows(event.target.value)} />
        </Field>
        <Button type="submit" loading={pending != null}>{selected.writable ? 'Review operation' : 'Run operation'}</Button>
      </form>}
      {pending && <Muted role="status">Waiting for {pending.name}. Leaving this view does not prove server work stopped.</Muted>}
      {error && <ErrorText role="alert" className={styles.runErrBox}>{error}</ErrorText>}
      {completed && <section aria-label="Operation result">
        <h4>Result: {completed.invocation.name}</h4><Muted>Received at {new Date(completed.at).toLocaleTimeString()}.</Muted>
        <details><summary>Executed inputs</summary><pre className={styles.sig}>{completed.invocation.argsJson}</pre></details>
        {completed.result.truncated_at != null && <Muted>Response truncated at {completed.result.truncated_at} rows. This is not the complete result.</Muted>}
        {rows?.length === 0 ? <Muted>The operation returned no rows.</Muted> : <div className={styles.tableWrap}><table className={styles.table}>
          <thead><tr>{columns.map((column) => <th key={column} scope="col">{column}</th>)}</tr></thead>
          <tbody>{rows?.map((row, index) => <tr key={index}>{columns.map((column) => {
            const value = row[column];
            const text = value == null ? (column in row ? 'NULL' : 'Not returned') : typeof value === 'object' ? JSON.stringify(value) : String(value);
            return <td key={column}>{text.length > 120 ? <details><summary>{text.slice(0, 119)}…</summary><pre className={styles.sig}>{text}</pre></details> : text}</td>;
          })}</tr>)}</tbody>
        </table></div>}
      </section>}
      <Modal open={confirm != null} onClose={() => setConfirm(null)} title={confirm?.destructive ? 'Confirm destructive operation' : 'Confirm state-changing operation'}
        actions={<><Button variant="ghost" onClick={() => setConfirm(null)}>Go back</Button><Button onClick={() => confirm && void execute(confirm)}>Confirm and run</Button></>}>
        <p>{confirm?.name} on tenant {tenant}. {confirm?.destructive ? 'This destroys stored testimony.' : 'This may change stored state.'}</p>
        <pre className={styles.sig}>{confirm?.argsJson ?? '{}' }</pre><p>Response row limit: {confirm?.maxRows}. The server revalidates the named call.</p>
      </Modal>
    </Panel>
  </div>;
}
