import { Link } from 'react-router-dom';

export interface BreadcrumbSegment {
  label: string;
  to?: string;
}

export function Breadcrumb({ segments }: { segments: BreadcrumbSegment[] }) {
  if (segments.length === 0) return null;
  return (
    <nav className="explore-breadcrumb" aria-label="Breadcrumb">
      {segments.map((seg, i) => (
        <span key={`${seg.label}-${i}`} className="crumb">
          {i > 0 ? <span className="sep">›</span> : null}
          {seg.to ? <Link to={seg.to}>{seg.label}</Link> : <span>{seg.label}</span>}
        </span>
      ))}
    </nav>
  );
}
