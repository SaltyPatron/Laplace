import { useCallback, useEffect, useState } from 'react';
import { Banner, Button, ErrorText, Input, LoadingText, Modal, Muted, Panel, Toggle } from '@ui';
import { useAppStore } from '../store';
import {
  analyzeSubstrate, indexHealth, reindexInvalid, vacuum, type IndexHealthRow,
} from './api';
import styles from './Admin.module.css';

/**
 * Database maintenance: lists invalid indexes (through `ops.index_health`) and runs
 * reindex, ANALYZE and VACUUM. Each action can be stopped from System activity and
 * keeps the work it already finished.
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
      setNote(after ?? 'Finished.');
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
    if (!confirmVacuumFull) { setConfirmVacuumFull(true); return; }
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

        {health == null ? <LoadingText>Checking indexes…</LoadingText>
          : health.length === 0 ? (
            <Muted>All indexes are valid. Nothing needs rebuilding.</Muted>
          ) : (
            <>
              {shells.length > 0 && (
                <Banner variant="warning">
                  {shells.length === 1 ? '1 partitioned index has' : `${shells.length} partitioned indexes have`} no
                  usable parts. Queries cannot use {shells.length === 1 ? 'it' : 'them'} even though the table
                  still reports its rows. Rebuild invalid indexes to fix this.
                </Banner>
              )}
              <div className={styles.tableWrap}>
                <table className={styles.table}>
                  <thead>
                    <tr>
                      <th scope="col">Index</th>
                      <th scope="col">Table</th>
                      <th scope="col">Schema</th>
                      <th scope="col">Partitioned</th>
                      <th scope="col">Usable parts</th>
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

        <Muted>
          Use Rebuild invalid indexes when this list is not empty. Dry run lists what would be rebuilt
          without changing anything.
        </Muted>
        <div className={styles.toolbar}>
          <Button
            variant="ghost"
            loading={busy === 'reindex dry run'}
            onClick={() => void run(
              'reindex dry run',
              () => reindexInvalid(true, 600, { tenant }),
              'Dry run finished. The list of indexes it would rebuild is in the server log; nothing was changed.',
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
          Refresh statistics (ANALYZE) when queries become slow even though the right indexes exist.
          Run VACUUM to reclaim space after large deletes or reloads. On the whole database it can take
          hours; you can stop it from System activity.
        </Muted>

        <Button
          variant="ghost"
          loading={busy === 'ANALYZE'}
          onClick={() => void run('ANALYZE', () => analyzeSubstrate(3600, { tenant }), 'Statistics refreshed.')}
        >
          Refresh statistics (ANALYZE)
        </Button>

        <label className={styles.field}>
          <span>Table (leave empty for the whole database)</span>
          <Input
            value={vacTable}
            onChange={(e) => setVacTable(e.target.value)}
            placeholder="attestations"
          />
        </label>
        <div className={styles.toolbar}>
          <label className={styles.liveLabel}>
            <Toggle checked={vacAnalyze} onCheckedChange={setVacAnalyze} aria-label="Also analyze" />
            Also refresh statistics
          </label>
          <label className={styles.liveLabel}>
            <Toggle checked={vacFull} onCheckedChange={setVacFull} aria-label="Vacuum full" />
            Full rewrite
          </label>
          <Button variant="ghost" loading={busy === 'VACUUM'} onClick={startVacuum}>Run VACUUM</Button>
        </div>
        {vacFull && (
          <Muted className={styles.writeWarn}>
            A full rewrite locks the table until it finishes: nothing can read or write it in the
            meantime, and it needs free disk space equal to the table's size.
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
          Each index is rebuilt on its own. If you stop the rebuild from System activity, the indexes
          already rebuilt stay fixed.
        </p>
        <p>
          While an index is rebuilt, its table is locked. These indexes are already unusable, so
          reads lose nothing they currently have.
        </p>
      </Modal>

      <Modal
        open={confirmVacuumFull}
        onClose={() => setConfirmVacuumFull(false)}
        title={vacFull ? 'Run a full VACUUM rewrite?' : `Run VACUUM on ${vacTable.trim() || 'the whole database'}?`}
        actions={
          <>
            <Button variant="ghost" onClick={() => setConfirmVacuumFull(false)}>Cancel</Button>
            <Button loading={busy === 'VACUUM'} onClick={startVacuum}>Run VACUUM</Button>
          </>
        }
      >
        {vacFull ? <>
          <p>
            Every read and write to <code>{vacTable.trim() || 'every table'}</code> waits until the
            rewrite finishes, and it needs free disk space equal to the table's current size.
          </p>
          <p>A regular VACUUM reclaims space without locking and is usually the better choice.</p>
        </> : <p>VACUUM does not block reads or writes, but on the whole database it can run for hours and slows other work on the server while it runs.</p>}
      </Modal>
    </div>
  );
}
