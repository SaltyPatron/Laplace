import { lazy, Suspense } from 'react';
import { Navigate, Route, Routes, useLocation, useNavigate } from 'react-router-dom';
import { LoadingText, Muted, NavTabs } from '@ui';
import styles from './LabView.module.css';

const loadExperiments = () => import('./ExperimentRunner');
const loadGauntlet = () => import('./gauntlet/GauntletView');
const loadLichess = () => import('./LichessPanel');
const loadCalibration = () => import('./gauntlet/CalibrationPanel');

const ExperimentRunner = lazy(() => loadExperiments().then((m) => ({ default: m.ExperimentRunner })));
const GauntletView = lazy(() => loadGauntlet().then((m) => ({ default: m.GauntletView })));
const LichessPanel = lazy(() => loadLichess().then((m) => ({ default: m.LichessPanel })));
const CalibrationPanel = lazy(() => loadCalibration().then((m) => ({ default: m.CalibrationPanel })));

const LAB_PREFETCH: Record<string, () => Promise<unknown>> = {
  experiments: loadExperiments,
  gauntlet: loadGauntlet,
  calibration: loadCalibration,
  import: async () => { await Promise.all([loadExperiments(), loadLichess()]); },
};

/** Chess experiments, measured machine calibration, external matches and imports. */
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
    id: 'calibration',
    label: 'Calibration',
    path: '/lab/calibration',
    blurb: 'Measured Stockfish and CuteChess performance, engine identity, and machine settings.',
  },
  {
    id: 'import',
    label: 'Import',
    path: '/lab/import',
    blurb: 'Search and import FIDE, Chess.com, and Lichess profiles; optionally import games.',
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
            onIntent: () => { void LAB_PREFETCH[t.id]?.(); },
            onClick: () => nav(t.path),
          }))}
        />
      </header>

      <div className={styles.body}>
        <Suspense fallback={<LoadingText>Loading Lab tool…</LoadingText>}>
        <Routes>
          <Route
            index
            element={<ExperimentRunner categories={['substrate', 'diagnostics']} initialKind="substrate-test" />}
          />
          <Route path="gauntlet" element={<GauntletView />} />
          <Route path="calibration" element={<CalibrationPanel />} />
          <Route
            path="import"
            element={(
              <div className={styles.stack}>
                <ExperimentRunner categories={['import']} initialKind="player-profile" />
                <LichessPanel />
              </div>
            )}
          />
          <Route path="lichess" element={<Navigate to="/lab/import" replace />} />
        </Routes>
        </Suspense>
      </div>
    </div>
  );
}
