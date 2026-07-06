import { Link } from 'react-router-dom';

export function EntityLink({ idHex, label }: { idHex: string; label: string }) {
  return (
    <Link to={`/explore/entity/${idHex}`} className="entity-link" title={idHex}>
      {label}
    </Link>
  );
}
