import { Panel } from '@ui';
import { ExportPanel } from '../../components/ExportPanel';
import type { BillingReceipt } from '../../types';

export function ExportTab({
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
  return (
    <Panel title="Training export">
      <ExportPanel
        busy={busy}
        onExport={onExport}
        receipt={receipt}
        witnessRows={witnessRows}
        consensusRows={consensusRows}
      />
    </Panel>
  );
}
