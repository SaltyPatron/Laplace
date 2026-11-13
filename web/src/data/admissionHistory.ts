import type { ContentMode, ContentReceipt } from './content';

/**
 * Admissions confirmed by a receipt in this browser tab, per account + tenant scope.
 *
 * The server exposes no listing of a workspace's admitted artifacts (only
 * POST /v1/content/{mode}[/raw] and GET /v1/content/{id}), so this history is
 * session-local: it survives navigation and reloads of this tab
 * (sessionStorage), and nothing else. It is never a complete inventory.
 */
export interface AdmissionRecord {
  file_id: string;
  content_id: string;
  source_id: string;
  source: string;
  name: string;
  path: string;
  mode: ContentMode;
  bytes: number;
  admitted_at: string;
}

const PREFIX = 'laplace.data.admissions.v1:';
const LIMIT = 200;

export function readAdmissionHistory(scope: string): AdmissionRecord[] {
  try {
    const raw = sessionStorage.getItem(PREFIX + scope);
    const parsed: unknown = raw ? JSON.parse(raw) : [];
    return Array.isArray(parsed)
      ? parsed.filter((row): row is AdmissionRecord => !!row && typeof row === 'object' && typeof (row as AdmissionRecord).file_id === 'string')
      : [];
  } catch {
    return [];
  }
}

/** Add receipts not yet recorded; returns the merged history, newest first. */
export function recordAdmissions(
  scope: string,
  current: AdmissionRecord[],
  items: readonly { name: string; path: string; mode: ContentMode; bytes: number; receipt?: ContentReceipt }[],
): AdmissionRecord[] {
  const known = new Set(current.map((row) => row.file_id));
  const now = new Date().toISOString();
  const added: AdmissionRecord[] = [];
  for (const item of items) {
    const receipt = item.receipt;
    if (!receipt || known.has(receipt.file_id)) continue;
    known.add(receipt.file_id);
    added.push({
      file_id: receipt.file_id, content_id: receipt.content_id, source_id: receipt.source_id, source: receipt.source,
      name: item.name, path: receipt.path, mode: item.mode, bytes: receipt.bytes ?? item.bytes,
      admitted_at: now,
    });
  }
  if (added.length === 0) return current;
  const next = [...added.reverse(), ...current].slice(0, LIMIT);
  try { sessionStorage.setItem(PREFIX + scope, JSON.stringify(next)); } catch { /* storage unavailable: keep in memory */ }
  return next;
}
