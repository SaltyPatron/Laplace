import { MuBadge } from './MuBadge';

export function RelationChip({
  type,
  label,
  mu,
  witnesses,
}: {
  type: string;
  label?: string;
  mu?: number;
  witnesses?: number;
}) {
  return (
    <span className="relation-chip">
      <strong>{type}</strong>
      {label ? <> → {label}</> : null}
      {(mu !== undefined || witnesses !== undefined) ? (
        <> <MuBadge mu={mu} witnesses={witnesses} /></>
      ) : null}
    </span>
  );
}
