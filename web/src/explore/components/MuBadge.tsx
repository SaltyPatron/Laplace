export function MuBadge({ mu, witnesses }: { mu?: number; witnesses?: number }) {
  if (mu === undefined && witnesses === undefined) return null;
  return (
    <span className="mu-badge">
      {mu !== undefined ? `μ ${mu.toFixed(1)}` : null}
      {witnesses !== undefined ? ` · ${witnesses} wit` : null}
    </span>
  );
}
