import { type ReactNode } from 'react';
import styles from './ChessSection.module.css';

/** Chess pages. Section navigation (Players, Laplace games, Play, Lab) lives in the app rail. */
export function ChessSection({ children }: { children: ReactNode }) {
  return <div className={styles.section}>{children}</div>;
}
