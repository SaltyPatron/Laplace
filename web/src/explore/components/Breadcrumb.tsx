import { Link as RouterLink } from 'react-router-dom';
import styles from './Breadcrumb.module.css';

export interface BreadcrumbSegment {
  label: string;
  to?: string;
}

export function Breadcrumb({ segments }: { segments: BreadcrumbSegment[] }) {
  if (segments.length === 0) return null;
  return (
    <nav className={styles.breadcrumb} aria-label="Breadcrumb">
      {segments.map((seg, i) => (
        <span key={`${seg.label}-${i}`} className={styles.crumb}>
          {i > 0 ? <span className={styles.sep}>›</span> : null}
          {seg.to ? (
            <RouterLink to={seg.to} className={styles.link}>{seg.label}</RouterLink>
          ) : (
            <span>{seg.label}</span>
          )}
        </span>
      ))}
    </nav>
  );
}
