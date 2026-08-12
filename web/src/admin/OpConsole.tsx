import { useEffect, useMemo, useState } from 'react';
import { Button, ErrorText, Input, LoadingText, Muted, Panel, TextArea } from '@ui';
import { useAppStore } from '../store';
import { callOp, listOps, type OpSignature } from './api';
import styles from './Admin.module.css';

/** Signatures that mutate — badged, because /v1/op refuses them read-only
 * unless allow-listed (InstalledOpInvoker.WritableOps). */
const WRITE_HINT = /_close|_deposit|_refresh|_set|_apply|_delete|_insert|_update|_reset/i;

/**
 * The operations console.
 *
 * `POST /v1/op` resolves a name against `ops.api()` and refuses anything outside
 * it — a named call, never SQL text — so exposing the whole catalog here adds no
 * capability the endpoint did not already grant. The console discovers itself
 * through `ops.api`, so it stays correct as operations are installed.
 */
export function OpConsole() {
  const { tenant } = useAppStore();
  const [ops, setOps] = useState<OpSignature[] | null>(null);
  const [catalogErr, setCatalogErr] = useState<string | null>(null);
  const [filter, setFilter] = useState('');

  const [name, setName] = useState('');
  const [argsText, setArgsText] = useState('{}');
  const [maxRows, setMaxRows] = useState(50);
  const [rows, setRows] = useState<Record<string, unknown>[] | null>(null);
  const [runErr, setRunErr] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    listOps(undefined, { tenant })
      .then((r) => setOps(r.rows ?? []))
      .catch((e) => setCatalogErr(e instanceof Error ? e.message : String(e)));
  }, [tenant]);

  const shown = useMemo(() => {
    if (!ops) return [];
    const f = filter.trim().toLowerCase();
    const list = f ? ops.filter((o) => o.name.toLowerCase().includes(f)) : ops;
    return list.slice(0, 300);
  }, [ops, filter]);

  const selected = ops?.find((o) => o.name === name);

  async function run() {
    if (!name.trim() || busy) return;
    setBusy(true);
    setRunErr(null);
    setRows(null);
    let args: Record<string, unknown> | undefined;
    try {
      const parsed = argsText.trim() ? JSON.parse(argsText) : {};
      if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
        args = parsed as Record<string, unknown>;
      } else {
        throw new Error('args must be a JSON object');
      }
    } catch (e) {
      setRunErr(`args: ${e instanceof Error ? e.message : String(e)}`);
      setBusy(false);
      return;
    }
    try {
      const res = await callOp(name.trim(), args, maxRows, undefined, { tenant });
      setRows(res.rows ?? []);
    } catch (e) {
      setRunErr(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  const columns = rows && rows.length ? Object.keys(rows[0]) : [];

  return (
    <div className={styles.opGrid}>
      <Panel title={`Catalog${ops ? ` — ${ops.length} operations` : ''}`}>
        <Input
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          placeholder="filter by name…"
          aria-label="Filter operations"
        />
        {catalogErr ? <ErrorText>{catalogErr}</ErrorText>
          : ops == null ? <LoadingText>Reading ops.api()…</LoadingText>
          : (
            <ul className={styles.opList}>
              {shown.map((o, i) => (
                <li key={`${o.name}-${i}`}>
                  <button
                    type="button"
                    className={`${styles.opItem} ${o.name === name ? styles.opItemOn : ''}`}
                    onClick={() => { setName(o.name); setArgsText('{}'); setRows(null); setRunErr(null); }}
                  >
                    <span className={styles.opName}>{o.name}</span>
                    <span className={styles.opArgs}>{o.args || '()'}</span>
                    {WRITE_HINT.test(o.name) && <span className={styles.writeTag}>write</span>}
                  </button>
                </li>
              ))}
            </ul>
          )}
      </Panel>

      <Panel title="Invoke">
        <label className={styles.field}>
          <span>name</span>
          <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="ops.ingest_runs" />
        </label>
        {selected && <code className={styles.sig}>{selected.name}({selected.args || ''})</code>}
        {selected && WRITE_HINT.test(selected.name) && (
          <Muted className={styles.writeWarn}>
            This looks like a write operation. <code>POST /v1/op</code> binds a read-only data
            source, so it will fail rather than take effect.
          </Muted>
        )}
        <label className={styles.field}>
          <span>args (JSON object)</span>
          <TextArea rows={4} value={argsText} onChange={(e) => setArgsText(e.target.value)} />
        </label>
        <label className={styles.field}>
          <span>max rows</span>
          <Input
            type="number"
            min={1}
            max={2000}
            value={maxRows}
            onChange={(e) => setMaxRows(Math.max(1, Math.min(2000, Number(e.target.value) || 50)))}
          />
        </label>
        <Button onClick={() => void run()} loading={busy} disabled={!name.trim()}>Run</Button>

        {runErr && <ErrorText className={styles.runErrBox}>{runErr}</ErrorText>}
        {rows && (
          rows.length === 0 ? <Muted>0 rows.</Muted> : (
            <div className={styles.tableWrap}>
              <table className={styles.table}>
                <thead>
                  <tr>{columns.map((c) => <th key={c} scope="col">{c}</th>)}</tr>
                </thead>
                <tbody>
                  {rows.map((r, i) => (
                    <tr key={i}>
                      {columns.map((c) => {
                        const v = r[c];
                        const text = v == null ? '—'
                          : typeof v === 'object' ? JSON.stringify(v)
                          : String(v);
                        return <td key={c} title={text}>{text.length > 120 ? `${text.slice(0, 119)}…` : text}</td>;
                      })}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )
        )}
      </Panel>
    </div>
  );
}
