import { useState } from 'react';
import { Button, Muted, Stack } from '@ui';
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
    <Stack gap={3}>
      <Muted>
        Download a JSON training bundle: entity, consensus neighborhood, evidence, geometry, and optional cross-links.
      </Muted>
      {(witnessRows != null || consensusRows != null) ? (
        <Muted>
          Estimated pack: {consensusRows?.toLocaleString() ?? '—'} consensus rows,{' '}
          {witnessRows?.toLocaleString() ?? '—'} witness rows (recipe.export metered).
        </Muted>
      ) : null}
      {!confirmed ? (
        <Button variant="ghost" onClick={() => setConfirmed(true)}>Review export estimate</Button>
      ) : (
        <Button disabled={busy} loading={busy} onClick={onExport}>
          {busy ? 'Exporting…' : 'Confirm download (recipe.export)'}
        </Button>
      )}
      {receipt ? (
        <Muted>
          Charged {receipt.amount_cents / 100} {receipt.currency} — {receipt.service_id} ({receipt.quote_id.slice(0, 12)}…)
        </Muted>
      ) : null}
    </Stack>
  );
}
