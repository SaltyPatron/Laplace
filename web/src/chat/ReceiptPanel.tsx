import { useState } from 'react';
import { apiGet, ApiError, type EvidenceResponse } from '../api/client';
import { asNum, useAppStore } from '../store';

const OUTCOME = ['refute', 'draw', 'confirm'] as const;

function outcomeName(outcome: string | number | null | undefined): (typeof OUTCOME)[number] {
  return OUTCOME[asNum(outcome ?? 1)] ?? 'draw';
}

/**
 * The receipt drill-down: look any word (or entity id) up against /v1/evidence and
 * see the raw witnessed attestations — source, relation, outcome, observation count.
 */
export function ReceiptPanel() {
  const tenant = useAppStore((s) => s.tenant);
  const [target, setTarget] = useState('');
  const [evidence, setEvidence] = useState<EvidenceResponse | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  async function lookup() {
    const query = target.trim();
    if (!query) return;
    setLoading(true);
    setError('');
    setEvidence(null);
    try {
      setEvidence(await apiGet<EvidenceResponse>(
        `/v1/evidence/${encodeURIComponent(query)}?limit=25`, { tenant }));
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Evidence lookup failed.');
    } finally {
      setLoading(false);
    }
  }

  return (
    <aside className="receipt-panel">
      <h2>Evidence</h2>
      <p className="hint">Every claim decomposes into named witnesses. Look one up.</p>
      <div className="row">
        <input
          value={target}
          placeholder="word or entity id"
          onChange={(e) => setTarget(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && lookup()}
        />
        <button onClick={lookup} disabled={loading}>
          {loading ? '…' : 'Look up'}
        </button>
      </div>
      {error && <p className="error">{error}</p>}
      {evidence && (
        <div className="evidence">
          <h3>
            {evidence.entity_label}
            <span className="entity-id">{evidence.entity_id}</span>
          </h3>
          {evidence.evidence.length === 0 && <p className="hint">No outbound attestations.</p>}
          <ul>
            {evidence.evidence.map((item, i) => (
              <li key={i} className={`outcome-${item.outcome}`}>
                <span className="relation">{item.type_label}</span>{' '}
                <span className="object">{item.object_label}</span>
                <span className="source" title={item.source_id ?? undefined}>
                  {item.source_label}
                </span>
                <span className={`chip chip-${outcomeName(item.outcome)}`}>
                  {outcomeName(item.outcome)} ×{item.observation_count}
                </span>
              </li>
            ))}
          </ul>
        </div>
      )}
    </aside>
  );
}
