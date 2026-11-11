import { useState } from 'react';
import { ErrorText, Muted, Panel, Table, Td, Th } from '@ui';
import { apiPost } from '../../api/client';
import { useAppStore } from '../../store';
import { useExploreStore } from '../store';
import { GatePrompt } from '../components/GatePrompt';

interface Count { metric: string; value: number | null }

/** "attestations(MEASURED)" → { label: "Attestations", how: "counted" }. */
export function describeMetric(metric: string): { label: string; how: string } {
  const match = /^(.*?)\s*\((MEASURED|ESTIMATE|ESTIMATED)\)\s*$/i.exec(metric);
  const name = (match ? match[1] : metric).replaceAll('_', ' ').trim();
  const how = !match ? '' : /^MEASURED$/i.test(match[2]) ? 'counted' : 'estimated from storage statistics';
  return { label: name.charAt(0).toUpperCase() + name.slice(1), how };
}

export function AuditPanel() {
  const { tenant, quoteId } = useAppStore();
  const exploreQuote = useExploreStore((s) => s.quoteId);
  const [report, setReport] = useState<{ counts?: Count[] } | null>(null);
  const [unlocked, setUnlocked] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  async function load() {
    setErr(null);
    try {
      const res = await apiPost<{ report: { counts: Count[] } }>(
        '/v1/audit/report',
        { include_consensus: true, include_convergence: true },
        { tenant, quoteId: exploreQuote || quoteId },
      );
      setReport(res.report);
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    }
  }

  const counts = report?.counts ?? [];
  return (
    <Panel title="Audit">
      {!unlocked ? (
        <GatePrompt
          serviceId="audit.deep_report"
          label="Deep substrate audit report."
          onReady={() => { setUnlocked(true); void load(); }}
        />
      ) : report && counts.length === 0 ? (
        <Muted>The audit returned no counts.</Muted>
      ) : counts.length > 0 ? (
        <Table>
          <thead>
            <tr><Th>What</Th><Th>Amount</Th><Th>How</Th></tr>
          </thead>
          <tbody>
            {counts.map((c) => {
              const { label, how } = describeMetric(c.metric);
              return (
                <tr key={c.metric}>
                  <Td>{label}</Td>
                  <Td>{c.value == null ? 'not available' : c.value.toLocaleString()}</Td>
                  <Td>{c.value == null ? 'this count was not taken' : how}</Td>
                </tr>
              );
            })}
          </tbody>
        </Table>
      ) : null}
      {err ? <ErrorText>{err}</ErrorText> : null}
    </Panel>
  );
}
