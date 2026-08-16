import { useCallback, useEffect, useState } from 'react';
import { Banner, Button, ErrorText, Input, LoadingText, Modal, Muted, Panel, Toggle } from '@ui';
import { useAppStore } from '../store';
import {
  analyzeSubstrate, indexHealth, reindexInvalid, vacuum, type IndexHealthRow,
} from './api';
import styles from './Admin.module.css';

/**
 * Diagnosis and repair.
 *
 * ops.index_health made the 2026-08-13 cycle-shell class visible from every
 * surface — 28 partitioned-parent secondaries left as empty invalid shells while
 * every row count read green. The fix stayed a hand-typed psql session, which is
 * the same gap one step later: an operator who can see the damage here and must
 * leave to act on it will eventually act on the wrong cluster.
 *
 * Every button here is long-running and therefore cancellable rather than
 * bounded — the Activity tab is the stop button, and each operation commits
 * incrementally so cancelling keeps the work already done.
 */
export function Repair() {
  const { tenant } = useAppStore();
  const [health, setHealth] = useState<IndexHealthRow[] | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [confirmReindex, setConfirmReindex] = useState(false);

  const [vacTable, setVacTable] = useState('');
  const [vacFull, setVacFull] = useState(false);
  const [vacAnalyze, setVacAnalyze] = useState(true);
  const [confirmVacuumFull, setConfirmVacuumFull] = useState(false);

  const load = useCallback(async () => {
    try {
      const res = await indexHealth({ tenant });
      setHealth(res.rows ?? []);
      setErr(null);
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    }
  }, [tenant]);

  useEffect(() => { void load(); }, [load]);

  async function run(label: string, fn: () => Promise<unknown>, after?: string) {
    setBusy(label);
    setErr(null);
    setNote(null);
    try {
      await fn();
      setNote(after ?? `${label} finished.`);
      await load();
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(null);
      setConfirmReindex(false);
      setConfirmVacuumFull(false);
    }
  }

  function startVacuum() {
    if (vacFull && !confirmVacuumFull) { setConfirmVacuumFull(true); return; }
    void run(
      'VACUUM',
      () => vacuum(
        {
          table: vacTable.trim() || undefined,
          full: vacFull,
          analyze: vacAnalyze,
          timeout_seconds: 21600,
        },
        { tenant },
      ),
      `VACUUM finished on ${vacTable.trim() || 'the whole database'}.`,
    );
  }

  const shells = health?.filter((h) => h.is_partitioned_parent && h.leaf_count === 0) ?? [];

  return (
    <div className={styles.opGrid}>
      <Panel
        title={`Index health${health ? ` — ${health.length} invalid` : ''}`}
        actions={<Button variant="ghost" onClick={() => void load()}>Refresh</Button>}
      >
        {err && <ErrorText className={styles.runErrBox}>{err}</ErrorText>}
        {note && <Banner variant="info">{note}</Banner>}

        {health == null ? <LoadingText>Reading ops.index_health()…</LoadingText>
          : health.length === 0 ? (
            <Muted>
              No invalid index. An empty set here IS the healthy answer — row counts read green
              through the 2026-08-13 shell incident, this does not.
            </Muted>
          ) : (
            <>
              {shells.length > 0 && (
                <Banner variant="warning">
                  {shells.length} partitioned parent{shells.length === 1 ? ' has' : 's have'} zero
                  leaves — the shell class: present in the catalog, unusable for reads, invisible
                  to row counts.
                </Banner>
              )}
              <div className={styles.tableWrap}>
                <table className={styles.table}>
                  <thead>
                    <tr>
                      <th scope="col">index</th>
                      <th scope="col">table</th>
                      <th scope="col">schema</th>
                      <th scope="col">partitioned</th>
                      <th scope="col">leaves</th>
                    </tr>
                  </thead>
                  <tbody>
                    {health.map((h) => (
                      <tr key={`${h.schema_name}.${h.index_name}`}>
                        <td>{h.index_name}</td>
                        <td>{h.table_name}</td>
                        <td>{h.schema_name}</td>
                        <td>{h.is_partitioned_parent ? 'yes' : 'no'}</td>
                        <td className={h.is_partitioned_parent && h.leaf_count === 0
                          ? styles.failed : styles.num}>
                          {h.leaf_count}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </>
          )}

        <div className={styles.toolbar}>
          <Button
            variant="ghost"
            loading={busy === 'reindex dry run'}
            onClick={() => void run(
              'reindex dry run',
              () => reindexInvalid(true, 600, { tenant }),
              'Dry run logged the plan to the server log — read it back with ops.app_log.',
            )}
          >
            Dry run
          </Button>
          <Button
            disabled={health != null && health.length === 0}
            loading={busy === 'reindex'}
            onClick={() => setConfirmReindex(true)}
          >
            Rebuild invalid indexes
          </Button>
        </div>
      </Panel>

      <Panel title="Maintenance">
        <Muted>
          ANALYZE refreshes planner statistics — the fix for the class of slowdown where the right
          index exists and the planner will not pick it. VACUUM reclaims dead tuples; it is not an
          installed operation because Postgres refuses it inside a transaction block, so the
          endpoint issues it on its own connection.
        </Muted>

        <Button
          variant="ghost"
          loading={busy === 'ANALYZE'}
          onClick={() => void run('ANALYZE', () => analyzeSubstrate(3600, { tenant }))}
        >
          ANALYZE substrate
        </Button>

        <label className={styles.field}>
          <span>table (blank = whole database)</span>
          <Input
            value={vacTable}
            onChange={(e) => setVacTable(e.target.value)}
            placeholder="attestations"
          />
        </label>
        <div className={styles.toolbar}>
          <label className={styles.liveLabel}>
            <Toggle checked={vacAnalyze} onCheckedChange={setVacAnalyze} aria-label="Also analyze" />
            ANALYZE too
          </label>
          <label className={styles.liveLabel}>
            <Toggle checked={vacFull} onCheckedChange={setVacFull} aria-label="Vacuum full" />
            FULL
          </label>
          <Button loading={busy === 'VACUUM'} onClick={startVacuum}>Run VACUUM</Button>
        </div>
        {vacFull && (
          <Muted className={styles.writeWarn}>
            VACUUM FULL rewrites the table and holds an ACCESS EXCLUSIVE lock for the whole
            rewrite: reads and writes to it block until it finishes, and it needs free disk equal
            to the table's size.
          </Muted>
        )}
      </Panel>

      <Modal
        open={confirmReindex}
        onClose={() => setConfirmReindex(false)}
        title={`Rebuild ${health?.length ?? 0} invalid index(es)?`}
        actions={
          <>
            <Button variant="ghost" onClick={() => setConfirmReindex(false)}>Cancel</Button>
            <Button
              loading={busy === 'reindex'}
              onClick={() => void run('reindex', () => reindexInvalid(false, 21600, { tenant }))}
            >
              Rebuild
            </Button>
          </>
        }
      >
        <p>
          Each index is rebuilt in its own transaction, so the run is resumable: cancelling from
          the Activity tab loses only the index in flight.
        </p>
        <p>
          The rebuild takes an exclusive lock on each index's table. That is the right trade here —
          these indexes are <strong>already invalid</strong>, so reads cannot use them and the
          outage began before the repair did.
        </p>
      </Modal>

      <Modal
        open={confirmVacuumFull}
        onClose={() => setConfirmVacuumFull(false)}
        title="VACUUM FULL rewrites the table"
        actions={
          <>
            <Button variant="ghost" onClick={() => setConfirmVacuumFull(false)}>Cancel</Button>
            <Button loading={busy === 'VACUUM'} onClick={startVacuum}>Run it</Button>
          </>
        }
      >
        <p>
          ACCESS EXCLUSIVE for the whole rewrite: every read and write to{' '}
          <code>{vacTable.trim() || 'every substrate table'}</code> blocks until it finishes, and
          it needs free disk equal to the table's current size.
        </p>
        <p>Plain VACUUM reclaims space without the lock and is almost always what you want.</p>
      </Modal>
    </div>
  );
}
