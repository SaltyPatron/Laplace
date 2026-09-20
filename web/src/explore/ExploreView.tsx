import { lazy, Suspense } from 'react';
import { NavLink, Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { Breadcrumb } from './components/Breadcrumb';
import { LoadingText } from '@ui';
import { useExploreStore } from './store';
import styles from './ExploreView.module.css';

const loadBrowse = () => import('./browse/BrowseHome');
const loadWarehouse = () => import('./catalog/WarehouseHome');
const loadAudit = () => import('./catalog/AuditPanel');
const loadStage = () => import('./browse/StageBrowse');
const loadSource = () => import('./browse/SourceBrowse');
const loadEntity = () => import('./entity/EntityDetail');
const loadNotFound = () => import('./entity/NotFoundExplorer');
const loadResolve = () => import('./entity/ResolveBrowseRedirect');
const loadConstellation = () => import('./glome/ConstellationView');
const loadHighway = () => import('./highway/HighwayLanding');
const loadLayer = () => import('./highway/LayerPage');
const loadMatchup = () => import('./matchup/MatchupView');
const loadMesh = () => import('./mesh/MeshView');
const loadWalk = () => import('./walk/WalkPanel');

const BrowseHome = lazy(() => loadBrowse().then((m) => ({ default: m.BrowseHome })));
const WarehouseHome = lazy(() => loadWarehouse().then((m) => ({ default: m.WarehouseHome })));
const AuditPanel = lazy(() => loadAudit().then((m) => ({ default: m.AuditPanel })));
const StageBrowse = lazy(() => loadStage().then((m) => ({ default: m.StageBrowse })));
const SourceBrowse = lazy(() => loadSource().then((m) => ({ default: m.SourceBrowse })));
const EntityDetail = lazy(() => loadEntity().then((m) => ({ default: m.EntityDetail })));
const NotFoundExplorer = lazy(() => loadNotFound().then((m) => ({ default: m.NotFoundExplorer })));
const ResolveBrowseRedirect = lazy(() => loadResolve().then((m) => ({ default: m.ResolveBrowseRedirect })));
const ConstellationView = lazy(() => loadConstellation().then((m) => ({ default: m.ConstellationView })));
const HighwayLanding = lazy(() => loadHighway().then((m) => ({ default: m.HighwayLanding })));
const LayerPage = lazy(() => loadLayer().then((m) => ({ default: m.LayerPage })));
const MatchupView = lazy(() => loadMatchup().then((m) => ({ default: m.MatchupView })));
const MeshView = lazy(() => loadMesh().then((m) => ({ default: m.MeshView })));
const WalkPanel = lazy(() => loadWalk().then((m) => ({ default: m.WalkPanel })));

const EXPLORE_PREFETCH: Record<string, () => Promise<unknown>> = {
  '/explore': loadBrowse,
  '/explore/highway': loadHighway,
  '/explore/mesh': loadMesh,
  '/explore/warehouse': loadWarehouse,
  '/explore/matchup': loadMatchup,
  '/explore/constellation': loadConstellation,
  '/explore/walk': loadWalk,
  '/explore/audit': loadAudit,
};

function ExploreBreadcrumb() {
  const { pathname } = useLocation();
  const crumb = useExploreStore((s) => s.breadcrumb);
  const segments: { label: string; to?: string }[] = [{ label: 'Browse', to: '/explore' }];

  const stageMatch = pathname.match(/\/explore\/stage\/([^/]+)/);
  if (stageMatch) {
    const stage = decodeURIComponent(stageMatch[1]);
    segments.push({ label: 'Warehouse', to: '/explore/warehouse' });
    segments.push({ label: stage, to: `/explore/stage/${stageMatch[1]}` });
  }
  const sourceMatch = pathname.match(/\/explore\/source\/([^/]+)/);
  if (sourceMatch) {
    const source = decodeURIComponent(sourceMatch[1]);
    segments.push({ label: 'Warehouse', to: '/explore/warehouse' });
    if (crumb.stage) segments.push({ label: crumb.stage, to: `/explore/stage/${encodeURIComponent(crumb.stage)}` });
    segments.push({ label: source, to: `/explore/source/${sourceMatch[1]}` });
  }
  const entityMatch = pathname.match(/\/explore\/entity\/([0-9a-f]{32})/i);
  if (entityMatch && crumb.entityLabel) {
    segments.push({ label: crumb.entityLabel });
  }

  return <Breadcrumb segments={segments} />;
}

export function ExploreView() {
  const navItems = [
    ['Browse', '/explore'],
    ['Highway', '/explore/highway'],
    ['Mesh', '/explore/mesh'],
    ['Warehouse', '/explore/warehouse'],
    ['Matchup', '/explore/matchup'],
    ['Constellation', '/explore/constellation'],
    ['Walk', '/explore/walk'],
    ['Audit', '/explore/audit'],
  ] as const;

  return (
    <div className={styles.layout}>
      <aside className={styles.sidebar}>
        <details className={styles.navDisclosure} open>
          <summary className={styles.navSummary}>Explore tools</summary>
          <nav className={styles.nav} aria-label="Explore tools">
            {navItems.map(([label, to]) => (
              <NavLink
                key={to}
                className={({ isActive }) => `${styles.navLink} ${isActive ? styles.navLinkActive : ''}`}
                to={to}
                onPointerEnter={() => { void EXPLORE_PREFETCH[to]?.(); }}
                onFocus={() => { void EXPLORE_PREFETCH[to]?.(); }}
                end={to === '/explore' || to === '/explore/warehouse'}
              >
                {label}
              </NavLink>
            ))}
          </nav>
        </details>
      </aside>
      <div className={styles.content}>
        <ExploreBreadcrumb />
        <Suspense fallback={<LoadingText>Loading Explore tool…</LoadingText>}>
        <Routes>
          <Route index element={<BrowseHome />} />
          <Route path="warehouse" element={<WarehouseHome />} />
          <Route path="constellation" element={<ConstellationView />} />
          <Route path="stage/:stageId" element={<StageBrowse />} />
          <Route path="source/:sourceKey" element={<SourceBrowse />} />
          <Route path="entity/:idHex" element={<EntityDetail />} />
          <Route path="notfound/:ref" element={<NotFoundExplorer />} />
          <Route path="resolve/:ref" element={<ResolveBrowseRedirect />} />
          <Route path="walk" element={<WalkPanel />} />
          <Route path="highway" element={<HighwayLanding />} />
          <Route path="highway/:slug" element={<LayerPage />} />
          <Route path="mesh" element={<MeshView />} />
          <Route path="mesh/:id" element={<MeshView />} />
          <Route path="matchup" element={<MatchupView />} />
          <Route path="matchup/:x/:y" element={<MatchupView />} />
          <Route path="audit" element={<AuditPanel />} />
          <Route path="*" element={<Navigate to="/explore" replace />} />
        </Routes>
        </Suspense>
      </div>
    </div>
  );
}
