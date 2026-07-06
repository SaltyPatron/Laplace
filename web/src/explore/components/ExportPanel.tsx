import { useState } from 'react';
import type { BillingReceipt } from '../types';

export function ExportPanel({
  busy,
  onExport,
  receipt,
  witnessRows,
  consensusRows,
}: {
  busy: boolean;
  onExport: () => void;
  receipt?: BillingReceipt | null;
  witnessRows?: number;
  consensusRows?: number;
}) {
  const [confirmed, setConfirmed] = useState(false);

  return (
    <section className="export-panel">
      <p>
        Download a JSON training bundle: entity, consensus neighborhood, evidence, geometry, and optional cross-links.
      </p>
      {(witnessRows != null || consensusRows != null) ? (
        <p className="muted">
          Estimated pack: {consensusRows?.toLocaleString() ?? '—'} consensus rows,{' '}
          {witnessRows?.toLocaleString() ?? '—'} witness rows (recipe.export metered).
        </p>
      ) : null}
      {!confirmed ? (
        <button type="button" onClick={() => setConfirmed(true)}>Review export estimate</button>
      ) : (
        <button type="button" disabled={busy} onClick={onExport}>
          {busy ? 'Exporting…' : 'Confirm download (recipe.export)'}
        </button>
      )}
      {receipt ? (
        <p className="receipt-hint muted">
          Charged {receipt.amount_cents / 100} {receipt.currency} — {receipt.service_id} ({receipt.quote_id.slice(0, 12)}…)
        </p>
      ) : null}
    </section>
  );
}
