import { useState } from 'react';
import { apiPost } from '../../api/client';
import { useAppStore } from '../../store';
import { useExploreStore } from '../store';
import { GatePrompt } from '../components/GatePrompt';

export function AuditPanel() {
  const { tenant, quoteId } = useAppStore();
  const exploreQuote = useExploreStore((s) => s.quoteId);
  const [report, setReport] = useState<{ counts?: { metric: string; value: number }[] } | null>(null);
  const [unlocked, setUnlocked] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  async function load() {
    setErr(null);
    try {
      const res = await apiPost<{ report: { counts: { metric: string; value: number }[] } }>(
        '/v1/audit/report',
        { include_consensus: true, include_convergence: true },
        { tenant, quoteId: exploreQuote || quoteId },
      );
      setReport(res.report);
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    }
  }

  return (
    <div>
      <h3>Audit</h3>
      {!unlocked ? (
        <GatePrompt serviceId="audit.deep_report" label="Deep substrate audit report." onReady={() => { setUnlocked(true); void load(); }} />
      ) : (
        <ul>
          {report?.counts?.map((c) => (
            <li key={c.metric}>{c.metric}: {c.value.toLocaleString()}</li>
          ))}
        </ul>
      )}
      {err ? <p className="error">{err}</p> : null}
    </div>
  );
}
