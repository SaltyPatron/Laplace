import { lazy, Suspense, useEffect } from 'react';
import { BrowserRouter, Link as RouterLink, Route, Routes, useLocation, useNavigate } from 'react-router-dom';
import { AppHeader, LoadingText, NavTabs, Panel, TenantField } from '@ui';
import { HomeView } from './home/HomeView';
import { DataActivity, UploadProvider } from './data/UploadProvider';
import { useAppStore } from './store';
import { SubstrateStatusBanner } from './layout/SubstrateStatusBanner';
import { AmbientFamiliar } from './layout/AmbientFamiliar';
import { ViewErrorBoundary } from './layout/ViewErrorBoundary';
import { AccountControls } from './auth/AccountControls';
import { apiGet, setApiWorkspace } from './api/client';
import type { AuthProvider, AuthUser } from './store';
import styles from './App.module.css';

const loadChat = () => import('./chat/ChatView');
const loadQuery = () => import('./query/QueryConsole');
const loadTopic = () => import('./topic/TopicView');
const loadBilling = () => import('./billing/BillingView');
const loadSettings = () => import('./auth/SettingsView');
const loadPlay = () => import('./chess/ChessView');
const loadLab = () => import('./chess/lab/LabView');
const loadChess = () => import('./chess/db/ChessDbView');
const loadExplore = () => import('./explore/ExploreView');
const loadProof = () => import('./explore/proof/StorageProofView');
const loadForwardProof = () => import('./forward/ForwardProofView');
const loadUnicode = () => import('./explore/unicode/UnicodeGlomeView');
const loadOperator = () => import('./admin/AdminView');
const loadData = () => import('./data/DataView');

const ChatView = lazy(() => loadChat().then((m) => ({ default: m.ChatView })));
const QueryConsole = lazy(() => loadQuery().then((m) => ({ default: m.QueryConsole })));
const TopicView = lazy(() => loadTopic().then((m) => ({ default: m.TopicView })));
const BillingView = lazy(() => loadBilling().then((m) => ({ default: m.BillingView })));
const BillingReturnView = lazy(() => loadSettings().then((m) => ({ default: m.BillingReturnView })));
const SettingsView = lazy(() => loadSettings().then((m) => ({ default: m.SettingsView })));
const ChessView = lazy(() => loadPlay().then((m) => ({ default: m.ChessView })));
const LabView = lazy(() => loadLab().then((m) => ({ default: m.LabView })));
const ChessDbView = lazy(() => loadChess().then((m) => ({ default: m.ChessDbView })));
const ExploreView = lazy(() => loadExplore().then((m) => ({ default: m.ExploreView })));
const StorageProofView = lazy(() => loadProof().then((m) => ({ default: m.StorageProofView })));
const ForwardProofView = lazy(() => loadForwardProof().then((m) => ({ default: m.ForwardProofView })));
const UnicodeGlomeView = lazy(() => loadUnicode().then((m) => ({ default: m.UnicodeGlomeView })));
const AdminView = lazy(() => loadOperator().then((m) => ({ default: m.AdminView })));
const DataView = lazy(() => loadData().then((m) => ({ default: m.DataView })));

const WORKSPACE_PREFETCH: Partial<Record<string, () => Promise<unknown>>> = {
  chat: loadChat, query: loadQuery, explore: loadExplore, proof: loadProof, forwardProof: loadForwardProof, unicode: loadUnicode,
  data: loadData, chess: loadChess, play: loadPlay, lab: loadLab, billing: loadBilling,
  settings: loadSettings, operator: loadOperator,
};

const TABS: { id: string; label: string; path: string }[] = [
  { id: 'home', label: 'Home', path: '/' },
  { id: 'chat', label: 'Chat', path: '/chat' },
  { id: 'query', label: 'Query', path: '/query' },
  { id: 'explore', label: 'Explore', path: '/explore' },
  { id: 'proof', label: 'Storage Proof', path: '/proof' },
  { id: 'forwardProof', label: 'Forward Pass Proof', path: '/forward-proof' },
  { id: 'unicode', label: 'Unicode Glome', path: '/unicode' },
  { id: 'data', label: 'Data', path: '/data' },
  { id: 'chess', label: 'Chess', path: '/chess' },
  { id: 'play', label: 'Play', path: '/play' },
  { id: 'lab', label: 'Lab', path: '/lab' },
  { id: 'billing', label: 'Billing', path: '/billing' },
  { id: 'settings', label: 'Settings', path: '/settings' },
  { id: 'operator', label: 'Operator', path: '/operator' },
];
function isActive(pathname: string, tabPath: string): boolean {
  return tabPath === '/' ? pathname === '/' : pathname === tabPath || pathname.startsWith(`${tabPath}/`);
}
function Shell() {
  const { tenant, setTenant, authReady, authUser, authProviders, setAuth } = useAppStore();
  const navigate = useNavigate();
  const location = useLocation();
  const viewScope = JSON.stringify([tenant, authUser?.id]);
  useEffect(() => {
    let live = true;
    void apiGet<{ authenticated: boolean; user: AuthUser | null; providers: AuthProvider[] }>('/v1/auth/me')
      .then((result) => {
        if (!live) return;
        setApiWorkspace(result.authenticated ? result.user?.tenantId ?? null : null);
        setAuth(result.authenticated ? result.user : null, result.providers ?? []);
      })
      .catch(() => { if (live) setAuth(null, []); });
    return () => { live = false; };
  }, [setAuth]);
  return <UploadProvider><div className={styles.shell}>
    <a className={styles.skipLink} href="#main-content">Skip to workspace</a>
    <AppHeader title={<RouterLink to="/" className={styles.title}>Laplace</RouterLink>} tagline="witnessed consensus, not weights"
      nav={<NavTabs tabs={TABS.map((tab) => ({ id: tab.id, label: tab.label, href: tab.path, active: isActive(location.pathname, tab.path), onClick: () => navigate(tab.path), onIntent: () => { void WORKSPACE_PREFETCH[tab.id]?.(); } }))} />}
      tenant={authReady && (authUser || authProviders.length > 0)
        ? <AccountControls user={authUser} providers={authProviders} returnUrl={`${location.pathname}${location.search}${location.hash}`} />
        : <TenantField value={tenant} onChange={setTenant} />} />
    <SubstrateStatusBanner />
    <DataActivity />
    <main id="main-content" tabIndex={-1} className={styles.main}>
      <ViewErrorBoundary resetKey={JSON.stringify([viewScope, location.key])}>
        <Suspense fallback={<LoadingText>Loading workspace…</LoadingText>}>
        <Routes key={viewScope}>
          <Route path="/" element={<HomeView onGoto={(tab) => navigate(`/${tab}`)} />} />
          <Route path="/chat" element={<ChatView />} />
          <Route path="/query" element={<QueryConsole />} />
          <Route path="/data" element={<DataView />} />
          <Route path="/topic" element={<TopicView />} />
          <Route path="/topic/:ref" element={<TopicView />} />
          <Route path="/explore/*" element={<ExploreView />} />
          <Route path="/proof" element={<StorageProofView />} />
          <Route path="/forward-proof" element={<ForwardProofView />} />
          <Route path="/unicode" element={<UnicodeGlomeView />} />
          <Route path="/chess/*" element={<ChessDbView />} />
          <Route path="/play" element={<ChessView />} />
          <Route path="/lab/*" element={<LabView />} />
          <Route path="/billing" element={<BillingView />} />
          <Route path="/billing/success" element={<BillingReturnView />} />
          <Route path="/billing/cancel" element={<BillingReturnView />} />
          <Route path="/settings" element={<SettingsView />} />
          <Route path="/operator" element={<AdminView />} />
          <Route path="*" element={<Panel title="Workspace not found"><p>This address does not match a Laplace workspace. Use the navigation above or return Home.</p><RouterLink to="/">Return Home</RouterLink></Panel>} />
        </Routes>
        </Suspense>
      </ViewErrorBoundary>
    </main>
    <AmbientFamiliar />
  </div></UploadProvider>;
}
export function App() { return <BrowserRouter><Shell /></BrowserRouter>; }
