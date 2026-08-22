import { Route, Routes, useLocation, useNavigate } from 'react-router-dom';
import { Muted, NavTabs } from '@ui';
import { ExperimentRunner } from './ExperimentRunner';
import { GauntletView } from './gauntlet/GauntletView';
import { LichessPanel } from './LichessPanel';
import styles from './LabView.module.css';

/**
 * One shell for three lab operations that were previously stacked into a single scrolling
 * page: substrate experiments, the external engine gauntlet, and Lichess.
 *
 * They were never one workflow. Running a gauntlet means watching an external process for
 * an hour; running a substrate test means filling a form and reading a table; Lichess is a
 * long-lived connection with its own state. Sharing a page meant every one of them was
 * mostly chrome belonging to the other two, and the operation you actually came for was
 * somewhere below the fold. Each is a route now, so each can be deep-linked, and each
 * shows only its own jobs.
 */
const TABS: { id: string; label: string; path: string; blurb: string }[] = [
  {
    id: 'experiments',
    label: 'Experiments',
    path: '/lab',
    blurb: 'In-process runs against the substrate: lift tests, eval ablation, tactics, review.',
  },
  {
    id: 'gauntlet',
    label: 'Gauntlet',
    path: '/lab/gauntlet',
    blurb: 'laplace-uci vs Stockfish through cutechess-cli, with the full process transcript.',
  },
  {
    id: 'lichess',
    label: 'Lichess',
    path: '/lab/lichess',
    blurb: 'Bot connectivity and player-game fetches.',
  },
];

export function LabView() {
  const nav = useNavigate();
  const { pathname } = useLocation();
  const activeTab = TABS.find((t) => t.path === pathname) ?? TABS[0];

  return (
    <div className={styles.lab}>
      <header className={styles.hero}>
        <div className={styles.heroText}>
          <h3>Chess Lab</h3>
          <Muted>{activeTab.blurb}</Muted>
        </div>
        <NavTabs
          className={styles.subnav}
          tabs={TABS.map((t) => ({
            id: t.id,
            label: t.label,
            active: t.id === activeTab.id,
            onClick: () => nav(t.path),
          }))}
        />
      </header>

      <div className={styles.body}>
        <Routes>
          <Route
            index
            element={<ExperimentRunner categories={['substrate', 'diagnostics']} initialKind="substrate-test" />}
          />
          <Route path="gauntlet" element={<GauntletView />} />
          <Route
            path="lichess"
            element={(
              <div className={styles.stack}>
                <LichessPanel />
                <ExperimentRunner categories={['lichess']} initialKind="lichess-fetch" />
              </div>
            )}
          />
        </Routes>
      </div>
    </div>
  );
}
