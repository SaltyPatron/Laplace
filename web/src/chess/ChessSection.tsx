import { type ReactNode } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { NavTabs } from '@ui';
import styles from './ChessSection.module.css';

const SECTIONS = [
  { id: 'players', label: 'Players', path: '/chess', match: (p: string) => p === '/chess' || p.startsWith('/chess/players') || p.startsWith('/chess/games') },
  { id: 'laplace', label: 'Laplace games', path: '/chess/laplace', match: (p: string) => p.startsWith('/chess/laplace') },
  { id: 'play', label: 'Play', path: '/play', match: (p: string) => p === '/play' },
  { id: 'lab', label: 'Lab', path: '/lab', match: (p: string) => p === '/lab' || p.startsWith('/lab/') },
] as const;

/** One chess domain: the admitted archive, the board, and the lab share a place. */
export function ChessSection({ children }: { children: ReactNode }) {
  const { pathname } = useLocation();
  const navigate = useNavigate();
  return (
    <div className={styles.section}>
      <NavTabs className={styles.subnav} label="Chess"
        tabs={SECTIONS.map((section) => ({
          id: section.id, label: section.label, href: section.path,
          active: section.match(pathname), onClick: () => navigate(section.path),
        }))} />
      {children}
    </div>
  );
}
