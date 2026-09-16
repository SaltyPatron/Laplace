export const OPEN_INGEST_STATES = new Set(['running', 'composed', 'started', 'pending', 'in_progress']);
export function ingestStatusTone(status: string): 'ok' | 'running' | 'failed' | 'cancelled' {
  const normalized = status.toLowerCase();
  if (['ok', 'complete', 'completed', 'skipped-complete'].includes(normalized)) return 'ok';
  if (OPEN_INGEST_STATES.has(normalized)) return 'running';
  if (['failed', 'failure', 'error'].includes(normalized)) return 'failed';
  // No comparison/no measurement and unfamiliar future statuses are not failures.
  return 'cancelled';
}
export function ingestDuration(started: string | null, ended: string | null): string {
  if (!started) return 'Not recorded';
  const start = Date.parse(started), end = ended ? Date.parse(ended) : Date.now();
  if (!Number.isFinite(start) || !Number.isFinite(end)) return 'Not recorded';
  const seconds = Math.max(0, Math.round((end - start) / 1000));
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  return minutes < 60 ? `${minutes}m ${seconds % 60}s` : `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
}
export function countText(value: number | null | undefined): string {
  return value == null ? 'Not recorded' : value.toLocaleString();
}
