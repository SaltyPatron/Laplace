import type { ProvenanceEntry } from '../store';





export function ProvenanceBadge({ entry }: { entry: ProvenanceEntry }) {
  if (entry.ordUsed !== undefined) {
    return <span className="badge badge-ord" title="n-gram context order used for this token">ord {entry.ordUsed}</span>;
  }
  if (entry.effMu === undefined) return null;

  const mu = entry.effMu;
  const clamped = Math.max(0, Math.min(1, mu));
  const hue = Math.round(clamped * 120); 
  return (
    <span
      className="badge badge-mu"
      style={{ borderColor: `hsl(${hue} 70% 45%)`, color: `hsl(${hue} 70% 65%)` }}
      title={`eff_mu ${mu} — Glicko-2 95% confidence lower bound; ${entry.witnesses ?? 0} witnesses`}
    >
      μ {mu.toFixed(3)} · {entry.witnesses ?? 0}w
    </span>
  );
}
