import { Link, Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { WarehouseHome } from './catalog/WarehouseHome';
import { AuditPanel } from './catalog/AuditPanel';
import { StageBrowse } from './browse/StageBrowse';
import { SourceBrowse } from './browse/SourceBrowse';
import { EntityDetail, ResolveRedirect } from './entity/EntityDetail';
import { ConstellationView } from './glome/ConstellationView';
import { WalkPanel } from './walk/WalkPanel';
import { Breadcrumb } from './components/Breadcrumb';
import { useExploreStore } from './store';

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
    <div className="explore-layout">
      <aside className="explore-sidebar">
        <nav>
          <Link to="/explore">Warehouse</Link>
          <Link to="/explore/constellation">Constellation</Link>
          <Link to="/explore/walk">Walk</Link>
          <Link to="/explore/audit">Audit</Link>
          <Link to="/">← App</Link>
        </nav>
      </aside>
      <div className="explore-content">
        <ExploreBreadcrumb />
        <Routes>
          <Route index element={<WarehouseHome />} />
          <Route path="constellation" element={<ConstellationView />} />
          <Route path="stage/:stageId" element={<StageBrowse />} />
          <Route path="source/:sourceKey" element={<SourceBrowse />} />
          <Route path="entity/:idHex" element={<EntityDetail />} />
          <Route path="resolve/:ref" element={<ResolveRedirect />} />
          <Route path="walk" element={<WalkPanel />} />
          <Route path="audit" element={<AuditPanel />} />
          <Route path="*" element={<Navigate to="/explore" replace />} />
        </Routes>
      </div>
    </div>
  );
}
