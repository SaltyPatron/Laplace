import { useCallback, useEffect, useState } from 'react';
import { Alert, Button, Muted, Panel } from '@ui';
import { apiGet } from '../../../api/client';
import styles from './CalibrationPanel.module.css';

interface Distribution { median?: number; min?: number; max?: number }
interface Calibration {
  status: string;
  message?: string;
  reportSha256?: string;
  selectionStatus?: string;
  currentEngineMatches?: boolean | null;
  report?: {
    status?: string;
    evidence_invalid?: boolean;
    finished_utc?: string;
    host?: { hostname?: string; cpu_models?: string[]; physical_memory_bytes?: number };
    parameters?: { repeats?: number; bench_limit?: number; bench_limit_type?: string;
      match_depth?: number; max_moves?: number };
    plan?: { games_per_match_sample?: number; match_threads_per_engine?: number;
      match_hash_mib_per_engine?: number };
    recommendations?: {
      bench_suite_latency?: { threads?: number; hash_mib?: number; median_seconds?: number };
    };
    stockfish_identity?: { source_commit?: string; sha256?: string };
    stockfish_bench?: { status: string; threads?: number; hash_mib?: number;
      steady_engine_seconds?: Distribution; steady_nodes_per_second?: Distribution }[];
    cutechess_matches?: { status: string; concurrency?: number;
      steady_games_per_second?: Distribution; steady_plies_per_second?: Distribution }[];
  };
}

function numeric(value: number | undefined, digits = 0) {
  return typeof value === 'number' && Number.isFinite(value)
    ? value.toLocaleString(undefined, { maximumFractionDigits: digits }) : '—';
}

export function CalibrationPanel() {
  const [value, setValue] = useState<Calibration | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const refresh = useCallback(async () => {
    setBusy(true);
    try {
      setValue(await apiGet<Calibration>('/chess/lab/calibration'));
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally { setBusy(false); }
  }, []);
  useEffect(() => { void refresh(); }, [refresh]);

  const report = value?.report;
  const recommendation = report?.recommendations?.bench_suite_latency;
  const fullGames = report?.parameters?.max_moves === 0;
  const usable = value?.selectionStatus === 'selected' && value.currentEngineMatches === true
    && report?.status === 'complete' && !report.evidence_invalid;

  return (
    <Panel title="Machine calibration">
      <div className={styles.toolbar}>
        <Muted>Saved measurements from this installation. Refresh checks the selected report and current engine.</Muted>
        <Button onClick={() => void refresh()} disabled={busy}>{busy ? 'Loading…' : 'Refresh'}</Button>
      </div>
      {error && <Alert>{error}</Alert>}
      {value && value.status !== 'available' && <Muted>{value.message ?? 'No calibration report is available.'}</Muted>}
      {report && (
        <>
          <p>
            {report.host?.hostname ?? 'Measured host'} · {report.host?.cpu_models?.join(', ') ?? 'CPU unknown'}
            {report.host?.physical_memory_bytes !== undefined
              ? ` · ${numeric(report.host.physical_memory_bytes / 2 ** 30, 1)} GiB RAM` : ''}
            {report.finished_utc ? ` · measured ${new Date(report.finished_utc).toLocaleString()}` : ''}
          </p>
          {!usable && (
            <Alert>
              Historical measurement: {value?.currentEngineMatches === false
                ? 'the currently selected Stockfish binary differs or is unavailable.'
                : 'the selected calibration has not been confirmed for the current engine.'}
              {' '}Run the machine calibration again before applying these settings.
            </Alert>
          )}
          {recommendation && (
            <p>
              Measured lowest bench latency: <strong>{numeric(recommendation.threads)} threads,
              {' '}{numeric(recommendation.hash_mib)} MiB hash</strong>,
              {' '}{numeric(recommendation.median_seconds, 3)} s median.
              {' '}This selection is for a single engine. Match concurrency is a separate measurement.
            </p>
          )}
          <Muted>
            Stockfish built-in bench at {report.parameters?.bench_limit_type ?? 'limit'}
            {' '}{numeric(report.parameters?.bench_limit)}; {numeric(report.parameters?.repeats)} steady samples per configuration,
            with the first warm-up sample excluded. Search work can vary with threads and hash.
          </Muted>
          <div className={styles.scroll}>
            <table className={styles.measurements}>
              <caption>Stockfish search calibration</caption>
              <thead><tr><th>Threads</th><th>Hash (MiB)</th><th>Engine seconds (median)</th><th>Nodes/s (median)</th><th>Result</th></tr></thead>
              <tbody>{report.stockfish_bench?.map((row, i) => (
                <tr key={i}><td>{numeric(row.threads)}</td><td>{numeric(row.hash_mib)}</td>
                  <td>{numeric(row.steady_engine_seconds?.median, 3)}</td>
                  <td>{numeric(row.steady_nodes_per_second?.median)}</td><td>{row.status}</td></tr>
              ))}</tbody>
            </table>
          </div>
          <Muted>
            CuteChess Stockfish self-play at depth {numeric(report.parameters?.match_depth)},
            {' '}{numeric(report.plan?.games_per_match_sample)} games per sample,
            {' '}{numeric(report.plan?.match_threads_per_engine)} thread(s) and
            {' '}{numeric(report.plan?.match_hash_mib_per_engine)} MiB hash per engine.
            {' '}{fullGames ? 'Games ran to completion.' : 'Move-limited diagnostic; full-game capacity was not measured.'}
            {' '}Games may repeat. Database recording throughput and playing strength were not measured by this calibration.
          </Muted>
          <div className={styles.scroll}>
            <table className={styles.measurements}>
              <caption>{fullGames ? 'Generated self-play throughput' : 'Move-limited self-play diagnostic'}</caption>
              <thead><tr><th>Concurrent games</th><th>Generated games/s (median)</th><th>Plies/s (median)</th><th>Result</th></tr></thead>
              <tbody>{report.cutechess_matches?.map((row, i) => (
                <tr key={i}><td>{numeric(row.concurrency)}</td>
                  <td>{fullGames ? numeric(row.steady_games_per_second?.median, 3) : 'Not measured'}</td>
                  <td>{numeric(row.steady_plies_per_second?.median, 3)}</td><td>{row.status}</td></tr>
              ))}</tbody>
            </table>
          </div>
          <details className={styles.identity}>
            <summary>Measured engine and report identity</summary>
            <p>Stockfish source: <code>{report.stockfish_identity?.source_commit ?? 'Unavailable'}</code></p>
            <p>Engine SHA-256: <code>{report.stockfish_identity?.sha256 ?? 'Unavailable'}</code></p>
            <p>Report SHA-256: <code>{value?.reportSha256}</code></p>
            <p>Current engine: {value?.currentEngineMatches === true ? 'matches the measured binary'
              : value?.currentEngineMatches === false ? 'differs or is unavailable' : 'not checked'}.</p>
            <Muted>Installation-time machine validation is retained in the report. Opening this page does not rerun benchmarks or change settings.</Muted>
          </details>
        </>
      )}
    </Panel>
  );
}
