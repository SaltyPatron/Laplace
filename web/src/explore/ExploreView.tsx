import { Link, Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { WarehouseHome } from './catalog/WarehouseHome';
import { AuditPanel } from './catalog/AuditPanel';
import { StageBrowse } from './browse/StageBrowse';
import { SourceBrowse } from './browse/SourceBrowse';
import { EntityDetail, ResolveRedirect } from './entity/EntityDetail';
import { NotFoundExplorer } from './entity/NotFoundExplorer';
import { ConstellationView } from './glome/ConstellationView';
import { MatchupView } from './matchup/MatchupView';
import { MeshView } from './mesh/MeshView';
import { WalkPanel } from './walk/WalkPanel';
import { Breadcrumb } from './components/Breadcrumb';
import { useExploreStore } from './store';
import styles from './ExploreView.module.css';

function ExploreBreadcrumb() {
  const { pathname } = useLocation();
  const crumb = useExploreStore((s) => s.breadcrumb);
  const segments: { label: string; to?: string }[] = [{ label: 'Warehouse', to: '/explore' }];

  const stageMatch = pathname.match(/\/explore\/stage\/([^/]+)/);
  if (stageMatch) {
    const stage = decodeURIComponent(stageMatch[1]);
    segments.push({ label: stage, to: `/explore/stage/${stageMatch[1]}` });
  }
  const sourceMatch = pathname.match(/\/explore\/source\/([^/]+)/);
  if (sourceMatch) {
    const source = decodeURIComponent(sourceMatch[1]);
    if (crumb.stage) segments.splice(1, 0, { label: crumb.stage, to: `/explore/stage/${encodeURIComponent(crumb.stage)}` });
    segments.push({ label: source, to: `/explore/source/${sourceMatch[1]}` });
  }
  const entityMatch = pathname.match(/\/explore\/entity\/([0-9a-f]{32})/i);
  if (entityMatch && crumb.entityLabel) {
    segments.push({ label: crumb.entityLabel });
  }

  return <Breadcrumb segments={segments} />;
}

export function ExploreView() {
  return (
    <div className={styles.layout}>
      <aside className={styles.sidebar}>
        <nav className={styles.nav}>
          <Link className={styles.navLink} to="/explore/mesh">Mesh</Link>
          <Link className={styles.navLink} to="/explore">Warehouse</Link>
          <Link className={styles.navLink} to="/explore/matchup">Matchup</Link>
          <Link className={styles.navLink} to="/explore/constellation">Constellation</Link>
          <Link className={styles.navLink} to="/explore/walk">Walk</Link>
          <Link className={styles.navLink} to="/explore/audit">Audit</Link>
        </nav>
      </aside>
      <div className={styles.content}>
        <ExploreBreadcrumb />
        <Routes>
          <Route index element={<WarehouseHome />} />
          <Route path="constellation" element={<ConstellationView />} />
          <Route path="stage/:stageId" element={<StageBrowse />} />
          <Route path="source/:sourceKey" element={<SourceBrowse />} />
          <Route path="entity/:idHex" element={<EntityDetail />} />
          <Route path="notfound/:ref" element={<NotFoundExplorer />} />
          <Route path="resolve/:ref" element={<ResolveRedirect />} />
          <Route path="walk" element={<WalkPanel />} />
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
