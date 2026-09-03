import { NavLink, Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { WarehouseHome } from './catalog/WarehouseHome';
import { AuditPanel } from './catalog/AuditPanel';
import { BrowseHome } from './browse/BrowseHome';
import { StageBrowse } from './browse/StageBrowse';
import { SourceBrowse } from './browse/SourceBrowse';
import { EntityDetail, ResolveRedirect } from './entity/EntityDetail';
import { NotFoundExplorer } from './entity/NotFoundExplorer';
import { ConstellationView } from './glome/ConstellationView';
import { HighwayLanding } from './highway/HighwayLanding';
import { LayerPage } from './highway/LayerPage';
import { MatchupView } from './matchup/MatchupView';
import { MeshView } from './mesh/MeshView';
import { WalkPanel } from './walk/WalkPanel';
import { Breadcrumb } from './components/Breadcrumb';
import { useExploreStore } from './store';
import styles from './ExploreView.module.css';

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
        <nav className={styles.nav}>
          {navItems.map(([label, to]) => (
            <NavLink
              key={to}
              className={({ isActive }) => `${styles.navLink} ${isActive ? styles.navLinkActive : ''}`}
              to={to}
              end={to === '/explore' || to === '/explore/warehouse'}
            >
              {label}
            </NavLink>
          ))}
        </nav>
      </aside>
      <div className={styles.content}>
        <ExploreBreadcrumb />
        <Routes>
          <Route index element={<BrowseHome />} />
          <Route path="warehouse" element={<WarehouseHome />} />
          <Route path="constellation" element={<ConstellationView />} />
          <Route path="stage/:stageId" element={<StageBrowse />} />
          <Route path="source/:sourceKey" element={<SourceBrowse />} />
          <Route path="entity/:idHex" element={<EntityDetail />} />
          <Route path="notfound/:ref" element={<NotFoundExplorer />} />
          <Route path="resolve/:ref" element={<ResolveRedirect />} />
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
      </div>
    </div>
  );
}
