import { lazy, Suspense } from 'react';
import { Navigate, Route, Routes, useLocation, useNavigate, useParams } from 'react-router-dom';
import { decodeRouteText } from '../layout/useRouteText';
import { changeWorld } from '../world/context';
import { Breadcrumb } from './components/Breadcrumb';
import { LoadingText, NavTabs } from '@ui';
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
const loadQuery = () => import('../query/QueryConsole');
const loadProof = () => import('./proof/StorageProofView');
const loadUnicode = () => import('./unicode/UnicodeGlomeView');
const loadArchitecture = () => import('./architecture/ArchitectureView');

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
const QueryConsole = lazy(() => loadQuery().then((m) => ({ default: m.QueryConsole })));
const StorageProofView = lazy(() => loadProof().then((m) => ({ default: m.StorageProofView })));
const UnicodeGlomeView = lazy(() => loadUnicode().then((m) => ({ default: m.UnicodeGlomeView })));
const ArchitectureView = lazy(() => loadArchitecture().then((m) => ({ default: m.ArchitectureView })));

/** Shared read layout for existing route addresses. */
export type ExplorePage = 'topic' | 'query' | 'proof' | 'unicode';

function TopicWorkspace() {
  const { ref } = useParams();
  const { search } = useLocation();
  const value = ref ? decodeRouteText(ref) : null;
  const params = changeWorld(new URLSearchParams(search), value ? { q: value, ...(/^[a-f0-9]{32}$/i.test(value) ? { selected: value } : {}) } : {});
  return <Navigate to={`/explore${params.size ? '?' + params : ''}`} replace />;
}

function WarehouseWorkspace() {
  const { search } = useLocation();
  const params = new URLSearchParams(search);
  return ['source', 'sources', 'stage', 'layer', 'role'].some(key => params.has(key))
    ? <Navigate to={`/explore/sources${search}`} replace /> : <BrowseHome />;
}

function ExploreBreadcrumb() {
  const { pathname, search } = useLocation();
  const params = new URLSearchParams(search);
  const crumb = useExploreStore((s) => s.breadcrumb);
  const searchHref = `/explore${params.get('q') ? `?q=${encodeURIComponent(params.get('q')!)}` : ''}`;
  const segments: { label: string; to?: string }[] = [{ label: 'Knowledge', to: searchHref }];
  if (pathname === '/explore/sources') segments.push({ label: 'Sources' });
  if (pathname === '/explore/highway') segments.push({ label: 'Highway' });
  const highwayMatch = pathname.match(/\/explore\/highway\/([^/]+)/);
  if (highwayMatch) {
    segments.push({ label: 'Highway', to: '/explore/highway' });
    segments.push({ label: decodeRouteText(highwayMatch[1]) });
  }
  if (pathname.startsWith('/explore/constellation')) segments.push({ label: 'Constellation' });
  if (pathname.startsWith('/unicode')) segments.push({ label: 'Unicode Glome' });
  if (pathname.startsWith('/proof')) segments.push({ label: 'Storage Proof' });
  if (pathname.startsWith('/explore/walk')) segments.push({ label: 'Forward Trace' });
  if (pathname === '/explore/mesh' || pathname.startsWith('/explore/mesh/')) segments.push({ label: 'Mesh', to: '/explore/mesh' });
  if (pathname === '/explore/matchup' || pathname.startsWith('/explore/matchup/')) segments.push({ label: 'Matchup', to: '/explore/matchup' });
  if (pathname.startsWith('/explore/audit')) segments.push({ label: 'Audit' });

  const stageMatch = pathname.match(/\/explore\/stage\/([^/]+)/);
  if (pathname.startsWith('/explore/architecture')) segments.push({ label: 'Architecture & operations' });
  if (stageMatch) {
    const stage = decodeRouteText(stageMatch[1]);
    segments.push({ label: 'Sources', to: '/explore/sources' });
    segments.push({ label: stage, to: `/explore/stage/${stageMatch[1]}` });
  }
  const sourceMatch = pathname.match(/\/explore\/source\/([^/]+)/);
  if (sourceMatch) {
    const source = decodeRouteText(sourceMatch[1]);
    segments.push({ label: 'Sources', to: '/explore/sources' });
    if (crumb.stage) segments.push({ label: crumb.stage, to: `/explore/stage/${encodeURIComponent(crumb.stage)}` });
    segments.push({ label: source, to: `/explore/source/${sourceMatch[1]}` });
  }
  const entityMatch = pathname.match(/\/explore\/entity\/([0-9a-f]{32})/i);
  if (entityMatch && crumb.entityLabel) {
    segments.push({ label: crumb.entityLabel });
  }

  return <Breadcrumb segments={segments} />;
}

const EXPLORE_SECTIONS = [
  { id: 'search', label: 'Search', path: '/explore', load: loadBrowse, match: (p: string) => p === '/explore' || p === '/query' || p.startsWith('/topic') || p.startsWith('/explore/entity/') || p.startsWith('/explore/notfound/') || p.startsWith('/explore/resolve/') },
  { id: 'sources', label: 'Sources', path: '/explore/sources', load: loadWarehouse, match: (p: string) => p === '/explore/sources' || p === '/explore/warehouse' || p.startsWith('/explore/source/') || p.startsWith('/explore/stage/') },
  { id: 'highway', label: 'Highway', path: '/explore/highway', load: loadHighway, match: (p: string) => p === '/explore/highway' || p.startsWith('/explore/highway/') },
  { id: 'constellation', label: 'Constellation', path: '/explore/constellation', load: loadConstellation, match: (p: string) => p.startsWith('/explore/constellation') },
  { id: 'unicode', label: 'Unicode Glome', path: '/unicode', load: loadUnicode, match: (p: string) => p.startsWith('/unicode') },
  { id: 'proof', label: 'Storage Proof', path: '/proof', load: loadProof, match: (p: string) => p.startsWith('/proof') },
  { id: 'walk', label: 'Forward Trace', path: '/explore/walk', load: loadWalk, match: (p: string) => p.startsWith('/explore/walk') },
  { id: 'mesh', label: 'Mesh', path: '/explore/mesh', load: loadMesh, match: (p: string) => p === '/explore/mesh' || p.startsWith('/explore/mesh/') },
  { id: 'matchup', label: 'Matchup', path: '/explore/matchup', load: loadMatchup, match: (p: string) => p === '/explore/matchup' || p.startsWith('/explore/matchup/') },
  { id: 'audit', label: 'Audit', path: '/explore/audit', load: loadAudit, match: (p: string) => p.startsWith('/explore/audit') },
  { id: 'architecture', label: 'Architecture', path: '/explore/architecture', load: loadArchitecture, match: (p: string) => p.startsWith('/explore/architecture') },
] as const;

function sectionDestination(sectionId: (typeof EXPLORE_SECTIONS)[number]['id'], basePath: string, pathname: string, search: string, crumb: { entityLabel?: string; entityId?: string }): string {
  const params = new URLSearchParams(search);
  const entityMatch = pathname.match(/^\/explore\/(?:entity|mesh)\/([0-9a-f]{32})$/i);
  const matchupMatch = pathname.match(/^\/explore\/matchup\/([^/]+)\/([^/]+)$/);
  const activeEntityId = params.get('selected') ?? entityMatch?.[1] ?? null;
  const activeSubject = (entityMatch && crumb.entityId === entityMatch[1] && crumb.entityLabel ? crumb.entityLabel : null)
    ?? params.get('q')
    ?? params.get('x')
    ?? (matchupMatch ? decodeRouteText(matchupMatch[1]) : null)
    ?? activeEntityId;
  const secondSubject = params.get('other') ?? params.get('y') ?? (matchupMatch ? decodeRouteText(matchupMatch[2]) : null);

  if (sectionId === 'search') {
    const next = new URLSearchParams();
    for (const [k, v] of params.entries()) {
      if (!['x', 'y', 'source', 'sources', 'entity', 'diagram', 'operation', 'section', 'file', 'expected'].includes(k)) next.append(k, v);
    }
    if (!next.has('q') && activeSubject && !pathname.startsWith('/explore/architecture')) next.set('q', activeSubject);
    if (!next.has('selected') && activeEntityId) next.set('selected', activeEntityId);
    return next.size ? `/explore?${next}` : '/explore';
  }
  if (sectionId === 'proof') {
    return activeSubject && !pathname.startsWith('/explore/architecture') ? `/proof?q=${encodeURIComponent(activeSubject)}` : '/proof';
  }
  if (sectionId === 'mesh') {
    if (activeEntityId) return `/explore/mesh/${activeEntityId}`;
    return params.get('q') && !pathname.startsWith('/explore/architecture') ? `/explore/mesh?q=${encodeURIComponent(params.get('q')!)}` : '/explore/mesh';
  }
  if (sectionId === 'matchup') {
    if (activeSubject && secondSubject && !pathname.startsWith('/explore/architecture')) {
      return `/explore/matchup/${encodeURIComponent(activeSubject)}/${encodeURIComponent(secondSubject)}`;
    }
    if (activeSubject && !pathname.startsWith('/explore/architecture')) {
      return `/explore/matchup?x=${encodeURIComponent(activeSubject)}`;
    }
    return '/explore/matchup';
  }
  return basePath;
}

function ExploreSubnav() {
  const { pathname, search } = useLocation();
  const navigate = useNavigate();
  const crumb = useExploreStore((s) => s.breadcrumb);
  return (
    <NavTabs
      label="Knowledge sections"
      tabs={EXPLORE_SECTIONS.map((section) => {
        const target = sectionDestination(section.id, section.path, pathname, search, crumb);
        return {
          id: section.id,
          label: section.label,
          href: target,
          active: section.match(pathname),
          onIntent: () => { void section.load(); },
          onClick: () => navigate(target),
        };
      })}
    />
  );
}

export function ExploreView({ page }: { page?: ExplorePage }) {
  return (
    <div className={styles.layout}>
      <div className={styles.content}>
        <ExploreSubnav />
        <ExploreBreadcrumb />
        <Suspense fallback={<LoadingText>Loading knowledge workspace…</LoadingText>}>
        {page === 'topic' ? <TopicWorkspace /> : page === 'query' ? <QueryConsole /> : page === 'proof' ? <StorageProofView /> : page === 'unicode' ? <UnicodeGlomeView /> : <Routes>
          <Route index element={<BrowseHome />} />
          <Route path="warehouse" element={<WarehouseWorkspace />} />
          <Route path="sources" element={<WarehouseHome />} />
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
          <Route path="architecture" element={<ArchitectureView />} />
          <Route path="*" element={<Navigate to="/explore" replace />} />
        </Routes>}
        </Suspense>
      </div>
    </div>
  );
}
