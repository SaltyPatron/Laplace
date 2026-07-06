import { useState } from 'react';
import { apiGet, ApiError, type EvidenceResponse } from '../api/client';
import { asNum, useAppStore } from '../store';

function formatMu(value: string | number | null | undefined): string | null {
  if (value === null || value === undefined) return null;
  const n = typeof value === 'number' ? value : Number(value);
  return Number.isFinite(n) ? n.toFixed(1) : null;
}

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
      <p className="hint">Ranked consensus facts for an entity — one row per relation, μ and witness count.</p>
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
          {evidence.evidence.length === 0 && <p className="hint">No outbound consensus yet.</p>}
          <ul>
            {evidence.evidence.map((item, i) => {
              const mu = formatMu(item.eff_mu);
              const witnesses = asNum(item.observation_count);
              return (
                <li key={`${item.type_id}-${item.object_id}-${i}`}>
                  <span className="relation">{item.type_label}</span>{' '}
                  <span className="object">{item.object_label}</span>
                  {(mu || witnesses > 0) && (
                    <span className="chip chip-confirm">
                      {mu ? `μ ${mu}` : null}
                      {mu && witnesses > 0 ? ' · ' : null}
                      {witnesses > 0 ? `${witnesses} wit` : null}
                    </span>
                  )}
                </li>
              );
            })}
          </ul>
        </div>
      )}
    </aside>
  );
}
